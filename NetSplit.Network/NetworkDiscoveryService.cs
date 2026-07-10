using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.NetworkInformation;
using NetSplit.Network.Interop;

namespace NetSplit.Network;

/// <summary>
/// Discovers running network processes, their active TCP connections, and
/// the network adapters on this machine - a single point-in-time snapshot.
/// No polling, no driver communication, no packet modification.
/// </summary>
public sealed class NetworkDiscoveryService
{
    public Task<IReadOnlyList<NetworkAdapter>> GetAdaptersAsync() =>
        Task.Run(() => (IReadOnlyList<NetworkAdapter>)EnumerateAdapters().Adapters);

    public Task<IReadOnlyList<NetworkProcess>> GetActiveProcessesAsync() =>
        Task.Run(() => (IReadOnlyList<NetworkProcess>)BuildProcessSnapshot());

    // Address -> InterfaceIndex, covering every unicast address (IPv4 *and*
    // IPv6, every address if an adapter has more than one) on every
    // adapter. Matching against only the first IPv4 address - the original
    // implementation - misses adapters with a secondary/APIPA address and
    // is blind to IPv6 entirely.
    private static (List<NetworkAdapter> Adapters, Dictionary<IPAddress, int> AddressMap) EnumerateAdapters()
    {
        var adapters = new List<NetworkAdapter>();
        var addressMap = new Dictionary<IPAddress, int>();

        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            IPInterfaceProperties props = nic.GetIPProperties();

            IPAddress? ipv4 = props.UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                ?.Address;

            IPAddress? gateway = props.GatewayAddresses
                .FirstOrDefault(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                ?.Address;

            long speed;
            try
            {
                speed = nic.Speed;
            }
            catch (NetworkInformationException)
            {
                speed = -1;
            }

            // Prefer the IPv4 interface index; some adapters (rare, but real -
            // certain tunnel/VPN configurations) carry IPv6 but throw for
            // GetIPv4Properties, so fall back to the IPv6 index rather than
            // giving up and matching nothing on that adapter at all.
            int interfaceIndex;
            try
            {
                interfaceIndex = props.GetIPv4Properties()?.Index ?? -1;
            }
            catch (NetworkInformationException)
            {
                try
                {
                    interfaceIndex = props.GetIPv6Properties()?.Index ?? -1;
                }
                catch (NetworkInformationException)
                {
                    interfaceIndex = -1;
                }
            }

            foreach (UnicastIPAddressInformation addr in props.UnicastAddresses)
            {
                // Same address genuinely configured on two adapters isn't a
                // normal setup; last one wins rather than throwing.
                addressMap[addr.Address] = interfaceIndex;
            }

            adapters.Add(new NetworkAdapter
            {
                InterfaceIndex = interfaceIndex,
                FriendlyName = nic.Name,
                Description = nic.Description,
                IPv4 = ipv4,
                Gateway = gateway,
                Speed = speed,
                Status = nic.OperationalStatus,
                Type = nic.NetworkInterfaceType,
            });
        }

        return (adapters, addressMap);
    }

    private static int ResolveAdapterIndex(IPAddress localAddress, Dictionary<IPAddress, int> addressMap)
    {
        // "Any" address (0.0.0.0 or ::) means "listening on every adapter" -
        // genuinely not tied to one, not a resolution failure.
        if (IPAddress.Any.Equals(localAddress) || IPAddress.IPv6Any.Equals(localAddress))
        {
            return -1;
        }

        return addressMap.TryGetValue(localAddress, out int index) ? index : -1;
    }

    private static List<NetworkProcess> BuildProcessSnapshot()
    {
        (List<NetworkAdapter> adapters, Dictionary<IPAddress, int> addressMap) = EnumerateAdapters();

        // Dual-stack sockets mean an IPv4 client can appear only in the IPv6
        // table (as an IPv4-mapped address, already unmapped by
        // GetIPv6TcpTable) - both tables must be read to see every
        // connection Resource Monitor would show.
        List<TcpTableInterop.RawTcpRow> tcpRows = TcpTableInterop.GetIPv4TcpTable();
        tcpRows.AddRange(TcpTableInterop.GetIPv6TcpTable());

        var connectionsByPid = new Dictionary<int, List<Connection>>();

        foreach (TcpTableInterop.RawTcpRow row in tcpRows)
        {
            var connection = new Connection
            {
                Protocol = TransportProtocol.Tcp,
                LocalAddress = row.LocalAddress,
                LocalPort = row.LocalPort,
                RemoteAddress = row.RemoteAddress,
                RemotePort = row.RemotePort,
                State = (TcpState)row.State,
                AdapterIndex = ResolveAdapterIndex(row.LocalAddress, addressMap),
            };

            if (!connectionsByPid.TryGetValue(row.Pid, out List<Connection>? list))
            {
                list = new List<Connection>();
                connectionsByPid[row.Pid] = list;
            }

            list.Add(connection);
        }

        var processes = new List<NetworkProcess>();

        foreach ((int pid, List<Connection> connections) in connectionsByPid)
        {
            if (pid == 0)
            {
                continue; // the idle/system placeholder, not a real process
            }

            string processName;
            string? executablePath = null;
            Icon? icon = null;

            try
            {
                using Process process = Process.GetProcessById(pid);
                processName = process.ProcessName;

                try
                {
                    executablePath = process.MainModule?.FileName;
                }
                catch (Exception)
                {
                    // Protected/elevated process - path unavailable, not fatal.
                }

                if (executablePath != null)
                {
                    try
                    {
                        icon = Icon.ExtractAssociatedIcon(executablePath);
                    }
                    catch (Exception)
                    {
                        // Icon extraction can fail independently of path access.
                    }
                }
            }
            catch (ArgumentException)
            {
                continue; // process exited between snapshot and lookup
            }

            NetworkAdapter? resolvedAdapter = connections
                .Where(c => c.AdapterIndex != -1)
                .GroupBy(c => c.AdapterIndex)
                .OrderByDescending(g => g.Count())
                .Select(g => adapters.FirstOrDefault(a => a.InterfaceIndex == g.Key))
                .FirstOrDefault();

            processes.Add(new NetworkProcess
            {
                ProcessId = pid,
                ProcessName = processName,
                ExecutablePath = executablePath,
                Icon = icon,
                Connections = connections,
                ResolvedAdapter = resolvedAdapter,
            });
        }

        return processes;
    }
}
