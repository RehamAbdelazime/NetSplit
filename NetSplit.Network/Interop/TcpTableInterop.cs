using System.Net;
using System.Runtime.InteropServices;

namespace NetSplit.Network.Interop;

/// <summary>
/// Thin wrapper over the native GetExtendedTcpTable API - the only reliable
/// way to map a TCP connection to its owning process ID.
/// </summary>
internal static class TcpTableInterop
{
    private const int AF_INET = 2;
    private const int AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;  // low 2 bytes used, network byte order
        public uint RemoteAddr;
        public uint RemotePort; // low 2 bytes used, network byte order
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddr;
        public uint LocalScopeId;
        public uint LocalPort;   // low 2 bytes used, network byte order
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] RemoteAddr;
        public uint RemoteScopeId;
        public uint RemotePort;  // low 2 bytes used, network byte order
        public uint State;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwSize,
        bool bOrder,
        int ulAf,
        int tableClass,
        uint reserved);

    // One connection, one identity, regardless of protocol. LocalAddress/
    // RemoteAddress are always the *display* form (IPv4-mapped IPv6
    // addresses already unwrapped to plain IPv4 - see NormalizeIfMapped).
    // IsIPv6Table records which native GetExtendedTcpTable call actually
    // produced this row, which TcpEstatsInterop needs: a dual-stack
    // connection from an IPv4 client is unwrapped for display here, but the
    // OS still tracks it as an IPv6 connection internally, so the ESTATS
    // call must target it via GetPerTcp6ConnectionEStats, not the v4
    // variant, even though LocalAddress.AddressFamily now reads InterNetwork.
    internal readonly record struct RawTcpRow(
        int Pid,
        IPAddress LocalAddress,
        int LocalPort,
        IPAddress RemoteAddress,
        int RemotePort,
        uint State,
        bool IsIPv6Table,
        // Raw (network-byte-order DWORD) port values, exactly as the table
        // returned them - needed to rebuild the native row for ESTATS calls.
        uint RawLocalPort,
        uint RawRemotePort);

    private static int ExtractPort(uint rawPort) =>
        (ushort)IPAddress.NetworkToHostOrder((short)rawPort);

    internal static List<RawTcpRow> GetIPv4TcpTable()
    {
        var results = new List<RawTcpRow>();
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            uint ret = GetExtendedTcpTable(buffer, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            if (ret != 0)
            {
                return results;
            }

            int rowCount = Marshal.ReadInt32(buffer);
            IntPtr rowPtr = buffer + 4;
            int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();

            for (int i = 0; i < rowCount; i++)
            {
                var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr + i * rowSize);
                results.Add(new RawTcpRow(
                    (int)row.OwningPid,
                    new IPAddress(row.LocalAddr),
                    ExtractPort(row.LocalPort),
                    new IPAddress(row.RemoteAddr),
                    ExtractPort(row.RemotePort),
                    row.State,
                    IsIPv6Table: false,
                    row.LocalPort,
                    row.RemotePort));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return results;
    }

    // Dual-stack sockets are the reason this exists: a socket bound to "::"
    // that accepts an IPv4 client shows up ONLY in the IPv6 table, as an
    // IPv4-mapped address (::ffff:a.b.c.d) - never in GetIPv4TcpTable at
    // all. Skipping IPv6 entirely (the original implementation) silently
    // drops those connections, and any genuine IPv6 traffic, from the
    // snapshot.
    internal static List<RawTcpRow> GetIPv6TcpTable()
    {
        var results = new List<RawTcpRow>();
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, true, AF_INET6, TCP_TABLE_OWNER_PID_ALL, 0);

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            uint ret = GetExtendedTcpTable(buffer, ref size, true, AF_INET6, TCP_TABLE_OWNER_PID_ALL, 0);
            if (ret != 0)
            {
                return results;
            }

            int rowCount = Marshal.ReadInt32(buffer);
            IntPtr rowPtr = buffer + 4;
            int rowSize = Marshal.SizeOf<MIB_TCP6ROW_OWNER_PID>();

            for (int i = 0; i < rowCount; i++)
            {
                var row = Marshal.PtrToStructure<MIB_TCP6ROW_OWNER_PID>(rowPtr + i * rowSize);

                IPAddress local = NormalizeIfMapped(new IPAddress(row.LocalAddr, row.LocalScopeId));
                IPAddress remote = NormalizeIfMapped(new IPAddress(row.RemoteAddr, row.RemoteScopeId));

                results.Add(new RawTcpRow(
                    (int)row.OwningPid,
                    local,
                    ExtractPort(row.LocalPort),
                    remote,
                    ExtractPort(row.RemotePort),
                    row.State,
                    IsIPv6Table: true,
                    row.LocalPort,
                    row.RemotePort));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return results;
    }

    // ::ffff:a.b.c.d -> a.b.c.d. Without this, a plain IPv4 client
    // connecting to a dual-stack listener would compare against adapters'
    // real IPv4 addresses and never match. The reverse mapping (needed for
    // ESTATS on these rows) is exact and lossless - see TcpEstatsInterop.
    private static IPAddress NormalizeIfMapped(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
