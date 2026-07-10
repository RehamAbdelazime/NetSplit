using NetSplit.Driver.Interop;

namespace NetSplit.Service;

/// <summary>
/// The Service's own verdict on driver availability - a strict superset of
/// DriverServiceState (Driver.Interop's SCM-only view) that additionally
/// accounts for whether the control device actually answers once the SCM
/// says Running. Exactly the five states the routing-validation prep asked
/// to be able to tell apart, plus Unknown for the (rare) case even the SCM
/// query itself fails oddly.
/// </summary>
public enum DriverAvailabilityState
{
    Unknown,
    NotInstalled,
    AccessDenied,
    Stopped,
    DeviceUnavailable,
    Running,
}

/// <summary>
/// Owns the driver's lifecycle from the Service's perspective: is it
/// loaded, is its protocol version compatible, and exactly why if not.
/// Nothing else in the Service calls DriverClient's version/health surface
/// directly - they ask this instead, so "how do we know if the driver is
/// alive" has exactly one answer.
///
/// Deliberately read-only: this component never starts, stops, or
/// otherwise mutates the driver's kernel service. Starting it is a
/// deployment step (see DEPLOYMENT_CHECKLIST.md) - RefreshAsync only
/// observes and records state, every tick, so a caller always has an
/// up-to-date, exactly-reasoned answer without the Service ever attempting
/// a privileged action it may not hold rights to perform anyway.
/// </summary>
public interface IDriverHost
{
    bool IsLoaded { get; }
    DriverVersionInfo? LastKnownVersion { get; }
    DriverAvailabilityState State { get; }

    /// <summary>The exact, unsummarized reason behind State - the SCM/device Win32 facts that produced it, not a paraphrase.</summary>
    string StateDetail { get; }

    /// <summary>Raised when the driver transitions from not-loaded/incompatible to loaded-and-compatible - a signal that any component caching "what I already pushed to the driver" must assume the kernel-side state is empty again.</summary>
    event EventHandler? DriverReconnected;

    /// <summary>Read-only health check: queries the SCM and (if it reports Running) the control device, and records what it finds. Never starts, stops, or otherwise changes the driver service. Safe to call repeatedly (e.g. once per discovery tick).</summary>
    Task RefreshAsync(CancellationToken cancellationToken);
}
