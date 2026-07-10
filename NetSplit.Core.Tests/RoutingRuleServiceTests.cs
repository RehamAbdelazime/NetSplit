using NetSplit.Core;
using Xunit;

namespace NetSplit.Core.Tests;

public class RoutingRuleServiceTests
{
    private static AdapterPreference WiFi => new(2, "Wi-Fi");
    private static AdapterPreference Ethernet => new(1, "Ethernet");

    [Fact]
    public void AddRule_ByProcessName_IsReturnedByGetRules()
    {
        var service = new InMemoryRoutingRuleService();

        RoutingRule rule = service.AddRule("chrome.exe", null, RuleTargetType.ProcessName, WiFi);

        Assert.Equal("chrome.exe", rule.ProcessName);
        Assert.Equal(WiFi, rule.TargetAdapter);
        Assert.True(rule.Enabled);
        Assert.Contains(rule, service.GetRules());
        Assert.Equal(rule, service.GetRule(rule.Id));
    }

    [Fact]
    public void AddRule_DuplicateProcessName_IsCaseInsensitiveAndThrows()
    {
        var service = new InMemoryRoutingRuleService();
        service.AddRule("chrome.exe", null, RuleTargetType.ProcessName, WiFi);

        Assert.Throws<DuplicateRoutingRuleException>(() =>
            service.AddRule("CHROME.EXE", null, RuleTargetType.ProcessName, Ethernet));
    }

    [Fact]
    public void AddRule_DuplicateProcessId_Throws()
    {
        var service = new InMemoryRoutingRuleService();
        service.AddRule("chrome", 1234, RuleTargetType.ProcessId, WiFi);

        Assert.Throws<DuplicateRoutingRuleException>(() =>
            service.AddRule("chrome2", 1234, RuleTargetType.ProcessId, Ethernet));
    }

    [Fact]
    public void AddRule_SameNameDifferentTargetType_DoesNotCollide()
    {
        var service = new InMemoryRoutingRuleService();
        service.AddRule("chrome.exe", null, RuleTargetType.ProcessName, WiFi);

        // A ProcessId rule with an unrelated ID is not a duplicate of a
        // ProcessName rule, even if the display name happens to match.
        RoutingRule rule = service.AddRule("chrome.exe", 999, RuleTargetType.ProcessId, Ethernet);

        Assert.Equal(2, service.GetRules().Count);
        Assert.Equal(999, rule.ProcessId);
    }

    [Fact]
    public void UpdateRule_ChangesFieldsAndPreservesEnabled()
    {
        var service = new InMemoryRoutingRuleService();
        RoutingRule original = service.AddRule("chrome.exe", null, RuleTargetType.ProcessName, WiFi);
        service.DisableRule(original.Id);

        RoutingRule updated = service.UpdateRule(original.Id, "chrome.exe", null, RuleTargetType.ProcessName, Ethernet);

        Assert.Equal(Ethernet, updated.TargetAdapter);
        Assert.False(updated.Enabled); // Update must not silently re-enable a disabled rule.
        Assert.Equal(updated, service.GetRule(original.Id));
    }

    [Fact]
    public void UpdateRule_UnknownId_Throws()
    {
        var service = new InMemoryRoutingRuleService();

        Assert.Throws<KeyNotFoundException>(() =>
            service.UpdateRule(Guid.NewGuid(), "chrome.exe", null, RuleTargetType.ProcessName, WiFi));
    }

    [Fact]
    public void UpdateRule_CollidingWithAnotherRule_Throws()
    {
        var service = new InMemoryRoutingRuleService();
        service.AddRule("chrome.exe", null, RuleTargetType.ProcessName, WiFi);
        RoutingRule second = service.AddRule("steam.exe", null, RuleTargetType.ProcessName, Ethernet);

        Assert.Throws<DuplicateRoutingRuleException>(() =>
            service.UpdateRule(second.Id, "chrome.exe", null, RuleTargetType.ProcessName, Ethernet));
    }

    [Fact]
    public void UpdateRule_RenamingItselfToSameName_DoesNotThrow()
    {
        var service = new InMemoryRoutingRuleService();
        RoutingRule rule = service.AddRule("chrome.exe", null, RuleTargetType.ProcessName, WiFi);

        RoutingRule updated = service.UpdateRule(rule.Id, "chrome.exe", null, RuleTargetType.ProcessName, Ethernet);

        Assert.Equal(Ethernet, updated.TargetAdapter);
    }

    [Fact]
    public void DeleteRule_RemovesRule()
    {
        var service = new InMemoryRoutingRuleService();
        RoutingRule rule = service.AddRule("chrome.exe", null, RuleTargetType.ProcessName, WiFi);

        bool deleted = service.DeleteRule(rule.Id);

        Assert.True(deleted);
        Assert.Empty(service.GetRules());
        Assert.Null(service.GetRule(rule.Id));
    }

    [Fact]
    public void DeleteRule_UnknownId_ReturnsFalse()
    {
        var service = new InMemoryRoutingRuleService();

        Assert.False(service.DeleteRule(Guid.NewGuid()));
    }

    [Fact]
    public void EnableRule_And_DisableRule_ToggleEnabledFlag()
    {
        var service = new InMemoryRoutingRuleService();
        RoutingRule rule = service.AddRule("chrome.exe", null, RuleTargetType.ProcessName, WiFi);

        Assert.True(service.DisableRule(rule.Id));
        Assert.False(service.GetRule(rule.Id)!.Enabled);

        Assert.True(service.EnableRule(rule.Id));
        Assert.True(service.GetRule(rule.Id)!.Enabled);
    }

    [Fact]
    public void EnableRule_UnknownId_ReturnsFalse()
    {
        var service = new InMemoryRoutingRuleService();

        Assert.False(service.EnableRule(Guid.NewGuid()));
    }

    [Fact]
    public void RulesChanged_FiresOnAddUpdateDeleteEnableDisable()
    {
        var service = new InMemoryRoutingRuleService();
        int fireCount = 0;
        service.RulesChanged += (_, _) => fireCount++;

        RoutingRule rule = service.AddRule("chrome.exe", null, RuleTargetType.ProcessName, WiFi);
        Assert.Equal(1, fireCount);

        service.UpdateRule(rule.Id, "chrome.exe", null, RuleTargetType.ProcessName, Ethernet);
        Assert.Equal(2, fireCount);

        service.DisableRule(rule.Id);
        Assert.Equal(3, fireCount);

        service.EnableRule(rule.Id);
        Assert.Equal(4, fireCount);

        service.DeleteRule(rule.Id);
        Assert.Equal(5, fireCount);
    }

    [Fact]
    public void RulesChanged_DoesNotFire_WhenEnableRuleIsANoOp()
    {
        var service = new InMemoryRoutingRuleService();
        RoutingRule rule = service.AddRule("chrome.exe", null, RuleTargetType.ProcessName, WiFi);
        int fireCount = 0;
        service.RulesChanged += (_, _) => fireCount++;

        service.EnableRule(rule.Id); // already enabled

        Assert.Equal(0, fireCount);
    }
}
