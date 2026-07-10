using System.Net;
using NetSplit.Core;
using NetSplit.Network;
using NetSplit.Runtime;

namespace NetSplit.Driver.Interop;

/// <summary>
/// Bridges NetSplit.Runtime's RuntimeResolver to the driver's IOCTL
/// surface. This is the only component that resolves an AdapterPreference
/// (InterfaceIndex) to a live IPv4 address - using whatever adapter
/// snapshot the caller supplies (from NetSplit.Network, the sole discovery
/// authority - this class does not fetch adapters itself, so the host's
/// own discovery tick is the only place that data is ever queried).
///
/// Two paths keep the driver's rule set current:
/// - RuntimeRuleAdded/Updated/Removed events push individual diffs the
///   moment the resolver's snapshot changes.
/// - SynchronizeRuntimeRules(adapters), called once per discovery tick by
///   the host, re-resolves every active rule's target address against the
///   latest adapter snapshot and re-pushes only what changed (e.g. a DHCP
///   renewal) or was never successfully pushed (e.g. the process started
///   before its adapter had an address yet). Never sends a full table -
///   only the delta from what was last pushed.
/// </summary>
public sealed class RuntimeRuleSynchronizer : IDisposable
{
    private readonly RuntimeResolver _resolver;
    private readonly Dictionary<int, IPAddress> _pushedAddresses = new();
    private IReadOnlyList<NetworkAdapter> _latestAdapters = Array.Empty<NetworkAdapter>();

    /// <summary>Every PID this synchronizer believes it has successfully pushed to the driver, and the address it pushed - the "did the DriverSynchronizer send it, and to what" trace point.</summary>
    public IReadOnlyDictionary<int, IPAddress> PushedAddresses => _pushedAddresses;

    public DateTimeOffset? LastSuccessfulSyncAt { get; private set; }
    public DateTimeOffset? LastFailedSyncAt { get; private set; }
    public string? LastFailedSyncReason { get; private set; }
    public long PushSuccessCount { get; private set; }
    public long PushFailureCount { get; private set; }

    public RuntimeRuleSynchronizer(RuntimeResolver resolver)
    {
        _resolver = resolver;

        _resolver.RuntimeRuleAdded += OnRuntimeRuleAdded;
        _resolver.RuntimeRuleUpdated += OnRuntimeRuleUpdated;
        _resolver.RuntimeRuleRemoved += OnRuntimeRuleRemoved;
    }

    /// <summary>
    /// Re-resolves every currently active runtime rule's target address
    /// against the given adapter snapshot and pushes only what changed
    /// since the last call.
    /// </summary>
    public void SynchronizeRuntimeRules(IReadOnlyList<NetworkAdapter> adapters)
    {
        _latestAdapters = adapters;

        IReadOnlyList<RuntimeRule> current = _resolver.CurrentRuntimeRules;
        var currentPids = current.Select(r => r.Pid).ToHashSet();

        bool anyFailure = false;
        string? failureReason = null;

        foreach (RuntimeRule rule in current)
        {
            if (!PushIfChanged(rule, out string? reason))
            {
                anyFailure = true;
                failureReason = reason;
            }
        }

        foreach (int pid in _pushedAddresses.Keys.Where(p => !currentPids.Contains(p)).ToList())
        {
            if (!DriverClient.RemoveRuntimeRule(pid))
            {
                anyFailure = true;
                failureReason = $"RemoveRuntimeRule IOCTL failed for PID {pid}.";
            }
            _pushedAddresses.Remove(pid);
        }

        if (anyFailure)
        {
            LastFailedSyncAt = DateTimeOffset.UtcNow;
            LastFailedSyncReason = failureReason;
        }
        else
        {
            LastSuccessfulSyncAt = DateTimeOffset.UtcNow;
        }
    }

    public void ClearRuntimeRules()
    {
        DriverClient.ClearRuntimeRules();
        _pushedAddresses.Clear();
    }

    /// <summary>
    /// Forgets everything this synchronizer believes it already pushed,
    /// without touching the driver. Call this after detecting the driver
    /// was reloaded (its kernel table is empty again, but our "already
    /// pushed" cache doesn't know that) - the next SynchronizeRuntimeRules
    /// call will then re-push every active rule from scratch instead of
    /// wrongly assuming they're already in place.
    /// </summary>
    public void ResetPushedState() => _pushedAddresses.Clear();

    private void OnRuntimeRuleAdded(object? sender, RuntimeRuleAddedEventArgs e) => PushIfChanged(e.Rule, out _);

    private void OnRuntimeRuleUpdated(object? sender, RuntimeRuleUpdatedEventArgs e) => PushIfChanged(e.NewRule, out _);

    private void OnRuntimeRuleRemoved(object? sender, RuntimeRuleRemovedEventArgs e)
    {
        DriverClient.RemoveRuntimeRule(e.Rule.Pid);
        _pushedAddresses.Remove(e.Rule.Pid);
    }

    /// <summary>Returns false (with a reason) if the address couldn't be resolved or the IOCTL failed - the caller decides how to fold that into overall sync success/failure.</summary>
    private bool PushIfChanged(RuntimeRule rule, out string? failureReason)
    {
        failureReason = null;

        IPAddress? address = ResolveAddress(rule.TargetAdapter);
        if (address == null)
        {
            // Adapter currently has no resolvable IPv4 (disconnected, mid-
            // DHCP). Leave whatever was last pushed rather than guess -
            // fail-safe, not fail-open to a wrong address.
            failureReason = $"PID {rule.Pid}: target adapter '{rule.TargetAdapter.AdapterName}' (index {rule.TargetAdapter.InterfaceIndex}) has no resolvable IPv4 address.";
            PushFailureCount++;
            return false;
        }

        if (_pushedAddresses.TryGetValue(rule.Pid, out IPAddress? pushedAddress))
        {
            if (pushedAddress.Equals(address))
            {
                return true; // unchanged - nothing to send, not a failure
            }

            if (DriverClient.UpdateRuntimeRule(rule.Pid, address))
            {
                _pushedAddresses[rule.Pid] = address;
                PushSuccessCount++;
                return true;
            }

            failureReason = $"PID {rule.Pid}: UpdateRuntimeRule IOCTL failed (target {address}).";
            PushFailureCount++;
            return false;
        }

        if (DriverClient.AddRuntimeRule(rule.Pid, address))
        {
            _pushedAddresses[rule.Pid] = address;
            PushSuccessCount++;
            return true;
        }

        failureReason = $"PID {rule.Pid}: AddRuntimeRule IOCTL failed (target {address}).";
        PushFailureCount++;
        return false;
    }

    private IPAddress? ResolveAddress(AdapterPreference targetAdapter) =>
        _latestAdapters.FirstOrDefault(a => a.InterfaceIndex == targetAdapter.InterfaceIndex)?.IPv4;

    public void Dispose()
    {
        _resolver.RuntimeRuleAdded -= OnRuntimeRuleAdded;
        _resolver.RuntimeRuleUpdated -= OnRuntimeRuleUpdated;
        _resolver.RuntimeRuleRemoved -= OnRuntimeRuleRemoved;
    }
}
