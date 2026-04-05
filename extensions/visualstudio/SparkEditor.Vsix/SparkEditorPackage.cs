using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SparkEditor.Vsix
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(SparkEditorToolWindow))]
    [Guid(PackageGuids.SparkEditorPackageString)]
    public sealed class SparkEditorPackage : AsyncPackage, IVsSolutionEvents
    {
        public static IVsOutputWindowPane OutputPane { get; private set; }

        private uint _solutionEventsCookie;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var outputWindow = (IVsOutputWindow)GetGlobalService(typeof(SVsOutputWindow));
            var paneGuid = new Guid("F54B0E74-2A27-4D88-B1ED-8C1A5E3F9D01");
            outputWindow.CreatePane(ref paneGuid, "Spark", 1, 0);
            outputWindow.GetPane(ref paneGuid, out var pane);
            OutputPane = pane;

            // Subscribe to solution events so we can stop the server when the solution closes
            var solution = (IVsSolution)GetGlobalService(typeof(SVsSolution));
            solution.AdviseSolutionEvents(this, out _solutionEventsCookie);

            await OpenSparkEditorCommand.InitializeAsync(this);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Unsubscribe from solution events
                if (_solutionEventsCookie != 0)
                {
                    var solution = (IVsSolution)GetGlobalService(typeof(SVsSolution));
                    solution?.UnadviseSolutionEvents(_solutionEventsCookie);
                    _solutionEventsCookie = 0;
                }

                // Kill the SparkEditor server process when VS shuts down
                StopEditorServer();
            }

            base.Dispose(disposing);
        }

        private void StopEditorServer()
        {
            var window = FindToolWindow(typeof(SparkEditorToolWindow), 0, false);
            if (window?.Content is SparkEditorToolWindowControl control)
            {
                control.StopServer();
            }
        }

        #region IVsSolutionEvents

        public int OnBeforeCloseSolution(object pUnkReserved)
        {
            StopEditorServer();
            return Microsoft.VisualStudio.VSConstants.S_OK;
        }

        public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded) => Microsoft.VisualStudio.VSConstants.S_OK;
        public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => Microsoft.VisualStudio.VSConstants.S_OK;
        public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) => Microsoft.VisualStudio.VSConstants.S_OK;
        public int OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy) => Microsoft.VisualStudio.VSConstants.S_OK;
        public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) => Microsoft.VisualStudio.VSConstants.S_OK;
        public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) => Microsoft.VisualStudio.VSConstants.S_OK;
        public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution) => Microsoft.VisualStudio.VSConstants.S_OK;
        public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) => Microsoft.VisualStudio.VSConstants.S_OK;
        public int OnAfterCloseSolution(object pUnkReserved) => Microsoft.VisualStudio.VSConstants.S_OK;

        #endregion
    }
}
