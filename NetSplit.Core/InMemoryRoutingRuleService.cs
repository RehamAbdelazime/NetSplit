namespace NetSplit.Core;

/// <summary>
/// Keeps rules in memory only. No file, registry, or database persistence -
/// that is a separate concern to be added later.
/// </summary>
public sealed class InMemoryRoutingRuleService : IRoutingRuleService
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, RoutingRule> _rules = new();

    public event EventHandler? RulesChanged;

    public IReadOnlyList<RoutingRule> GetRules()
    {
        lock (_gate)
        {
            return _rules.Values.ToList();
        }
    }

    public RoutingRule? GetRule(Guid id)
    {
        lock (_gate)
        {
            return _rules.GetValueOrDefault(id);
        }
    }

    public RoutingRule AddRule(
        string processName,
        int? processId,
        RuleTargetType targetType,
        AdapterPreference targetAdapter,
        bool enabled = true)
    {
        lock (_gate)
        {
            EnsureNoDuplicate(processName, processId, targetType, excludingId: null);

            var rule = new RoutingRule(Guid.NewGuid(), processName, processId, targetType, targetAdapter, enabled);
            _rules.Add(rule.Id, rule);
            RaiseRulesChanged();
            return rule;
        }
    }

    public RoutingRule UpdateRule(
        Guid id,
        string processName,
        int? processId,
        RuleTargetType targetType,
        AdapterPreference targetAdapter)
    {
        lock (_gate)
        {
            if (!_rules.TryGetValue(id, out RoutingRule? existing))
            {
                throw new KeyNotFoundException($"No routing rule with ID {id}.");
            }

            EnsureNoDuplicate(processName, processId, targetType, excludingId: id);

            var updated = existing with
            {
                ProcessName = processName,
                ProcessId = processId,
                TargetType = targetType,
                TargetAdapter = targetAdapter,
            };

            _rules[id] = updated;
            RaiseRulesChanged();
            return updated;
        }
    }

    public bool DeleteRule(Guid id)
    {
        lock (_gate)
        {
            if (!_rules.Remove(id))
            {
                return false;
            }

            RaiseRulesChanged();
            return true;
        }
    }

    public bool EnableRule(Guid id) => SetEnabled(id, true);

    public bool DisableRule(Guid id) => SetEnabled(id, false);

    private bool SetEnabled(Guid id, bool enabled)
    {
        lock (_gate)
        {
            if (!_rules.TryGetValue(id, out RoutingRule? existing))
            {
                return false;
            }

            if (existing.Enabled == enabled)
            {
                return true;
            }

            _rules[id] = existing with { Enabled = enabled };
            RaiseRulesChanged();
            return true;
        }
    }

    /// <summary>
    /// Rejects rules that target the same process name (case-insensitive) or
    /// the same process ID as an existing rule of the same target type.
    /// Must be called while holding _gate.
    /// </summary>
    private void EnsureNoDuplicate(
        string processName,
        int? processId,
        RuleTargetType targetType,
        Guid? excludingId)
    {
        foreach (RoutingRule rule in _rules.Values)
        {
            if (rule.Id == excludingId || rule.TargetType != targetType)
            {
                continue;
            }

            switch (targetType)
            {
                case RuleTargetType.ProcessName:
                    if (string.Equals(rule.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new DuplicateRoutingRuleException(
                            $"A rule for process name '{processName}' already exists.");
                    }
                    break;

                case RuleTargetType.ProcessId:
                    if (rule.ProcessId == processId)
                    {
                        throw new DuplicateRoutingRuleException(
                            $"A rule for process ID {processId} already exists.");
                    }
                    break;
            }
        }
    }

    private void RaiseRulesChanged() => RulesChanged?.Invoke(this, EventArgs.Empty);
}
