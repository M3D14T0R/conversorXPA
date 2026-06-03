using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Task = System.Threading.Tasks.Task;

namespace XpaTaskExplorer;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[InstalledProductRegistration("XPA Task Explorer", "Reads converted XPA C# task hierarchy.", "0.1")]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideToolWindow(typeof(XpaTaskExplorerToolWindow))]
[Guid(PackageGuids.PackageGuidString)]
public sealed class XpaTaskExplorerPackage : AsyncPackage
{
    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        await ShowXpaTaskExplorerCommand.InitializeAsync(this, cancellationToken);
    }
}
