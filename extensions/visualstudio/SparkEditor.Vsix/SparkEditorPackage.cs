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
    public sealed class SparkEditorPackage : AsyncPackage
    {
        public static IVsOutputWindowPane OutputPane { get; private set; }

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var outputWindow = (IVsOutputWindow)GetGlobalService(typeof(SVsOutputWindow));
            var paneGuid = new Guid("F54B0E74-2A27-4D88-B1ED-8C1A5E3F9D01");
            outputWindow.CreatePane(ref paneGuid, "Spark", 1, 0);
            outputWindow.GetPane(ref paneGuid, out var pane);
            OutputPane = pane;

            await OpenSparkEditorCommand.InitializeAsync(this);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Kill the SparkEditor server process when VS shuts down
                var window = FindToolWindow(typeof(SparkEditorToolWindow), 0, false);
                if (window?.Content is SparkEditorToolWindowControl control)
                {
                    control.StopServer();
                }
            }

            base.Dispose(disposing);
        }
    }
}
