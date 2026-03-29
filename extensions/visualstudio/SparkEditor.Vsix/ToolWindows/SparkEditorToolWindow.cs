using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;

namespace SparkEditor.Vsix
{
    [Guid("d3b3ebd9-87d1-41cd-bf84-268d88953417")]
    public class SparkEditorToolWindow : ToolWindowPane
    {
        public SparkEditorToolWindow() : base(null)
        {
            Caption = "Spark Editor";
            Content = new SparkEditorToolWindowControl();
        }
    }
}
