using NetSplit.Core;
using NetSplit.Network;

namespace NetSplit.Runtime;

/// <summary>
/// Translates permanent, name-based rules ("chrome.exe -> Wi-Fi") into
/// runtime, PID-based rules ("PID 1234 -> Wi-Fi").
///
/// Detection is not independent: it rides the discovery snapshot the host's
/// process-monitoring component already produces (NetSplit.Network's
/// process enumeration). Call UpdateSnapshot() once per discovery tick;
/// this class only ever diffs the PIDs in that snapshot against permanent
/// rules and its own previous snapshot - no WMI, no ETW, no ManagementEventWatcher,
/// no ownProcess enumeration, no timer of its own. One less OS subsystem to
/// depend on, one less elevation requirement (WMI kernel trace subscription
/// needed SeSystemProfilePrivilege; this needs nothing beyond what process
/// discovery already needs), and one fewer moving part to keep correct.
///
/// Independent of WPF and of the driver: it depends only on NetSplit.Core's
/// rule model and NetSplit.Network's NetworkProcess/discovery model.
/// </summary>
public sealed class RuntimeResolver
{
    private readonly IRoutingRuleService _rules;
    private readonly object _gate = new();
    private readonly Dictionary<int, RuntimeRule> _current = new();

    public event EventHandler<RuntimeRuleAddedEventArgs>? RuntimeRuleAdded;
    public event EventHandler<RuntimeRuleUpdatedEventArgs>? RuntimeRuleUpdated;
    public event EventHandler<RuntimeRuleRemovedEventArgs>? RuntimeRuleRemoved;

    public RuntimeResolver(IRoutingRuleService rules)
    {
        _rules = rules;
    }

    /// <summary>Current runtime rules, for a consumer that wants a snapshot rather than only the incremental events.</summary>
    public IReadOnlyList<RuntimeRule> CurrentRuntimeRules
    {
        get
        {
            lock (_gate)
            {
                return _current.Values.ToList();
            }
        }
    }

    /// <summary>
    /// Diffs the processes in this snapshot (and the current permanent
    /// rules) against the previous call, firing Added/Updated/Removed for
    /// whatever changed. Call once per discovery tick - this method IS the
    /// synchronization point; there is no independent polling inside it.
    /// </summary>
    public void UpdateSnapshot(IReadOnlyList<NetworkProcess> processes)
    {
        IReadOnlyList<RoutingRule> permanentRules = _rules.GetRules()
            .Where(r => r.Enabled && r.TargetType == RuleTargetType.ProcessName)
            .ToList();

        if (permanentRules.Count == 0 && _current.Count == 0)
        {
            return; // common case, cheap exit
        }

        // NetworkProcess.ProcessName has no ".exe" (Process.ProcessName's
        // own convention); permanent rules store it with the extension.
        var rulesByProcessName = new Dictionary<string, RoutingRule>(StringComparer.OrdinalIgnoreCase);
        foreach (RoutingRule rule in permanentRules)
        {
            string nameWithoutExtension = rule.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? rule.ProcessName[..^4]
                : rule.ProcessName;

            rulesByProcessName[nameWithoutExtension] = rule; // last one wins if duplicates ever slip through
        }

        var desired = new Dictionary<int, RuntimeRule>();
        foreach (NetworkProcess process in processes)
        {
            if (rulesByProcessName.TryGetValue(process.ProcessName, out RoutingRule? rule))
            {
                desired[process.ProcessId] = new RuntimeRule(rule.Id, process.ProcessId, rule.ProcessName, rule.TargetAdapter);
            }
        }

        List<RuntimeRule> added = new();
        List<(RuntimeRule Old, RuntimeRule New)> updated = new();
        List<RuntimeRule> removed = new();

        lock (_gate)
        {
            foreach (int pid in _current.Keys.Except(desired.Keys).ToList())
            {
                removed.Add(_current[pid]);
                _current.Remove(pid);
            }

            foreach ((int pid, RuntimeRule rule) in desired)
            {
                if (!_current.TryGetValue(pid, out RuntimeRule? existing))
                {
                    _current[pid] = rule;
                    added.Add(rule);
                }
                else if (existing.TargetAdapter != rule.TargetAdapter)
                {
                    _current[pid] = rule;
                    updated.Add((existing, rule));
                }
            }
        }

        foreach (RuntimeRule rule in removed)
        {
            RuntimeRuleRemoved?.Invoke(this, new RuntimeRuleRemovedEventArgs(rule));
        }

        foreach ((RuntimeRule oldRule, RuntimeRule newRule) in updated)
        {
            RuntimeRuleUpdated?.Invoke(this, new RuntimeRuleUpdatedEventArgs(oldRule, newRule));
        }

        foreach (RuntimeRule rule in added)
        {
            RuntimeRuleAdded?.Invoke(this, new RuntimeRuleAddedEventArgs(rule));
        }
    }
}
