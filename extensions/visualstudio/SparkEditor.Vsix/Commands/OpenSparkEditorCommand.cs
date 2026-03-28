using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using System.Threading.Tasks;

namespace SparkEditor.Vsix.Commands
{
    [Command(PackageIds.OpenSparkEditorCommand)]
    internal sealed class OpenSparkEditorCommand : BaseCommand<OpenSparkEditorCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await SparkEditorToolWindow.ShowAsync();
        }
    }
}
