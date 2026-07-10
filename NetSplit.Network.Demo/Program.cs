using NetSplit.Network;

var discovery = new NetworkDiscoveryService();

IReadOnlyList<NetworkProcess> processes = await discovery.GetActiveProcessesAsync();

foreach (NetworkProcess process in processes.OrderByDescending(p => p.Connections.Count))
{
    Console.WriteLine(process.ProcessName);
    Console.WriteLine($"PID {process.ProcessId}");
    Console.WriteLine();
    Console.WriteLine("Adapter:");
    Console.WriteLine(process.ResolvedAdapter?.FriendlyName ?? "(unresolved)");
    Console.WriteLine();
    Console.WriteLine("Connections:");
    Console.WriteLine(process.Connections.Count);
    Console.WriteLine();
    Console.WriteLine(new string('-', 32));
}
