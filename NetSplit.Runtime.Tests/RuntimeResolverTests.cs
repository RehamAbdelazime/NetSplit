using NetSplit.Core;
using NetSplit.Network;
using Xunit;

namespace NetSplit.Runtime.Tests;

public class RuntimeResolverTests
{
    private static AdapterPreference WiFi => new(15, "Wi-Fi");
    private static AdapterPreference Ethernet => new(8, "Ethernet");

    private static NetworkProcess Proc(int pid, string name) =>
        new() { ProcessId = pid, ProcessName = name, Connections = Array.Empty<Connection>() };

    [Fact]
    public void UpdateSnapshot_MatchingProcess_FiresAdded()
    {
        var rules = new InMemoryRoutingRuleService();
        rules.AddRule("chrome.exe", null, RuleTargetType.ProcessName, WiFi);
        var resolver = new RuntimeResolver(rules);

        RuntimeRuleAddedEventArgs? captured = null;
        resolver.RuntimeRuleAdded += (_, e) => captured = e;

        resolver.UpdateSnapshot(new[] { Proc(111, "chrome") });

        Assert.NotNull(captured);
        Assert.Equal(111, captured!.Rule.Pid);
        Assert.Equal(WiFi, captured.Rule.TargetAdapter);
        Assert.Contains(resolver.CurrentRuntimeRules, r => r.Pid == 111);
    }

    [Fact]
    public void UpdateSnapshot_NoMatchingRule_FiresNothing()
    {
        var rules = new InMemoryRoutingRuleService();
        rules.AddRule("chrome.exe", null, RuleTargetType.ProcessName, WiFi);
        var resolver = new RuntimeResolver(rules);

        bool fired = false;
        resolver.RuntimeRuleAdded += (_, _) => fired = true;

        resolver.UpdateSnapshot(new[] { Proc(222, "discord") });

        Assert.False(fired);
        Assert.Empty(resolver.CurrentRuntimeRules);
    }

    [Fact]
    public void UpdateSnapshot_IsCaseInsensitive()
    {
        var rules = new InMemoryRoutingRuleService();
        rules.AddRule("Chrome.exe", null, RuleTargetType.ProcessName, WiFi);
        var resolver = new RuntimeResolver(rules);

        RuntimeRuleAddedEventArgs? captured = null;
        resolver.RuntimeRuleAdded += (_, e) => captured = e;

        resolver.UpdateSnapshot(new[] { Proc(333, "CHROME") });

        Assert.NotNull(captured);
        Assert.Equal(333, captured!.Rule.Pid);
    }

    [Fact]
    public void UpdateSnapshot_ProcessNoLongerInSnapshot_FiresRemoved()
    {
        var rules = new InMemoryRoutingRuleService();
        rules.AddRule("chrome.exe", null, RuleTargetType.ProcessName, WiFi);
        var resolver = new RuntimeResolver(rules);

        resolver.UpdateSnapshot(new[] { Proc(444, "chrome") });

        RuntimeRuleRemovedEventArgs? captured = null;
        resolver.RuntimeRuleRemoved += (_, e) => captured = e;

        resolver.UpdateSnapshot(Array.Empty<NetworkProcess>()); // process exited

        Assert.NotNull(captured);
        Assert.Equal(444, captured!.Rule.Pid);
        Assert.Empty(resolver.CurrentRuntimeRules);
    }

    [Fact]
    public void UpdateSnapshot_UnchangedProcess_FiresNothingOnSecondCall()
    {
        var rules = new InMemoryRoutingRuleService();
        rules.AddRule("chrome.exe", null, RuleTargetType.ProcessName, WiFi);
        var resolver = new RuntimeResolver(rules);

        resolver.UpdateSnapshot(new[] { Proc(555, "chrome") });

        bool addedAgain = false, removedAgain = false;
        resolver.RuntimeRuleAdded += (_, _) => addedAgain = true;
        resolver.RuntimeRuleRemoved += (_, _) => removedAgain = true;

        resolver.UpdateSnapshot(new[] { Proc(555, "chrome") }); // same PID, same rule

        Assert.False(addedAgain);
        Assert.False(removedAgain);
    }

    [Fact]
    public void UpdateSnapshot_TargetAdapterChanged_FiresUpdatedNotAddOrRemove()
    {
        var rules = new InMemoryRoutingRuleService();
        RoutingRule rule = rules.AddRule("chrome.exe", null, RuleTargetType.ProcessName, WiFi);
        var resolver = new RuntimeResolver(rules);

        resolver.UpdateSnapshot(new[] { Proc(666, "chrome") });

        int addedCount = 0, removedCount = 0;
        RuntimeRuleUpdatedEventArgs? updated = null;
        resolver.RuntimeRuleAdded += (_, _) => addedCount++;
        resolver.RuntimeRuleRemoved += (_, _) => removedCount++;
        resolver.RuntimeRuleUpdated += (_, e) => updated = e;

        rules.UpdateRule(rule.Id, "chrome.exe", null, RuleTargetType.ProcessName, Ethernet);
        resolver.UpdateSnapshot(new[] { Proc(666, "chrome") }); // same process, rule retargeted

        Assert.NotNull(updated);
        Assert.Equal(666, updated!.OldRule.Pid);
        Assert.Equal(WiFi, updated.OldRule.TargetAdapter);
        Assert.Equal(Ethernet, updated.NewRule.TargetAdapter);
        Assert.Equal(0, addedCount);
        Assert.Equal(0, removedCount);
    }

    [Fact]
    public void UpdateSnapshot_RuleDisabled_FiresRemovedForTrackedPid()
    {
        var rules = new InMemoryRoutingRuleService();
        RoutingRule rule = rules.AddRule("chrome.exe", null, RuleTargetType.ProcessName, WiFi);
        var resolver = new RuntimeResolver(rules);

        resolver.UpdateSnapshot(new[] { Proc(777, "chrome") });

        rules.DisableRule(rule.Id);

        RuntimeRuleRemovedEventArgs? captured = null;
        resolver.RuntimeRuleRemoved += (_, e) => captured = e;

        resolver.UpdateSnapshot(new[] { Proc(777, "chrome") }); // process still running, rule now disabled

        Assert.NotNull(captured);
        Assert.Equal(777, captured!.Rule.Pid);
        Assert.Empty(resolver.CurrentRuntimeRules);
    }

    [Fact]
    public void UpdateSnapshot_MultipleInstancesOfSameProcessName_AreTrackedIndependently()
    {
        var rules = new InMemoryRoutingRuleService();
        rules.AddRule("chrome.exe", null, RuleTargetType.ProcessName, WiFi);
        var resolver = new RuntimeResolver(rules);

        resolver.UpdateSnapshot(new[] { Proc(801, "chrome"), Proc(802, "chrome") });

        Assert.Equal(2, resolver.CurrentRuntimeRules.Count);
        Assert.Contains(resolver.CurrentRuntimeRules, r => r.Pid == 801);
        Assert.Contains(resolver.CurrentRuntimeRules, r => r.Pid == 802);
    }
}
