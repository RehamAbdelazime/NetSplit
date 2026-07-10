using System.Collections.ObjectModel;
using System.Windows.Threading;
using NetSplit.Core;
using NetSplit.Network;

namespace NetSplit.App.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private readonly NetworkDiscoveryService _discovery = new();
    private readonly TrafficMonitor _traffic = new();
    private readonly IRoutingRuleService _rules;
    private readonly DispatcherTimer _timer;

    public ObservableCollection<ProcessViewModel> Processes { get; } = new();

    public MainViewModel(IRoutingRuleService rules)
    {
        _rules = rules;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        IReadOnlyList<NetworkAdapter> adapters = await _discovery.GetAdaptersAsync();
        IReadOnlyList<NetworkProcess> processes = await _discovery.GetActiveProcessesAsync();

        // Sampled every tick regardless of who's expanded - cheap relative to
        // the rest of a refresh, and needed continuously for the rate math
        // (it diffs against its own previous call).
        IReadOnlyDictionary<int, TrafficRate> rates = _traffic.Sample();

        IReadOnlyList<RoutingRule> rules = _rules.GetRules();

        var adapterOptions = new List<AdapterOptionViewModel> { AdapterOptionViewModel.Auto };
        adapterOptions.AddRange(adapters
            .Where(a => a.InterfaceIndex >= 0)
            .Select(a => new AdapterOptionViewModel(a.FriendlyName, a.IPv4?.ToString(), a.InterfaceIndex, false)));

        var seenPids = new HashSet<int>();

        foreach (NetworkProcess process in processes.OrderByDescending(p => p.Connections.Count))
        {
            seenPids.Add(process.ProcessId);
            string exeName = process.ProcessName + ".exe";

            RoutingRule? rule = rules.FirstOrDefault(r =>
                r.TargetType == RuleTargetType.ProcessName &&
                string.Equals(r.ProcessName, exeName, StringComparison.OrdinalIgnoreCase));

            AdapterOptionViewModel selected = rule != null
                ? adapterOptions.FirstOrDefault(o => o.InterfaceIndex == rule.TargetAdapter.InterfaceIndex) ?? AdapterOptionViewModel.Auto
                : AdapterOptionViewModel.Auto;

            TrafficRate rate = rates.GetValueOrDefault(process.ProcessId) ?? new TrafficRate(0, 0);

            ProcessViewModel? vm = Processes.FirstOrDefault(p => p.Pid == process.ProcessId);
            if (vm == null)
            {
                vm = new ProcessViewModel(_rules)
                {
                    ProcessName = exeName,
                    Pid = process.ProcessId,
                };
                Processes.Add(vm);
            }

            vm.SuppressRuleWrites = true;
            vm.CurrentAdapter = process.ResolvedAdapter?.FriendlyName ?? "Unknown";
            vm.AdapterOptions = adapterOptions;
            vm.SelectedAdapterOption = selected;
            vm.SuppressRuleWrites = false;

            vm.SendBytesPerSecond = rate.SendBytesPerSecond;
            vm.ReceiveBytesPerSecond = rate.ReceiveBytesPerSecond;

            SyncConnections(vm, process.Connections, adapters);
        }

        for (int i = Processes.Count - 1; i >= 0; i--)
        {
            if (!seenPids.Contains(Processes[i].Pid))
            {
                Processes.RemoveAt(i);
            }
        }
    }

    private static void SyncConnections(ProcessViewModel vm, IReadOnlyList<Connection> connections, IReadOnlyList<NetworkAdapter> adapters)
    {
        vm.Connections.Clear();
        foreach (Connection c in connections)
        {
            string adapterName = c.AdapterIndex >= 0
                ? adapters.FirstOrDefault(a => a.InterfaceIndex == c.AdapterIndex)?.FriendlyName ?? "Unknown"
                : "Unknown";

            vm.Connections.Add(new ConnectionViewModel
            {
                LocalEndpoint = $"{c.LocalAddress}:{c.LocalPort}",
                RemoteEndpoint = $"{c.RemoteAddress}:{c.RemotePort}",
                State = c.State.ToString(),
                Adapter = adapterName,
            });
        }
    }
}
