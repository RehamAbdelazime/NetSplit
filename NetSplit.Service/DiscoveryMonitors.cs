using NetSplit.Network;

namespace NetSplit.Service;

public sealed class ProcessesUpdatedEventArgs(IReadOnlyList<NetworkProcess> processes) : EventArgs
{
    public IReadOnlyList<NetworkProcess> Processes { get; } = processes;
}

public sealed class AdaptersUpdatedEventArgs(IReadOnlyList<NetworkAdapter> adapters) : EventArgs
{
    public IReadOnlyList<NetworkAdapter> Adapters { get; } = adapters;
}

/// <summary>Owns process discovery, as a stream subscribers react to. One responsibility: tell subscribers what's currently running.</summary>
public interface IProcessMonitor
{
    event EventHandler<ProcessesUpdatedEventArgs>? ProcessesUpdated;
}

/// <summary>Owns adapter discovery, as a stream subscribers react to. One responsibility: tell subscribers what adapters currently exist and their addresses.</summary>
public interface IAdapterMonitor
{
    event EventHandler<AdaptersUpdatedEventArgs>? AdaptersUpdated;
}

/// <summary>
/// Implements both monitor interfaces backed by one shared discovery tick.
/// Deliberate: NetSplit.Network's GetActiveProcessesAsync/GetAdaptersAsync
/// already do their own native table scans, and driving Process/Adapter
/// monitoring from two fully independent timers would double that work for
/// zero benefit. Each interface is still independently consumable and
/// independently replaceable - a caller wanting only adapter changes
/// depends on IAdapterMonitor and never sees a process event. The
/// component that owns *when* a tick happens (Worker, the composition
/// root) is separate from this class, which only ever reacts to Publish
/// calls - it has no timer of its own.
/// </summary>
public sealed class DiscoverySnapshotPump : IProcessMonitor, IAdapterMonitor
{
    public event EventHandler<ProcessesUpdatedEventArgs>? ProcessesUpdated;
    public event EventHandler<AdaptersUpdatedEventArgs>? AdaptersUpdated;

    /// <summary>Last-published snapshot, for a consumer (diagnostics) that wants "what does the Service currently see" without subscribing to the event stream.</summary>
    public IReadOnlyList<NetworkProcess> LatestProcesses { get; private set; } = Array.Empty<NetworkProcess>();
    public IReadOnlyList<NetworkAdapter> LatestAdapters { get; private set; } = Array.Empty<NetworkAdapter>();

    public void PublishProcesses(IReadOnlyList<NetworkProcess> processes)
    {
        LatestProcesses = processes;
        ProcessesUpdated?.Invoke(this, new ProcessesUpdatedEventArgs(processes));
    }

    public void PublishAdapters(IReadOnlyList<NetworkAdapter> adapters)
    {
        LatestAdapters = adapters;
        AdaptersUpdated?.Invoke(this, new AdaptersUpdatedEventArgs(adapters));
    }
}
