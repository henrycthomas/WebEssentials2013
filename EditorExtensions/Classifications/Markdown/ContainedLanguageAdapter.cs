﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Html.Editor;
using Microsoft.Html.Editor.Projection;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Html.ContainedLanguage;
using Microsoft.VisualStudio.Html.Editor;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using Microsoft.VisualStudio.Web.Editor;
using Microsoft.VisualStudio.Web.Editor.Workspace;
using Microsoft.Web.Editor;
using Microsoft.Web.Editor.ContainedLanguage;
using VSConstants = Microsoft.VisualStudio.VSConstants;


namespace MadsKristensen.EditorExtensions.Classifications.Markdown
{
    // Abandon all hope ye who enters here.
    // https://twitter.com/Schabse/status/393092191356076032
    // https://twitter.com/jasonmalinowski/status/393094145398407168

    // Based on decompiled code from Microsoft.VisualStudio.Html.ContainedLanguage.Server
    // Thanks to Jason Malinowski for helping me navigate this mess.
    // All of this can go away when the Roslyn editor ships.


    class ContainedLanguageAdapter
    {
        public static string ExtensionFromContentType(IContentType contentType)
        {
            IFileExtensionRegistryService value = WebEditor.ExportProvider.GetExport<IFileExtensionRegistryService>().Value;
            return value.GetExtensionsForContentType(contentType).FirstOrDefault();
        }
        public static Guid LanguageServiceFromContentType(IContentType contentType)
        {
            string extension = ExtensionFromContentType(contentType);
            if (extension == null)
                return Guid.Empty;

            Guid retVal;
            IVsTextManager globalService = Globals.GetGlobalService<IVsTextManager>(typeof(SVsTextManager));
            globalService.MapFilenameToLanguageSID("file." + extension, out retVal);
            return retVal;
        }

        public static ContainedLanguageAdapter ForBuffer(ITextBuffer textBuffer)
        {
            var retVal = ServiceManager.GetService<ContainedLanguageAdapter>(textBuffer);
            if (retVal == null)
                retVal = new ContainedLanguageAdapter(textBuffer);
            return retVal;
        }

        public HtmlEditorDocument Document { get; private set; }
        IVsWebWorkspaceItem WorkspaceItem { get { return (IVsWebWorkspaceItem)Document.WorkspaceItem; } }
        readonly Dictionary<IContentType, LanguageBridge> languageBridges = new Dictionary<IContentType, LanguageBridge>();

        public ContainedLanguageAdapter(ITextBuffer textBuffer)
        {
            Document = ServiceManager.GetService<HtmlEditorDocument>(textBuffer);
            Document.OnDocumentClosing += this.OnDocumentClosing;

            ServiceManager.AddService(this, textBuffer);
        }

        class LanguageBridge
        {
            public LanguageProjectionBuffer ProjectionBuffer { get; private set; }

            readonly ContainedLanguageAdapter owner;
            readonly IVsContainedLanguageFactory languageFactory;

            private IVsContainedLanguage containedLanguage;
            private IContainedLanguageHostVs containedLanguage2;

            private IVsContainedLanguageHost _containedLanguageHost;
            private IVsTextBufferCoordinator _textBufferCoordinator;
            private IVsTextLines _secondaryBuffer;
            private LegacyContainedLanguageCommandTarget _legacyCommandTarget;

            public LanguageBridge(ContainedLanguageAdapter owner, LanguageProjectionBuffer projectionBuffer, IVsContainedLanguageFactory languageFactory)
            {
                this.owner = owner;
                this.languageFactory = languageFactory;
                this.ProjectionBuffer = projectionBuffer;
                InitContainedLanguage();
            }

            public IVsContainedLanguageHost GetLegacyContainedLanguageHost()
            {
                if (this._containedLanguageHost == null)
                    this._containedLanguageHost = new VsLegacyContainedLanguageHost(owner.Document, ProjectionBuffer);
                return this._containedLanguageHost;
            }
            private void InitContainedLanguage()
            {
                IVsTextLines vsTextLines = this.EnsureBufferCoordinator();
                IVsContainedLanguage vsContainedLanguage;

                int hr = languageFactory.GetLanguage(owner.WorkspaceItem.Hierarchy, (uint)owner.WorkspaceItem.ItemId, this._textBufferCoordinator, out vsContainedLanguage);
                if (vsContainedLanguage == null)
                {
                    Logger.Log("Markdown: Couldn't get IVsContainedLanguage for " + ProjectionBuffer.IProjectionBuffer.ContentType);
                    return;
                }

                Guid langService;
                vsContainedLanguage.GetLanguageServiceID(out langService);
                vsTextLines.SetLanguageServiceID(ref langService);

                containedLanguage = vsContainedLanguage;
                IVsContainedLanguageHost legacyContainedLanguageHost = this.GetLegacyContainedLanguageHost();
                vsContainedLanguage.SetHost(legacyContainedLanguageHost);
                this._legacyCommandTarget = new LegacyContainedLanguageCommandTarget();

                IVsTextViewFilter textViewFilter;
                this._legacyCommandTarget.Create(owner.Document, vsContainedLanguage, this._textBufferCoordinator, ProjectionBuffer, out textViewFilter);
                IWebContainedLanguageHost webContainedLanguageHost = legacyContainedLanguageHost as IWebContainedLanguageHost;
                webContainedLanguageHost.SetContainedCommandTarget(this._legacyCommandTarget.TextView, this._legacyCommandTarget.ContainedLanguageTarget);
                containedLanguage2 = (webContainedLanguageHost as IContainedLanguageHostVs);
                containedLanguage2.TextViewFilter = textViewFilter;

                ProjectionBuffer.ResetMappings();

                WebEditor.TraceEvent(1005);
            }

            private IVsTextLines EnsureBufferCoordinator()
            {
                if (this._secondaryBuffer != null)
                    return this._secondaryBuffer;


                var vsTextBuffer = owner.Document.TextBuffer.QueryInterface<IVsTextBuffer>();

                IVsEditorAdaptersFactoryService adapterFactory = WebEditor.ExportProvider.GetExport<IVsEditorAdaptersFactoryService>().Value;
                this._secondaryBuffer = (adapterFactory.GetBufferAdapter(ProjectionBuffer.IProjectionBuffer) as IVsTextLines);
                if (this._secondaryBuffer == null)
                {
                    this._secondaryBuffer = (adapterFactory.CreateVsTextBufferAdapterForSecondaryBuffer(vsTextBuffer.GetServiceProvider(), ProjectionBuffer.IProjectionBuffer) as IVsTextLines);
                }

                this._secondaryBuffer.SetTextBufferData(VSConstants.VsTextBufferUserDataGuid.VsBufferDetectLangSID_guid, false);
                this._secondaryBuffer.SetTextBufferData(VSConstants.VsTextBufferUserDataGuid.VsBufferMoniker_guid, owner.WorkspaceItem.PhysicalPath);

                IOleUndoManager oleUndoManager;
                this._secondaryBuffer.GetUndoManager(out oleUndoManager);
                oleUndoManager.Enable(0);

                this._textBufferCoordinator = adapterFactory.CreateVsTextBufferCoordinatorAdapter();
                vsTextBuffer.SetTextBufferData(HtmlConstants.SID_SBufferCoordinatorServerLanguage, this._textBufferCoordinator);
                vsTextBuffer.SetTextBufferData(typeof(VsTextBufferCoordinatorClass).GUID, this._textBufferCoordinator);

                this._textBufferCoordinator.SetBuffers(vsTextBuffer as IVsTextLines, this._secondaryBuffer);

                return this._secondaryBuffer;
            }
            public void ClearBufferCoordinator()
            {
                IVsTextBuffer vsTextBuffer = owner.Document.TextBuffer.QueryInterface<IVsTextBuffer>();
                vsTextBuffer.SetTextBufferData(HtmlConstants.SID_SBufferCoordinatorServerLanguage, null);
                vsTextBuffer.SetTextBufferData(typeof(VsTextBufferCoordinatorClass).GUID, null);
            }


            public void Terminate()
            {
                if (this._legacyCommandTarget != null && this._legacyCommandTarget.TextView != null)
                    containedLanguage2.RemoveContainedCommandTarget(this._legacyCommandTarget.TextView);
                containedLanguage2.ContainedLanguageDebugInfo = null;
                containedLanguage2.TextViewFilter = null;

                if (this._legacyCommandTarget != null)
                {
                    this._legacyCommandTarget.Dispose();
                    this._legacyCommandTarget = null;
                }
                containedLanguage.SetHost(null);

                this._textBufferCoordinator = null;
                this._containedLanguageHost = null;
                if (this._secondaryBuffer != null)
                {
                    (this._secondaryBuffer as IVsPersistDocData).Close();
                    this._secondaryBuffer = null;
                }
            }
        }


        IWebApplicationCtxSvc WebApplicationContextService
        {
            get { return ServiceProvider.GlobalProvider.GetService(typeof(SWebApplicationCtxSvc)) as IWebApplicationCtxSvc; }
        }

        #region IVsIntellisenseProjectManager management
        private class IntellisenseProjectWrapper : IVsIntellisenseProjectEventSink
        {
            public IContentType ContentType { get; private set; }
            public IVsIntellisenseProjectManager Manager { get; private set; }
            readonly ContainedLanguageAdapter owner;

            uint cookie;
            INTELLIPROJSTATUS status;
            bool isReady;

            public IntellisenseProjectWrapper(ContainedLanguageAdapter owner, IContentType contentType)
            {
                this.owner = owner;
                this.ContentType = contentType;
            }

            public void CreateWhenIdle()
            {
                EventHandler<EventArgs> idleHandler = null;
                idleHandler = delegate
                {
                    WebEditor.OnIdle -= idleHandler;
                    Create();
                };
                WebEditor.OnIdle += idleHandler;
            }

            public void Create()
            {
                if (owner.WorkspaceItem.Hierarchy == null || cookie != 0u)
                {
                    Logger.Log("Markdown: Not creating IVsIntellisenseProjectManager; no WorkspaceItem.Hierarchy");
                    return;
                }

                Microsoft.VisualStudio.OLE.Interop.IServiceProvider sp;
                owner.WebApplicationContextService.GetItemContext(owner.WorkspaceItem.Hierarchy, (uint)owner.WorkspaceItem.ItemId, out sp);
                ServiceProvider serviceProvider = new ServiceProvider(sp);

                object o;
                serviceProvider.QueryService(typeof(SVsIntellisenseProjectManager).GUID, out o);
                Manager = (o as IVsIntellisenseProjectManager);
                if (Manager == null)
                    throw new InvalidOperationException("ServiceProvider didn't return SVsIntellisenseProjectManager");
                Manager.AdviseIntellisenseProjectEvents(this, out cookie);
            }

            public void Dispose()
            {
                if (Manager == null)
                    return;
                if (cookie != 0u)
                {
                    Manager.UnadviseIntellisenseProjectEvents(cookie);
                    cookie = 0u;
                }
                LanguageBridge bridge;
                if (owner.languageBridges.TryGetValue(ContentType, out bridge))
                {
                    bridge.Terminate();
                    owner.languageBridges.Remove(ContentType);
                }
                Manager.CloseIntellisenseProject();
                Manager = null;
            }

            ///<summary>Occurs when the IntelliSense project is ready to intialize contained languages.</summary>
            public event EventHandler LoadComplete;
            ///<summary>Raises the LoadComplete event.</summary>
            protected virtual void OnLoadComplete() { OnLoadComplete(EventArgs.Empty); }
            ///<summary>Raises the LoadComplete event.</summary>
            ///<param name="e">An EventArgs object that provides the event data.</param>
            protected virtual void OnLoadComplete(EventArgs e)
            {
                if (LoadComplete != null)
                    LoadComplete(this, e);
            }


            int IVsIntellisenseProjectEventSink.OnCodeFileChange(string pszOldCodeFile, string pszNewCodeFile) { return 0; }
            int IVsIntellisenseProjectEventSink.OnConfigChange() { return 0; }
            int IVsIntellisenseProjectEventSink.OnReferenceChange(uint dwChangeType, string pszAssemblyPath) { return 0; }
            int IVsIntellisenseProjectEventSink.OnStatusChange(uint dwStatus)
            {
                switch (dwStatus)
                {
                    case 1u:
                        status = (INTELLIPROJSTATUS)dwStatus;
                        if (!HtmlUtilities.IsSolutionLoading(ServiceProvider.GlobalProvider))
                            this.EnsureIntellisenseProjectLoaded();
                        break;
                    case 2u:
                        status = (INTELLIPROJSTATUS)dwStatus;
                        OnLoadComplete();
                        isReady = true;
                        this.NotifyEditorReady();
                        break;
                    case 3u:
                        this.Reset(false);
                        status = (INTELLIPROJSTATUS)dwStatus;
                        break;
                    case 4u:
                        this.EnsureIntellisenseProjectLoaded();
                        this.NotifyEditorReady();
                        break;
                }
                return 0;
            }
            internal void Reset(bool createIntellisenseProject)
            {
                this.Dispose();
                if (createIntellisenseProject)
                {
                    this.Create();
                }
            }
            internal void NotifyEditorReady()
            {
                if (!isReady)
                    return;

                if (!HtmlUtilities.IsSolutionLoading(ServiceProvider.GlobalProvider))
                    this.EnsureIntellisenseProjectLoaded();

                Manager.OnEditorReady();
                isReady = false;
            }
            private void EnsureIntellisenseProjectLoaded()
            {
                if (status == INTELLIPROJSTATUS.INTELLIPROJSTATUS_LOADING)
                {
                    Manager.CompleteIntellisenseProjectLoad();
                }
            }
        }
        #endregion

        private IntellisenseProjectWrapper intellisenseProject;

        ///<summary>Creates a ContainedLanguage for the specified ProjectionBuffer, using an IVsIntellisenseProjectManager to intialize the language.</summary>
        ///<param name="projectionBuffer">The buffer to connect to the language service.</param>
        ///<param name="intelliSenseGuid">The GUID of the IntellisenseProvider; used to create IVsIntellisenseProject.</param>
        public void AddIntellisenseProjectLanguage(LanguageProjectionBuffer projectionBuffer, Guid intelliSenseGuid)
        {
            var contentType = projectionBuffer.IProjectionBuffer.ContentType;
            if (languageBridges.ContainsKey(contentType))
                return;

            Guid iid_vsip = typeof(IVsIntellisenseProject).GUID;

            var project = (IVsIntellisenseProject)EditorExtensionsPackage.Instance.CreateInstance(ref intelliSenseGuid, ref iid_vsip, typeof(IVsIntellisenseProject));

            string projectPath;
            WorkspaceItem.FileItemContext.GetWebRootPath(out projectPath);

            project.Init(new ProjectHost(WorkspaceItem.Hierarchy, projectPath));
            project.StartIntellisenseEngine();
            project.WaitForIntellisenseReady();
            project.ResumePostedNotifications();
            //project.AddAssemblyReference(typeof(Enumerable).Assembly.Location);

            int needsFile;
            project.IsWebFileRequiredByProject(out needsFile);
            if (needsFile != 0)
                project.AddFile(projectionBuffer.IProjectionBuffer.GetFileName(), (uint)WorkspaceItem.ItemId);

            project.WaitForIntellisenseReady();
            IVsContainedLanguageFactory factory;
            project.GetContainedLanguageFactory(out factory);
            if (factory == null)
            {
                Logger.Log("Markdown: Couldn't create IVsContainedLanguageFactory for " + contentType);
                project.Close();
                return;
            }
            // TODO: IVsIntellisenseProject.Close();
            languageBridges.Add(contentType, new LanguageBridge(this, projectionBuffer, factory));
        }

        class ProjectHost : IVsIntellisenseProjectHost
        {
            readonly IVsHierarchy hierarchy;
            readonly string projectPath;
            public ProjectHost(IVsHierarchy hierarchy, string projectPath)
            {
                this.hierarchy = hierarchy;
                this.projectPath = projectPath;
            }

            public int CreateFileCodeModel(string pszFilename, out object ppCodeModel)
            {
                ppCodeModel = null;
                return VSConstants.E_NOTIMPL;
            }

            public int GetCompilerOptions(out string pbstrOptions)
            {
                pbstrOptions = "";
                return 0;
            }

            public int GetHostProperty(uint dwPropID, out object pvar)
            {
                var prop = (HOSTPROPID)dwPropID;
                // Based on decompiled Microsoft.VisualStudio.ProjectSystem.VS.Implementation.Designers.LanguageServiceBase
                switch (prop)
                {
                    case HOSTPROPID.HOSTPROPID_PROJECTNAME:
                    case HOSTPROPID.HOSTPROPID_RELURL:
                        pvar = projectPath;
                        return VSConstants.S_OK;
                    case HOSTPROPID.HOSTPROPID_HIERARCHY:
                        pvar = hierarchy;
                        return VSConstants.S_OK;
                    case HOSTPROPID.HOSTPROPID_INTELLISENSECACHE_FILENAME:
                        pvar = Path.GetTempPath();
                        return VSConstants.S_OK;
                    case (HOSTPROPID)(-2):
                        pvar = ".NET Framework, Version=4.5";   // configurationGeneral.TargetFrameworkMoniker.GetEvaluatedValueAtEndAsync
                        return VSConstants.S_OK;
                    case (HOSTPROPID)(-1):
                        pvar = false;   // No clue...
                        return VSConstants.S_OK;

                    default:
                        pvar = null;
                        return VSConstants.E_INVALIDARG;
                }
            }

            public int GetOutputAssembly(out string pbstrOutputAssembly)
            {
                pbstrOutputAssembly = "";
                return 0;
            }
        }

        #region Cleanup
        private void OnDocumentClosing(object sender, EventArgs e)
        {
            foreach (var b in languageBridges.Values)
                b.ClearBufferCoordinator();

            var service = ServiceManager.GetService<ContainedLanguageAdapter>(this.Document.TextBuffer);
            if (service != null)
            {
                ServiceManager.RemoveService<ContainedLanguageAdapter>(this.Document.TextBuffer);
                service.Dispose();
            }
            Document.OnDocumentClosing -= this.OnDocumentClosing;
            Document = null;
        }
        public void Dispose()
        {
            if (intellisenseProject != null)
                intellisenseProject.Dispose();

            foreach (var b in languageBridges.Values)
                b.Terminate();
        }
        #endregion
    }
}
