using System.Net;
using System.Net.NetworkInformation;

namespace NetSplit.Network;

public sealed class NetworkAdapter
{
    public required int InterfaceIndex { get; init; }
    public required string FriendlyName { get; init; }
    public required string Description { get; init; }
    public IPAddress? IPv4 { get; init; }
    public IPAddress? Gateway { get; init; }
    public long Speed { get; init; }
    public OperationalStatus Status { get; init; }
    public NetworkInterfaceType Type { get; init; }
}
