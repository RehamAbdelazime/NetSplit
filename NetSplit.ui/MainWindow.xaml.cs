using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace NetSplit.ui;

public partial class MainWindow : Window
{
    private static readonly string[] AdapterNames = { "Ethernet", "Wi-Fi" };

    private readonly ObservableCollection<ProcessRow> _rows = new();
    private readonly DispatcherTimer _timer;

    public MainWindow()
    {
        InitializeComponent();
        ProcessGrid.ItemsSource = _rows;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        Refresh();
    }

    private void AdapterComboBox_Loaded(object sender, RoutedEventArgs e)
    {
        var combo = (ComboBox)sender;
        combo.ItemsSource = AdapterNames;
    }

    // Looks up each of the two hardcoded adapters' current IPv4 address.
    private static Dictionary<string, IPAddress> GetAdapterAddresses()
    {
        var result = new Dictionary<string, IPAddress>();

        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            if (!AdapterNames.Contains(nic.Name))
            {
                continue;
            }

            foreach (UnicastIPAddressInformation addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    result[nic.Name] = addr.Address;
                    break;
                }
            }
        }

        return result;
    }

    private static string ResolveCurrentAdapter(IPAddress localAddress, Dictionary<string, IPAddress> adapterAddresses)
    {
        foreach (var kvp in adapterAddresses)
        {
            if (kvp.Value.Equals(localAddress))
            {
                return kvp.Key;
            }
        }
        return "Unknown";
    }

    private void Refresh()
    {
        Dictionary<string, IPAddress> adapterAddresses = GetAdapterAddresses();

        // GetTcpConnections uses the native GetExtendedTcpTable API.
        var establishedByPid = new Dictionary<int, IPAddress>();
        foreach (NativeInterop.TcpConnection conn in NativeInterop.GetTcpConnections())
        {
            if (conn.Established && !establishedByPid.ContainsKey(conn.Pid))
            {
                establishedByPid[conn.Pid] = conn.LocalAddress;
            }
        }

        var seenPids = new HashSet<int>();

        foreach (var (pid, localAddress) in establishedByPid)
        {
            string processName;
            try
            {
                processName = Process.GetProcessById(pid).ProcessName + ".exe";
            }
            catch (ArgumentException)
            {
                continue; // process exited between snapshot and lookup
            }

            seenPids.Add(pid);
            string currentAdapter = ResolveCurrentAdapter(localAddress, adapterAddresses);

            ProcessRow? existing = _rows.FirstOrDefault(r => r.Pid == pid);
            if (existing != null)
            {
                existing.CurrentAdapter = currentAdapter;
                existing.NotifyCurrentAdapterChanged();
            }
            else
            {
                _rows.Add(new ProcessRow
                {
                    ProcessName = processName,
                    Pid = pid,
                    CurrentAdapter = currentAdapter,
                    SelectedAdapter = currentAdapter == "Unknown" ? AdapterNames[0] : currentAdapter,
                });
            }
        }

        for (int i = _rows.Count - 1; i >= 0; i--)
        {
            if (!seenPids.Contains(_rows[i].Pid))
            {
                _rows.RemoveAt(i);
            }
        }
    }

    private void AdapterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { Tag: ProcessRow row } combo || combo.SelectedItem is not string adapterName)
        {
            return;
        }

        Dictionary<string, IPAddress> adapterAddresses = GetAdapterAddresses();
        if (!adapterAddresses.TryGetValue(adapterName, out IPAddress? targetAddress))
        {
            return;
        }

        NativeInterop.SetTarget(row.ProcessName, targetAddress);
    }
}
