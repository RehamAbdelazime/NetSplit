using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetSplit.Driver.Interop;
using NetSplit.Ipc;
using NetSplit.Network;
using NetSplit.Runtime;

namespace NetSplit.Service;

/// <summary>
/// The bootstrap layer, and nothing else. Wires already-built,
/// independently-responsible components together (event subscriptions) and
/// drives the one discovery tick that everything else reacts to. No rule
/// logic, no address resolution, no IOCTL calls live here - those all
/// belong to (and stay in) RuntimeResolver, RuntimeRuleSynchronizer, and
/// DriverClient respectively.
/// </summary>
public sealed class Worker : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);

    private readonly ILogger<Worker> _logger;
    private readonly NetworkDiscoveryService _discovery;
    private readonly DiscoverySnapshotPump _pump;
    private readonly RuntimeResolver _resolver;
    private readonly RuntimeRuleSynchronizer _synchronizer;
    private readonly IDriverHost _driverHost;
    private readonly RoutingRuleIpcServer _ipcServer;

    public Worker(
        ILogger<Worker> logger,
        NetworkDiscoveryService discovery,
        DiscoverySnapshotPump pump,
        RuntimeResolver resolver,
        RuntimeRuleSynchronizer synchronizer,
        IDriverHost driverHost,
        RoutingRuleIpcServer ipcServer)
    {
        _logger = logger;
        _discovery = discovery;
        _pump = pump;
        _resolver = resolver;
        _synchronizer = synchronizer;
        _driverHost = driverHost;
        _ipcServer = ipcServer;

        // Composition only: connect each component to the events it reacts
        // to. No decision-making happens in these lambdas.
        _pump.ProcessesUpdated += (_, e) => _resolver.UpdateSnapshot(e.Processes);
        _pump.AdaptersUpdated += (_, e) => _synchronizer.SynchronizeRuntimeRules(e.Adapters);

        // A driver reload means its kernel table is empty again - the
        // synchronizer's "already pushed" cache must be forgotten so the
        // next tick re-pushes everything instead of wrongly skipping it.
        _driverHost.DriverReconnected += (_, _) => _synchronizer.ResetPushedState();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("NetSplit service starting. Session {SessionId}.", Environment.ProcessId);

        _ipcServer.Start();
        _logger.LogInformation("IPC pipe '{PipeName}' listening.", IpcProtocol.PipeName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _driverHost.RefreshAsync(stoppingToken).ConfigureAwait(false);

                IReadOnlyList<NetworkAdapter> adapters = await _discovery.GetAdaptersAsync().ConfigureAwait(false);
                IReadOnlyList<NetworkProcess> processes = await _discovery.GetActiveProcessesAsync().ConfigureAwait(false);

                // Adapters first: RuntimeResolver may raise RuntimeRuleAdded
                // synchronously as a result of PublishProcesses, and the
                // synchronizer needs a current adapter snapshot to resolve
                // an address for it immediately rather than waiting a tick.
                _pump.PublishAdapters(adapters);
                _pump.PublishProcesses(processes);
            }
            catch (Exception ex)
            {
                // One bad tick (a transient discovery failure, for example)
                // must not take the whole service down - log and retry next tick.
                _logger.LogError(ex, "Unhandled error during discovery tick.");
            }

            try
            {
                await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("NetSplit service stopping - clearing runtime rules from the driver.");

        // Graceful shutdown clears the kernel rule table so no PID's
        // routing outlives this process. An unclean stop (crash, kill -9)
        // skips this - that's exactly why the driver itself, not just this
        // best-effort call, is the thing responsible for eventually being
        // told to clear state again on the next clean startup.
        _synchronizer.ClearRuntimeRules();

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
