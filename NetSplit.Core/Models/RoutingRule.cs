namespace NetSplit.Core;

public sealed record RoutingRule(
    Guid Id,
    string ProcessName,
    int? ProcessId,
    RuleTargetType TargetType,
    AdapterPreference TargetAdapter,
    bool Enabled);
