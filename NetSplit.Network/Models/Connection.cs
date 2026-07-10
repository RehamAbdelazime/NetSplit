using System.Net;
using System.Net.NetworkInformation;

namespace NetSplit.Network;

public enum TransportProtocol
{
    Tcp,
}

public sealed class Connection
{
    public required TransportProtocol Protocol { get; init; }
    public required IPAddress LocalAddress { get; init; }
    public required int LocalPort { get; init; }
    public required IPAddress RemoteAddress { get; init; }
    public required int RemotePort { get; init; }
    public required TcpState State { get; init; }

    /// <summary>
    /// The InterfaceIndex of the adapter that owns LocalAddress, or -1 if it
    /// could not be resolved (e.g. the socket is unbound, 0.0.0.0).
    /// </summary>
    public required int AdapterIndex { get; init; }
}
