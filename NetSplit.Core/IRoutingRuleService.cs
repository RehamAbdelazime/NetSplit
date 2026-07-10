namespace NetSplit.Core;

public interface IRoutingRuleService
{
    IReadOnlyList<RoutingRule> GetRules();

    RoutingRule? GetRule(Guid id);

    /// <summary>
    /// Throws <see cref="DuplicateRoutingRuleException"/> if a rule already
    /// targets the same process name (case-insensitive) or process ID.
    /// </summary>
    RoutingRule AddRule(
        string processName,
        int? processId,
        RuleTargetType targetType,
        AdapterPreference targetAdapter,
        bool enabled = true);

    /// <summary>
    /// Throws <see cref="KeyNotFoundException"/> if no rule has the given ID,
    /// or <see cref="DuplicateRoutingRuleException"/> if the new target
    /// collides with a different existing rule.
    /// </summary>
    RoutingRule UpdateRule(
        Guid id,
        string processName,
        int? processId,
        RuleTargetType targetType,
        AdapterPreference targetAdapter);

    /// <returns>true if a rule was found and deleted; false otherwise.</returns>
    bool DeleteRule(Guid id);

    /// <returns>true if a rule was found and enabled; false otherwise.</returns>
    bool EnableRule(Guid id);

    /// <returns>true if a rule was found and disabled; false otherwise.</returns>
    bool DisableRule(Guid id);

    event EventHandler? RulesChanged;
}
