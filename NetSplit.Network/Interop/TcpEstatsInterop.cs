using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NetSplit.Network.Interop;

/// <summary>
/// Thin wrapper over Get/SetPerTcpConnectionEStats and their IPv6
/// counterparts Get/SetPerTcp6ConnectionEStats - the documented,
/// non-ETW way to read real cumulative byte counters for a single TCP
/// connection, for either protocol. This is the same mechanism Resource
/// Monitor and similar tools rely on for connection-level throughput.
///
/// The "Data" ESTATS type (byte counters) and its RW/ROD structures are
/// identical for IPv4 and IPv6 - only the row identifying the connection
/// differs. So the only protocol-specific code here is building that row;
/// enabling collection and reading the counters is one shared implementation
/// used by both protocols.
/// </summary>
internal static class TcpEstatsInterop
{
    // TCP_ESTATS_TYPE: TcpConnectionEstatsData is the second value (index 1).
    private const int TcpConnectionEstatsData = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCP6ROW
    {
        public uint State;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddr;
        public uint LocalScopeId;
        public uint LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] RemoteAddr;
        public uint RemoteScopeId;
        public uint RemotePort;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TCP_ESTATS_DATA_RW_v0
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool EnableCollection;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TCP_ESTATS_DATA_ROD_v0
    {
        public ulong DataBytesOut;
        public ulong DataSegsOut;
        public ulong DataBytesIn;
        public ulong DataSegsIn;
        public ulong SegsOut;
        public ulong SegsIn;
        public uint SoftErrors;
        public uint SoftErrorReason;
        public uint SndUna;
        public uint SndNxt;
        public uint SndMax;
        public ulong ThruBytesAcked;
        public uint RcvNxt;
        public ulong ThruBytesReceived;
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint SetPerTcpConnectionEStats(
        ref MIB_TCPROW row, int estatsType,
        ref TCP_ESTATS_DATA_RW_v0 rw, uint rwVersion, uint rwSize, uint offset);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetPerTcpConnectionEStats(
        ref MIB_TCPROW row, int estatsType,
        IntPtr rw, uint rwVersion, uint rwSize,
        IntPtr ros, uint rosVersion, uint rosSize,
        ref TCP_ESTATS_DATA_ROD_v0 rod, uint rodVersion, uint rodSize);

    [DllImport("iphlpapi.dll")]
    private static extern uint SetPerTcp6ConnectionEStats(
        ref MIB_TCP6ROW row, int estatsType,
        ref TCP_ESTATS_DATA_RW_v0 rw, uint rwVersion, uint rwSize, uint offset);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetPerTcp6ConnectionEStats(
        ref MIB_TCP6ROW row, int estatsType,
        IntPtr rw, uint rwVersion, uint rwSize,
        IntPtr ros, uint rosVersion, uint rosSize,
        ref TCP_ESTATS_DATA_ROD_v0 rod, uint rodVersion, uint rodSize);

    private delegate uint SetCollectionCall(ref TCP_ESTATS_DATA_RW_v0 rw, uint rwVersion, uint rwSize, uint offset);
    private delegate uint GetDataCall(ref TCP_ESTATS_DATA_ROD_v0 rod, uint rodVersion, uint rodSize);

    /// <summary>
    /// Turns on byte-counter collection for one connection. Must succeed
    /// before GetCumulativeBytes will return meaningful data for it. Safe to
    /// call repeatedly - enabling an already-enabled connection is a no-op.
    /// Works identically for IPv4 and IPv6; callers never need to branch.
    /// </summary>
    internal static bool TryEnableCollection(TcpTableInterop.RawTcpRow connection)
    {
        SetCollectionCall call = connection.IsIPv6Table
            ? BuildV6SetCall(connection)
            : BuildV4SetCall(connection);

        return TryEnableCollectionCore(call);
    }

    /// <summary>
    /// Reads current cumulative bytes sent/received for one connection.
    /// Returns false if stats aren't available yet (collection was just
    /// enabled and no data has accumulated) or the connection has already
    /// closed. Works identically for IPv4 and IPv6.
    /// </summary>
    internal static bool TryGetCumulativeBytes(TcpTableInterop.RawTcpRow connection, out ulong bytesOut, out ulong bytesIn)
    {
        GetDataCall call = connection.IsIPv6Table
            ? BuildV6GetCall(connection)
            : BuildV4GetCall(connection);

        return TryGetCumulativeBytesCore(call, out bytesOut, out bytesIn);
    }

    // --- Shared logic: identical for both protocols from here down. ---

    private static bool TryEnableCollectionCore(SetCollectionCall setCall)
    {
        var rw = new TCP_ESTATS_DATA_RW_v0 { EnableCollection = true };
        uint rwSize = (uint)Marshal.SizeOf<TCP_ESTATS_DATA_RW_v0>();
        return setCall(ref rw, 0, rwSize, 0) == 0;
    }

    private static bool TryGetCumulativeBytesCore(GetDataCall getCall, out ulong bytesOut, out ulong bytesIn)
    {
        bytesOut = 0;
        bytesIn = 0;

        var rod = new TCP_ESTATS_DATA_ROD_v0();
        uint rodSize = (uint)Marshal.SizeOf<TCP_ESTATS_DATA_ROD_v0>();

        if (getCall(ref rod, 0, rodSize) != 0)
        {
            return false;
        }

        bytesOut = rod.DataBytesOut;
        bytesIn = rod.DataBytesIn;
        return true;
    }

    // --- Protocol-specific: only builds the native row and binds it to the
    // matching P/Invoke entry point. Nothing else differs. ---

    private static SetCollectionCall BuildV4SetCall(TcpTableInterop.RawTcpRow connection)
    {
        MIB_TCPROW row = BuildV4Row(connection);
        return (ref TCP_ESTATS_DATA_RW_v0 rw, uint v, uint s, uint o) =>
            SetPerTcpConnectionEStats(ref row, TcpConnectionEstatsData, ref rw, v, s, o);
    }

    private static GetDataCall BuildV4GetCall(TcpTableInterop.RawTcpRow connection)
    {
        MIB_TCPROW row = BuildV4Row(connection);
        return (ref TCP_ESTATS_DATA_ROD_v0 rod, uint v, uint s) =>
            GetPerTcpConnectionEStats(ref row, TcpConnectionEstatsData, IntPtr.Zero, 0, 0, IntPtr.Zero, 0, 0, ref rod, v, s);
    }

    private static SetCollectionCall BuildV6SetCall(TcpTableInterop.RawTcpRow connection)
    {
        MIB_TCP6ROW row = BuildV6Row(connection);
        return (ref TCP_ESTATS_DATA_RW_v0 rw, uint v, uint s, uint o) =>
            SetPerTcp6ConnectionEStats(ref row, TcpConnectionEstatsData, ref rw, v, s, o);
    }

    private static GetDataCall BuildV6GetCall(TcpTableInterop.RawTcpRow connection)
    {
        MIB_TCP6ROW row = BuildV6Row(connection);
        return (ref TCP_ESTATS_DATA_ROD_v0 rod, uint v, uint s) =>
            GetPerTcp6ConnectionEStats(ref row, TcpConnectionEstatsData, IntPtr.Zero, 0, 0, IntPtr.Zero, 0, 0, ref rod, v, s);
    }

    private static MIB_TCPROW BuildV4Row(TcpTableInterop.RawTcpRow connection) => new()
    {
        State = connection.State,
        LocalAddr = BitConverter.ToUInt32(connection.LocalAddress.GetAddressBytes(), 0),
        LocalPort = connection.RawLocalPort,
        RemoteAddr = BitConverter.ToUInt32(connection.RemoteAddress.GetAddressBytes(), 0),
        RemotePort = connection.RawRemotePort,
    };

    // Rows from the IPv6 table may have had an IPv4-mapped address unwrapped
    // for display (see TcpTableInterop.NormalizeIfMapped). MapToIPv6() here
    // is the exact inverse of that unwrap - ::ffff:a.b.c.d round-trips
    // losslessly - so the OS still recognizes the connection this targets.
    private static MIB_TCP6ROW BuildV6Row(TcpTableInterop.RawTcpRow connection)
    {
        IPAddress local = connection.LocalAddress.AddressFamily == AddressFamily.InterNetwork
            ? connection.LocalAddress.MapToIPv6()
            : connection.LocalAddress;

        IPAddress remote = connection.RemoteAddress.AddressFamily == AddressFamily.InterNetwork
            ? connection.RemoteAddress.MapToIPv6()
            : connection.RemoteAddress;

        return new MIB_TCP6ROW
        {
            State = connection.State,
            LocalAddr = local.GetAddressBytes(),
            LocalScopeId = (uint)local.ScopeId,
            LocalPort = connection.RawLocalPort,
            RemoteAddr = remote.GetAddressBytes(),
            RemoteScopeId = (uint)remote.ScopeId,
            RemotePort = connection.RawRemotePort,
        };
    }
}
