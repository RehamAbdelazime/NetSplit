using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NetSplit.ui;

// Everything needed to (a) list processes with active TCP connections via
// GetExtendedTcpTable, and (b) push the one hardcoded rule to the driver via
// DeviceIoControl. No abstraction layers - this is a debug tool.
internal static class NativeInterop
{
    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const uint MIB_TCP_STATE_ESTAB = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;  // only low 2 bytes used, network byte order
        public uint RemoteAddr;
        public uint RemotePort;
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

    public sealed record TcpConnection(int Pid, IPAddress LocalAddress, bool Established);

    // Native GetExtendedTcpTable enumeration of every IPv4 TCP row currently on the system.
    public static List<TcpConnection> GetTcpConnections()
    {
        var results = new List<TcpConnection>();
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
                results.Add(new TcpConnection(
                    (int)row.OwningPid,
                    new IPAddress(row.LocalAddr),
                    row.State == MIB_TCP_STATE_ESTAB));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return results;
    }

    // --- Driver IOCTL ---

    private const uint FILE_DEVICE_UNKNOWN = 0x00000022;
    private const uint METHOD_BUFFERED = 0;
    private const uint FILE_ANY_ACCESS = 0;

    private static uint CtlCode(uint deviceType, uint function, uint method, uint access) =>
        (deviceType << 16) | (access << 14) | (function << 2) | method;

    private static readonly uint IOCTL_NETSPLIT_SET_TARGET =
        CtlCode(FILE_DEVICE_UNKNOWN, 0x800, METHOD_BUFFERED, FILE_ANY_ACCESS);

    private const string DevicePath = @"\\.\NetSplit";

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        byte[] lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint OPEN_EXISTING = 3;

    // Sends the single hardcoded rule (process name -> target adapter's IPv4) to the driver.
    // Mirrors NETSPLIT_SET_TARGET_REQUEST in the driver's Public.h: WCHAR[64] + UINT32.
    public static bool SetTarget(string processName, IPAddress targetLocalAddress)
    {
        using SafeFileHandle handle = CreateFile(
            DevicePath,
            GENERIC_READ | GENERIC_WRITE,
            0,
            IntPtr.Zero,
            OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            return false;
        }

        byte[] buffer = new byte[128 + 4];
        byte[] nameBytes = System.Text.Encoding.Unicode.GetBytes(processName);
        Array.Copy(nameBytes, buffer, Math.Min(nameBytes.Length, 126));

        byte[] addressBytes = targetLocalAddress.GetAddressBytes(); // already network-order octets
        Array.Copy(addressBytes, 0, buffer, 128, 4);

        return DeviceIoControl(
            handle,
            IOCTL_NETSPLIT_SET_TARGET,
            buffer,
            (uint)buffer.Length,
            IntPtr.Zero,
            0,
            out _,
            IntPtr.Zero);
    }
}
