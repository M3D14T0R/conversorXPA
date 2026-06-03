using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel.Design;
using System.Threading;
using System.Threading.Tasks;
using Task = System.Threading.Tasks.Task;

namespace XpaTaskExplorer;

internal sealed class ShowXpaTaskExplorerCommand
{
    public const int CommandId = 0x0100;

    private readonly AsyncPackage _package;

    private ShowXpaTaskExplorerCommand(AsyncPackage package, OleMenuCommandService commandService)
    {
        _package = package ?? throw new ArgumentNullException(nameof(package));
        var commandId = new CommandID(PackageGuids.CommandSet, CommandId);
        commandService.AddCommand(new MenuCommand(Execute, commandId));
    }

    public static async Task InitializeAsync(AsyncPackage package, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (await package.GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
            _ = new ShowXpaTaskExplorerCommand(package, commandService);
    }

    private void Execute(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _package.JoinableTaskFactory.Run(async () =>
        {
            var window = await _package.ShowToolWindowAsync(typeof(XpaTaskExplorerToolWindow), 0, true, _package.DisposalToken);
            if (window?.Frame is null)
                throw new NotSupportedException("Cannot create XPA Task Explorer window.");
        });
    }
}
