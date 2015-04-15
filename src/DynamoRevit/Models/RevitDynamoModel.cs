﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Soap;
using System.Windows.Forms;

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

using DSIronPython;

using Dynamo.Interfaces;
using Dynamo.Models;
using Dynamo.UpdateManager;
using Dynamo.Utilities;

using DynamoServices;

using Greg;

using ProtoCore;

using Revit.Elements;

using RevitServices.Elements;
using RevitServices.Materials;
using RevitServices.Persistence;
using RevitServices.Threading;
using RevitServices.Transactions;

using Category = Revit.Elements.Category;
using Element = Autodesk.Revit.DB.Element;
using View = Autodesk.Revit.DB.View;

namespace Dynamo.Applications.Models
{
    public class RevitDynamoModel : DynamoModel
    {
        public interface IRevitStartConfiguration : IStartConfiguration
        {
            ExternalCommandData ExternalCommandData { get; set; }
        }

        public struct RevitStartConfiguration : IRevitStartConfiguration
        {
            public string Context { get; set; }
            public string DynamoCorePath { get; set; }
            public IPathResolver PathResolver { get; set; }
            public IPreferences Preferences { get; set; }
            public bool StartInTestMode { get; set; }
            public IUpdateManager UpdateManager { get; set; }
            public ISchedulerThread SchedulerThread { get; set; }
            public string GeometryFactoryPath { get; set; }
            public IAuthProvider AuthProvider { get; set; }
            public string PackageManagerAddress { get; set; }
            public ExternalCommandData ExternalCommandData { get; set; }
        }

        /// <summary>
        ///     Flag for syncing up document switches between Application.DocumentClosing and
        ///     Application.DocumentClosed events.
        /// </summary>
        private bool updateCurrentUIDoc;

        private readonly ExternalCommandData externalCommandData;

        #region Events

        /// <summary>
        /// Event triggered when the current Revit document is changed.
        /// </summary>
        public event EventHandler RevitDocumentChanged;

        public virtual void OnRevitDocumentChanged()
        {
            if (RevitDocumentChanged != null)
                RevitDocumentChanged(this, EventArgs.Empty);
        }

        /// <summary>
        /// Event triggered when the Revit document that Dynamo had 
        /// previously been pointing at has been closed.
        /// </summary>
        public event Action RevitDocumentLost;

        private void OnRevitDocumentLost()
        {
            var handler = RevitDocumentLost;
            if (handler != null) handler();
        }

        /// <summary>
        /// Event triggered when Revit enters a context 
        /// where external applications are not allowed.
        /// </summary>
        public event Action RevitContextUnavailable;

        private void OnRevitContextUnavailable()
        {
            var handler = RevitContextUnavailable;
            if (handler != null) handler();
        }

        /// <summary>
        /// Event triggered when Revit enters a context where
        /// external applications are allowed.
        /// </summary>
        public event Action RevitContextAvailable;

        private void OnRevitContextAvailable()
        {
            var handler = RevitContextAvailable;
            if (handler != null) handler();
        }

        /// <summary>
        /// Event triggered when the active Revit view changes.
        /// </summary>
        public event Action<View> RevitViewChanged;

        private void OnRevitViewChanged(View newView)
        {
            var handler = RevitViewChanged;
            if (handler != null) handler(newView);
        }

        /// <summary>
        /// Event triggered when a document other than the
        /// one Dynamo is pointing at becomes active.
        /// </summary>
        public event Action InvalidRevitDocumentActivated;

        private void OnInvalidRevitDocumentActivated()
        {
            var handler = InvalidRevitDocumentActivated;
            if (handler != null) handler();
        }

        #endregion

        #region Properties/Fields
        override internal string AppVersion
        {
            get
            {
                return base.AppVersion +
                    "-R" + DocumentManager.Instance.CurrentUIApplication.Application.VersionBuild;
            }
        }

        #endregion

        #region Constructors

        public new static RevitDynamoModel Start()
        {
            return Start(new RevitStartConfiguration());
        }

        public new static RevitDynamoModel Start(IRevitStartConfiguration configuration)
        {
            // where necessary, assign defaults
            if (string.IsNullOrEmpty(configuration.Context))
                configuration.Context = Core.Context.REVIT_2015;

            return new RevitDynamoModel(configuration);
        }

        private RevitDynamoModel(IRevitStartConfiguration configuration) :
            base(configuration)
        {
            externalCommandData = configuration.ExternalCommandData;

            RevitServicesUpdater.Initialize(DynamoRevitApp.ControlledApplication, DynamoRevitApp.Updaters);

            SubscribeRevitServicesUpdaterEvents();

            SubscribeApplicationEvents(configuration.ExternalCommandData);
            InitializeDocumentManager();
            SubscribeDocumentManagerEvents();
            SubscribeTransactionManagerEvents();

            MigrationManager.MigrationTargets.Add(typeof(WorkspaceMigrationsRevit));

            SetupPython();

            WorkspaceEvents.WorkspaceAdded += WorkspaceEvents_WorkspaceAdded;
        }

        /// <summary>
        /// A map of historical element ids associated with a workspace.
        /// </summary>
        private Dictionary<Guid, Dictionary<Guid, List<int>>> historicalElementData = new Dictionary<Guid, Dictionary<Guid, List<int>>>();

        private bool isFirstEvaluation = true;

        void WorkspaceEvents_WorkspaceAdded(WorkspacesModificationEventArgs args)
        {
            // The workspace's PreloadedTraceData should be available here
            // before the workspace has been run. We use this trace data to
            // get a dictionary, keyed by node guid, of element ids which
            // have been saved to the file. This dictionary will be used only
            // once, after the first run of the graph to reconcile elements that 
            // are newly created with what had been created previously, removing
            // any elements from the model that were pre-existing and associated
            // with Dynamo, but no longer created by Dynamo.
            
            var hws = Workspaces.FirstOrDefault(ws => ws is HomeWorkspaceModel) as HomeWorkspaceModel;
            if (hws == null) return;

            var serializedTraceData = hws.PreloadedTraceData;
            if (serializedTraceData == null) return;

            historicalElementData.Add(args.Id, new Dictionary<Guid, List<int>>());

            foreach (var kvp in serializedTraceData)
            {
                var idList = new List<int>();

                historicalElementData[args.Id].Add(kvp.Key, idList);

                foreach (var serializables in kvp.Value.Select(CallSite.GetAllSerializablesFromSingleRunTraceData)) 
                {
                    idList.AddRange(serializables.Select(ser => ((SerializableId)ser).IntID));
                }
            }
        }

        #endregion

        #region Initialization

        /// <summary>
        /// This call is made during start-up sequence after RevitDynamoModel 
        /// constructor returned. Virtual methods on DynamoModel that perform 
        /// initialization steps should only be called from here.
        /// </summary>
        internal void HandlePostInitialization()
        {
            InitializeMaterials(); // Initialize materials for preview.
        }

        private bool setupPython;

        private void SetupPython()
        {
            if (setupPython) return;

            IronPythonEvaluator.OutputMarshaler.RegisterMarshaler(
                (Element element) => element.ToDSType(true));

            // Turn off element binding during iron python script execution
            IronPythonEvaluator.EvaluationBegin +=
                (a, b, c, d, e) => ElementBinder.IsEnabled = false;
            IronPythonEvaluator.EvaluationEnd += (a, b, c, d, e) => ElementBinder.IsEnabled = true;

            // register UnwrapElement method in ironpython
            IronPythonEvaluator.EvaluationBegin += (a, b, scope, d, e) =>
            {
                var marshaler = new DataMarshaler();
                marshaler.RegisterMarshaler(
                    (Revit.Elements.Element element) => element.InternalElement);
                marshaler.RegisterMarshaler((Category element) => element.InternalCategory);

                Func<object, object> unwrap = marshaler.Marshal;
                scope.SetVariable("UnwrapElement", unwrap);
            };

            setupPython = true;
        }

        private void InitializeDocumentManager()
        {
            // Set the intitial document.
            if (DocumentManager.Instance.CurrentUIApplication.ActiveUIDocument != null)
            {
                DocumentManager.Instance.CurrentUIDocument =
                    DocumentManager.Instance.CurrentUIApplication.ActiveUIDocument;

                OnRevitDocumentChanged();
            }
        }

        private static void InitializeMaterials()
        {
            // Ensure that the current document has the needed materials
            // and graphic styles to support visualization in Revit.
            var mgr = MaterialsManager.Instance;
            IdlePromise.ExecuteOnIdleAsync(mgr.InitializeForActiveDocumentOnIdle);
        }

        #endregion

        #region Event subscribe/unsubscribe

        private void SubscribeRevitServicesUpdaterEvents()
        {
            RevitServicesUpdater.Instance.ElementsDeleted += RevitServicesUpdater_ElementsDeleted;
            RevitServicesUpdater.Instance.ElementsModified += RevitServicesUpdater_ElementsModified;
        }

        private void UnsubscribeRevitServicesUpdaterEvents()
        {
            RevitServicesUpdater.Instance.ElementsDeleted -= RevitServicesUpdater_ElementsDeleted;
            RevitServicesUpdater.Instance.ElementsModified -= RevitServicesUpdater_ElementsModified;
        }

        private void SubscribeTransactionManagerEvents()
        {
            TransactionManager.Instance.TransactionWrapper.FailuresRaised +=
                TransactionManager_FailuresRaised;
        }

        private void UnsubscribeTransactionManagerEvents()
        {
            TransactionManager.Instance.TransactionWrapper.FailuresRaised -=
                TransactionManager_FailuresRaised;
        }

        private void SubscribeDocumentManagerEvents()
        {
            DocumentManager.OnLogError += Logger.Log;
        }

        private void UnsubscribeDocumentManagerEvents()
        {
            DocumentManager.OnLogError -= Logger.Log;
        }

        private bool hasRegisteredApplicationEvents;
        private void SubscribeApplicationEvents(ExternalCommandData commandData)
        {
            if (hasRegisteredApplicationEvents)
            {
                return;
            }

            commandData.Application.ViewActivating += OnApplicationViewActivating;
            commandData.Application.ViewActivated += OnApplicationViewActivated;

            commandData.Application.Application.DocumentClosing += OnApplicationDocumentClosing;
            commandData.Application.Application.DocumentClosed += OnApplicationDocumentClosed;
            commandData.Application.Application.DocumentOpened += OnApplicationDocumentOpened;

            hasRegisteredApplicationEvents = true;
        }

        private void UnsubscribeApplicationEvents(ExternalCommandData commandData)
        {
            if (!hasRegisteredApplicationEvents)
            {
                return;
            }

            commandData.Application.ViewActivating -= OnApplicationViewActivating;
            commandData.Application.ViewActivated -= OnApplicationViewActivated;

            commandData.Application.Application.DocumentClosing -= OnApplicationDocumentClosing;
            commandData.Application.Application.DocumentClosed -= OnApplicationDocumentClosed;
            commandData.Application.Application.DocumentOpened -= OnApplicationDocumentOpened;

            hasRegisteredApplicationEvents = false;
        }

        #endregion

        #region Application event handler
        /// <summary>
        /// Handler for Revit's DocumentOpened event.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void OnApplicationDocumentOpened(object sender, Autodesk.Revit.DB.Events.DocumentOpenedEventArgs e)
        {
            HandleApplicationDocumentOpened();
        }

        /// <summary>
        /// Handler for Revit's DocumentClosing event.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void OnApplicationDocumentClosing(object sender, Autodesk.Revit.DB.Events.DocumentClosingEventArgs e)
        {
            HandleApplicationDocumentClosing(e.Document);
        }

        /// <summary>
        /// Handler for Revit's DocumentClosed event.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void OnApplicationDocumentClosed(object sender, Autodesk.Revit.DB.Events.DocumentClosedEventArgs e)
        {
            HandleApplicationDocumentClosed();
        }

        /// <summary>
        /// Handler for Revit's ViewActivating event.
        /// Addins are not available in some views in Revit, notably perspective views.
        /// This will present a warning that Dynamo is not available to run and disable the run button.
        /// This handler is called before the ViewActivated event registered on the RevitDynamoModel.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        internal void OnApplicationViewActivating(object sender, ViewActivatingEventArgs e)
        {
            SetRunEnabledBasedOnContext(e.NewActiveView);
        }

        /// <summary>
        /// Handler for Revit's ViewActivated event.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void OnApplicationViewActivated(object sender, ViewActivatedEventArgs e)
        {
            HandleRevitViewActivated();
        }

        #endregion

        #region Public methods

        public override void OnEvaluationCompleted(object sender, EvaluationCompletedEventArgs e)
        {
            Debug.WriteLine(ElementIDLifecycleManager<int>.GetInstance());

            if (historicalElementData.ContainsKey(CurrentWorkspace.Guid))
            {
                ReconcileHistoricalElements();
            }

            // finally close the transaction!
            TransactionManager.Instance.ForceCloseTransaction();

            base.OnEvaluationCompleted(sender, e);
        }

        private void ReconcileHistoricalElements()
        {
            // Read the current run's trace data into a dictionary which matches
            // the layout of our historical element data. 

            var traceData = EngineController.LiveRunnerCore.RuntimeData.GetTraceDataForNodes(
                CurrentWorkspace.Nodes.Select(n => n.GUID),
                EngineController.LiveRunnerCore.DSExecutable);

            var currentRunData = new Dictionary<Guid, List<int>>();
            foreach (var kvp in traceData)
            {
                var idList = new List<int>();
                currentRunData.Add(kvp.Key, idList);

                foreach (var serializables in kvp.Value.Select(CallSite.GetAllSerializablesFromSingleRunTraceData))
                {
                    idList.AddRange(serializables.Select(ser => ((SerializableId)ser).IntID));
                }
            }

            // Compare the historical element data and the current run
            // data to see whether there are elements that had been
            // created by nodes previously, which were not created in 
            // this run. Store the elements ids of all the elements that
            // can't be found in the orphans collection.

            var workspaceHistory = historicalElementData[CurrentWorkspace.Guid];
            var orphanedIds = new List<int>();

            foreach (var kvp in workspaceHistory)
            {
                var nodeGuid = kvp.Key;

                // If the current run doesn't have a key for 
                // this guid, then all of these elements are 
                // orphaned.

                if (!currentRunData.ContainsKey(nodeGuid))
                {
                    orphanedIds.AddRange(kvp.Value);
                    continue;
                }

                var currentRunNodeGuids = currentRunData[nodeGuid];

                // If the current run didn't create an element
                // in the current run, then add an orphan.

                orphanedIds.AddRange(kvp.Value.Where(id => !currentRunNodeGuids.Contains(id)));
            }

            if (IsTestMode)
            {
                DeleteOrphanedElements(orphanedIds);
            }
            else
            {
                // Delete all the orphans.
                IdlePromise.ExecuteOnIdleAsync(
                    () =>
                    {
                        DeleteOrphanedElements(orphanedIds);
                    });
            }

            Debug.WriteLine("ELEMENT RECONCILIATION: {0} elements were orphaned.", orphanedIds.Count);

            // When reconciliation is complete, wipe the historical data.
            // At this point, element binding is as up to date as it's going to get.

            historicalElementData.Remove(CurrentWorkspace.Guid);
        }

        private static void DeleteOrphanedElements(List<int> orphanedIds)
        {
            var toDelete = new List<ElementId>();
            foreach (var id in orphanedIds)
            {
                // Check whether the element is valid before attempting to delete.
                Element el;
                if (DocumentManager.Instance.CurrentDBDocument.TryGetElement(new ElementId(id), out el))
                    toDelete.Add(el.Id);
            }

            using (var trans = new SubTransaction(DocumentManager.Instance.CurrentDBDocument))
            {
                trans.Start();
                DocumentManager.Instance.CurrentDBDocument.Delete(toDelete);
                trans.Commit();
            }
        }

        protected override void PreShutdownCore(bool shutdownHost)
        {
            if (shutdownHost)
            {
                var uiApplication = DocumentManager.Instance.CurrentUIApplication;
                uiApplication.Idling += ShutdownRevitHostOnce;
            }

            base.PreShutdownCore(shutdownHost);
        }

        private static void ShutdownRevitHostOnce(object sender, IdlingEventArgs idlingEventArgs)
        {
            var uiApplication = DocumentManager.Instance.CurrentUIApplication;
            uiApplication.Idling -= ShutdownRevitHostOnce;
            ShutdownRevitHost();
        }

        protected override void ShutDownCore(bool shutDownHost)
        {
            DisposeLogic.IsShuttingDown = true;

            base.ShutDownCore(shutDownHost);

            // unsubscribe events
            RevitServicesUpdater.Instance.UnRegisterAllChangeHooks();

            UnsubscribeApplicationEvents(externalCommandData);
            UnsubscribeDocumentManagerEvents();
            UnsubscribeRevitServicesUpdaterEvents();
            UnsubscribeTransactionManagerEvents();

            RevitServicesUpdater.DisposeInstance();
            ElementIDLifecycleManager<int>.DisposeInstance();
        }

        /// <summary>
        /// This event handler is called if 'markNodesAsDirty' in a 
        /// prior call to RevitDynamoModel.ResetEngine was set to 'true'.
        /// </summary>
        /// <param name="markNodesAsDirty"></param>
        private void OnResetMarkNodesAsDirty(bool markNodesAsDirty)
        {
            foreach (var workspace in Workspaces.OfType<HomeWorkspaceModel>())
                workspace.ResetEngine(EngineController, markNodesAsDirty);
        }

        public void SetRunEnabledBasedOnContext(View newView)
        {
            var view = newView as View3D;

            if (view != null && view.IsPerspective
                && Context != Core.Context.VASARI_2014)
            {
                OnRevitContextUnavailable();

                foreach (
                    var ws in Workspaces.OfType<HomeWorkspaceModel>())
                {
                    ws.RunSettings.RunEnabled = false;
                }
            }
            else
            {
                Logger.Log(
                    string.Format("Active view is now {0}", newView.Name));

                // If there is a current document, then set the run enabled
                // state based on whether the view just activated is 
                // the same document.
                if (DocumentManager.Instance.CurrentUIDocument != null)
                {
                    var newEnabled =
                        newView.Document.Equals(DocumentManager.Instance.CurrentDBDocument);

                    if (!newEnabled)
                    {
                        OnInvalidRevitDocumentActivated();
                    }

                    foreach (HomeWorkspaceModel ws in Workspaces.OfType<HomeWorkspaceModel>())
                    {
                        ws.RunSettings.RunEnabled = newEnabled;
                    }
                }
            }
        }

        #endregion

        #region Event handlers

        /// <summary>
        /// Handler Revit's DocumentOpened event.
        /// It is called when a document is opened, but NOT when a document is 
        /// created from a template.
        /// </summary>
        private void HandleApplicationDocumentOpened()
        {
            // If the current document is null, for instance if there are
            // no documents open, then set the current document, and 
            // present a message telling us where Dynamo is pointing.
            if (DocumentManager.Instance.CurrentUIDocument == null)
            {
                DocumentManager.Instance.CurrentUIDocument =
                    DocumentManager.Instance.CurrentUIApplication.ActiveUIDocument;
                OnRevitDocumentChanged();

                foreach (HomeWorkspaceModel ws in Workspaces.OfType<HomeWorkspaceModel>())
                {
                    ws.RunSettings.RunEnabled = true;
                }

                ResetForNewDocument();
            }
        }

        /// <summary>
        /// Handler Revit's DocumentClosing event.
        /// It is called when a document is closing.
        /// </summary>
        private void HandleApplicationDocumentClosing(Document doc)
        {
            // ReSharper disable once PossibleUnintendedReferenceComparison
            if (DocumentManager.Instance.CurrentDBDocument.Equals(doc))
            {
                updateCurrentUIDoc = true;
            }
        }

        /// <summary>
        /// Handle Revit's DocumentClosed event.
        /// It is called when a document is closed.
        /// </summary>
        private void HandleApplicationDocumentClosed()
        {
            // If the active UI document is null, it means that all views have been 
            // closed from all document. Clear our reference, present a warning,
            // and disable running.
            if (DocumentManager.Instance.CurrentUIApplication.ActiveUIDocument == null)
            {
                DocumentManager.Instance.CurrentUIDocument = null;
                foreach (HomeWorkspaceModel ws in Workspaces.OfType<HomeWorkspaceModel>())
                {
                    ws.RunSettings.RunEnabled = false;
                }

                OnRevitDocumentLost();
            }
            else
            {
                // If Dynamo's active UI document's document is the one that was just closed
                // then set Dynamo's active UI document to whatever revit says is active.
                if (updateCurrentUIDoc)
                {
                    updateCurrentUIDoc = false;
                    DocumentManager.Instance.CurrentUIDocument =
                        DocumentManager.Instance.CurrentUIApplication.ActiveUIDocument;

                    OnRevitDocumentChanged();
                }
            }

            var uiDoc = DocumentManager.Instance.CurrentUIDocument;
            if (uiDoc != null)
            {
                SetRunEnabledBasedOnContext(uiDoc.ActiveView);
            }
        }

        /// <summary>
        /// Handler Revit's ViewActivated event.
        /// It is called when a view is activated. It is called after the 
        /// ViewActivating event.
        /// </summary>
        private void HandleRevitViewActivated()
        {
            // If there is no active document, then set it to whatever
            // document has just been activated
            if (DocumentManager.Instance.CurrentUIDocument == null)
            {
                DocumentManager.Instance.CurrentUIDocument =
                    DocumentManager.Instance.CurrentUIApplication.ActiveUIDocument;

                OnRevitDocumentChanged();

                InitializeMaterials();
                foreach (HomeWorkspaceModel ws in Workspaces.OfType<HomeWorkspaceModel>())
                {
                    ws.RunSettings.RunEnabled = true;
                }
            }
        }

        /// <summary>
        ///     Clears all element collections on nodes and resets the visualization manager and the old value.
        /// </summary>
        private void ResetForNewDocument()
        {
            foreach (var ws in Workspaces.OfType<HomeWorkspaceModel>())
            {
                ws.MarkNodesAsModifiedAndRequestRun(ws.Nodes);
                
                foreach (var node in ws.Nodes)
                {
                    lock (node.RenderPackagesMutex)
                    {
                        node.RenderPackages.Clear();
                    }
                }
            }

            OnRevitDocumentChanged();
        }

        private static void ShutdownRevitHost()
        {
            // this method cannot be called without Revit 2014
            var exitCommand = RevitCommandId.LookupPostableCommandId(PostableCommand.ExitRevit);
            var uiApplication = DocumentManager.Instance.CurrentUIApplication;

            if ((uiApplication != null) && uiApplication.CanPostCommand(exitCommand))
                uiApplication.PostCommand(exitCommand);
            else
            {
                MessageBox.Show(
                    "A command in progress prevented Dynamo from " +
                        "closing revit. Dynamo update will be cancelled.");
            }
        }

        private void TransactionManager_FailuresRaised(FailuresAccessor failuresAccessor)
        {
            IList<FailureMessageAccessor> failList = failuresAccessor.GetFailureMessages();

            IEnumerable<FailureMessageAccessor> query =
                from fail in failList
                where fail.GetSeverity() == FailureSeverity.Warning
                select fail;

            foreach (FailureMessageAccessor fail in query)
            {
                Logger.Log("!! Warning: " + fail.GetDescriptionText());
                failuresAccessor.DeleteWarning(fail);
            }
        }

        private void RevitServicesUpdater_ElementsDeleted(
            Document document, IEnumerable<ElementId> deleted)
        {
            if (!deleted.Any())
                return;

            var nodes = ElementBinder.GetNodesFromElementIds(
                deleted,
                CurrentWorkspace,
                EngineController);
            foreach (var node in nodes)
            {
                node.OnNodeModified(forceExecute: true);
            }
        }

        private void RevitServicesUpdater_ElementsModified(IEnumerable<string> updated)
        {
            var updatedIds = updated.Select(
                x =>
                {
                    Element ret;
                    DocumentManager.Instance.CurrentDBDocument.TryGetElement(x, out ret);
                    return ret;
                }).Select(x => x.Id);

            if (!updatedIds.Any())
                return;

            var nodes = ElementBinder.GetNodesFromElementIds(
                updatedIds,
                CurrentWorkspace,
                EngineController);
            foreach (var node in nodes)
            {
                node.OnNodeModified(true);
            }
        }

        protected override void OpenFileImpl(OpenFileCommand command)
        {
            IdlePromise.ExecuteOnIdleAsync(() => base.OpenFileImpl(command));
        }

        #endregion

    }
}
