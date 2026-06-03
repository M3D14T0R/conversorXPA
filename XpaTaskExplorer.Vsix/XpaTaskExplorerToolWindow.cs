using Microsoft.VisualStudio.Shell;
using System.Runtime.InteropServices;

namespace XpaTaskExplorer;

[Guid(PackageGuids.ToolWindowGuidString)]
public sealed class XpaTaskExplorerToolWindow : ToolWindowPane
{
    public XpaTaskExplorerToolWindow() : base(null)
    {
        Caption = "XPA Task Explorer";
        Content = new XpaTaskExplorerControl();
    }
}
