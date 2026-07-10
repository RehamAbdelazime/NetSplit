using NetSplit.Core;
using NetSplit.Network;
using NetSplit.Runtime;

// Standalone demo of the Runtime Resolver alone - no driver, no IPC.
// Detection rides the same discovery snapshot NetSplit.Network already
// produces - no WMI, no ETW, no elevation required.

var rules = new InMemoryRoutingRuleService();
rules.AddRule("chrome.exe", null, RuleTargetType.ProcessName, new AdapterPreference(15, "Wi-Fi"));
rules.AddRule("steam.exe", null, RuleTargetType.ProcessName, new AdapterPreference(8, "Ethernet"));

var resolver = new RuntimeResolver(rules);
var discovery = new NetworkDiscoveryService();

resolver.RuntimeRuleAdded += (_, e) =>
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ADD PID {e.Rule.Pid} -> {e.Rule.TargetAdapter.AdapterName}");

resolver.RuntimeRuleUpdated += (_, e) =>
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] UPDATE PID {e.NewRule.Pid} -> {e.NewRule.TargetAdapter.AdapterName}");

resolver.RuntimeRuleRemoved += (_, e) =>
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] REMOVE PID {e.Rule.Pid}");

Console.WriteLine("Watching for chrome.exe -> Wi-Fi and steam.exe -> Ethernet.");
Console.WriteLine("Detection rides the existing discovery snapshot (2s tick) - no WMI, no polling of its own.");
Console.WriteLine("Start/close matching processes to see ADD/REMOVE. Press Ctrl+C to exit.");
Console.WriteLine();

while (true)
{
    IReadOnlyList<NetworkProcess> processes = await discovery.GetActiveProcessesAsync();
    resolver.UpdateSnapshot(processes);
    await Task.Delay(TimeSpan.FromSeconds(2));
}
