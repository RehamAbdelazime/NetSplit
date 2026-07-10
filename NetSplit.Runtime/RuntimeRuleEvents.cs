namespace NetSplit.Runtime;

public sealed class RuntimeRuleAddedEventArgs(RuntimeRule rule) : EventArgs
{
    public RuntimeRule Rule { get; } = rule;
}

public sealed class RuntimeRuleUpdatedEventArgs(RuntimeRule oldRule, RuntimeRule newRule) : EventArgs
{
    public RuntimeRule OldRule { get; } = oldRule;
    public RuntimeRule NewRule { get; } = newRule;
}

public sealed class RuntimeRuleRemovedEventArgs(RuntimeRule rule) : EventArgs
{
    public RuntimeRule Rule { get; } = rule;
}
