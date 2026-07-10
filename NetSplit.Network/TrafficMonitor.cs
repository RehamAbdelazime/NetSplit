using System.Net;
using NetSplit.Network.Interop;

namespace NetSplit.Network;

public sealed record TrafficRate(double SendBytesPerSecond, double ReceiveBytesPerSecond);

/// <summary>
/// Real, measured per-process network throughput - not simulated. Built on
/// Get/SetPerTcpConnectionEStats and their IPv6 counterparts (the same
/// documented mechanism Resource Monitor uses), not ETW: far less machinery
/// for the same per-connection byte counters, at the cost of needing two
/// samples before a rate is available for a given connection.
///
/// Stateful by design: call Sample() repeatedly (e.g. once per UI refresh
/// tick) and it diffs against its own previous call to compute a rate.
/// Protocol-agnostic throughout - a connection's identity is its
/// (local address, local port, remote address, remote port) tuple, exactly
/// as NetworkDiscoveryService identifies one, regardless of whether the
/// addresses are IPv4 or IPv6.
/// </summary>
public sealed class TrafficMonitor
{
    private readonly record struct ConnectionKey(IPAddress LocalAddress, int LocalPort, IPAddress RemoteAddress, int RemotePort);

    private sealed class ConnectionSample
    {
        public ulong BytesOut;
        public ulong BytesIn;
        public DateTime Timestamp;
    }

    private readonly Dictionary<ConnectionKey, ConnectionSample> _previousSamples = new();
    private readonly HashSet<ConnectionKey> _collectionEnabled = new();

    /// <summary>
    /// Takes one measurement and returns Send/Receive bytes-per-second per
    /// PID, based on the delta since the previous call. A process with no
    /// prior sample (just discovered, or every one of its connections is
    /// new) reports 0 until the next call.
    /// </summary>
    public IReadOnlyDictionary<int, TrafficRate> Sample()
    {
        // One combined scan per call, same growth pattern NetworkDiscoveryService
        // already uses - no extra table reads beyond the one v4 + one v6 pass
        // IPv6 support inherently requires.
        List<TcpTableInterop.RawTcpRow> rows = TcpTableInterop.GetIPv4TcpTable();
        rows.AddRange(TcpTableInterop.GetIPv6TcpTable());
        DateTime now = DateTime.UtcNow;

        var seenKeys = new HashSet<ConnectionKey>();
        var ratesByPid = new Dictionary<int, (double Send, double Receive)>();

        foreach (TcpTableInterop.RawTcpRow row in rows)
        {
            var key = new ConnectionKey(row.LocalAddress, row.LocalPort, row.RemoteAddress, row.RemotePort);
            seenKeys.Add(key);

            if (_collectionEnabled.Add(key))
            {
                // First time seeing this connection - turn on byte counting
                // for it. If this fails (e.g. connection closed between the
                // table snapshot and this call), it's simply skipped below.
                TcpEstatsInterop.TryEnableCollection(row);
            }

            if (!TcpEstatsInterop.TryGetCumulativeBytes(row, out ulong bytesOut, out ulong bytesIn))
            {
                continue;
            }

            if (_previousSamples.TryGetValue(key, out ConnectionSample? previous))
            {
                double elapsedSeconds = (now - previous.Timestamp).TotalSeconds;
                if (elapsedSeconds > 0)
                {
                    // Counters are cumulative and monotonic for the life of the
                    // connection; a negative delta would only mean the OS
                    // reused the tuple for a new connection - guard against it.
                    double sendRate = bytesOut >= previous.BytesOut ? (bytesOut - previous.BytesOut) / elapsedSeconds : 0;
                    double receiveRate = bytesIn >= previous.BytesIn ? (bytesIn - previous.BytesIn) / elapsedSeconds : 0;

                    (double Send, double Receive) existing = ratesByPid.GetValueOrDefault(row.Pid);
                    ratesByPid[row.Pid] = (existing.Send + sendRate, existing.Receive + receiveRate);
                }

                previous.BytesOut = bytesOut;
                previous.BytesIn = bytesIn;
                previous.Timestamp = now;
            }
            else
            {
                _previousSamples[key] = new ConnectionSample { BytesOut = bytesOut, BytesIn = bytesIn, Timestamp = now };
            }
        }

        // Drop bookkeeping for connections that no longer exist, so these
        // dictionaries don't grow unbounded over a long-running session.
        foreach (ConnectionKey staleKey in _previousSamples.Keys.Where(k => !seenKeys.Contains(k)).ToList())
        {
            _previousSamples.Remove(staleKey);
            _collectionEnabled.Remove(staleKey);
        }

        return ratesByPid.ToDictionary(kv => kv.Key, kv => new TrafficRate(kv.Value.Send, kv.Value.Receive));
    }
}
