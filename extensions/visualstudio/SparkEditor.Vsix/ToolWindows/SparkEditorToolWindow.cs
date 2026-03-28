using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace SparkEditor.Vsix
{
    public class SparkEditorToolWindow : BaseToolWindow<SparkEditorToolWindow>
    {
        public override string GetTitle(int toolWindowId) => "Spark Editor";

        public override Type PaneType => typeof(Pane);

        public override Task<FrameworkElement> CreateAsync(int toolWindowId, CancellationToken cancellationToken)
        {
            return Task.FromResult<FrameworkElement>(new SparkEditorToolWindowControl());
        }

        [Guid("d3b3ebd9-87d1-41cd-bf84-268d88953417")]
        internal class Pane : ToolWindowPane
        {
            public Pane()
            {
                BitmapImageMoniker = KnownMonikers.SettingsGroup;
            }
        }
    }
}
