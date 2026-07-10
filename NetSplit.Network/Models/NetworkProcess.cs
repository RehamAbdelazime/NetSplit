using System.Drawing;

namespace NetSplit.Network;

public sealed class NetworkProcess
{
    public required int ProcessId { get; init; }
    public required string ProcessName { get; init; }
    public string? ExecutablePath { get; init; }
    public Icon? Icon { get; init; }
    public required IReadOnlyList<Connection> Connections { get; init; }

    /// <summary>
    /// The adapter most of this process's connections are currently using,
    /// or null if none could be resolved.
    /// </summary>
    public NetworkAdapter? ResolvedAdapter { get; init; }
}
