using System.Windows;
using NetSplit.App.ViewModels;
using NetSplit.Core;
using NetSplit.Ipc;

namespace NetSplit.App;

public partial class App : Application
{
    // The composition root. The UI no longer talks to the driver or owns
    // any resolver/synchronizer - NetSplit.Service does, and keeps working
    // even when this process isn't running. IRoutingRuleService is the only
    // seam MainViewModel and every View depend on, so swapping the
    // in-process implementation for a pipe-backed one required no changes
    // below this file. No admin rights needed: the pipe client only talks
    // to the already-elevated Service over IPC.
    private PipeRoutingRuleClient? _rules;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _rules = new PipeRoutingRuleClient();

        var viewModel = new MainViewModel(_rules);
        var window = new MainWindow(viewModel, _rules);
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _rules?.Dispose();
        base.OnExit(e);
    }
}
