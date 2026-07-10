using System.ComponentModel;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NetSplit.Driver.Interop;

/// <summary>
/// The SCM's own recorded state for the NetSplit kernel service - queried
/// directly via OpenSCManager/OpenService/QueryServiceStatus (raw advapi32
/// P/Invoke, not System.ServiceProcess.ServiceController's exception-based
/// API), so "not installed" and "access denied" are two distinct, exactly-
/// observed Win32 outcomes instead of both collapsing into the same caught
/// InvalidOperationException. Never used to start/stop the service - see
/// DEPLOYMENT_CHECKLIST.md for how it gets started.
/// </summary>
public enum DriverServiceState
{
    Unknown,
    NotInstalled,
    AccessDenied,
    Stopped,
    Running,
}

/// <summary>Exact SCM-level facts, independent of whether the control device can actually be opened - see DeviceOpenDiagnostics for that.</summary>
public sealed record DriverServiceStateInfo(
    DriverServiceState State,
    string Detail,
    int? Win32ErrorCode,
    string? Win32ErrorMessage,
    bool StartPermissionAvailable);

/// <summary>
/// The on-disk .sys file's own metadata, resolved via the SCM's ImagePath
/// for the NetSplit service (QueryServiceConfig) - not a value the driver
/// reports over IOCTL, since no build identifier exists on that wire format
/// and this task does not add one. "Build" here means exactly what it says:
/// the binary's last-write timestamp and size on disk, not a version string.
/// </summary>
public sealed record DriverBuildInfo(
    string? ImagePath,
    DateTimeOffset? FileWriteTimeUtc,
    long? FileSizeBytes,
    string Detail);

/// <summary>
/// The protocol version this build of NetSplit.Driver.Interop was written
/// against. Compared against the driver's own NETSPLIT_PROTOCOL_VERSION at
/// startup (see DriverClient.GetVersion) - a mismatch means the driver and
/// this client disagree about struct layout and must not talk further.
/// </summary>
public static class DriverProtocol
{
    public const int Version = 1;
}

/// <summary>Clean, driver-implementation-free view of the kernel's version/capability response.</summary>
public sealed record DriverVersionInfo(int ProtocolVersion, bool SupportsIPv4Redirect, bool SupportsIPv6Redirect)
{
    public bool IsCompatible => ProtocolVersion == DriverProtocol.Version;
}

/// <summary>Clean, driver-implementation-free view of the kernel's statistics response.</summary>
public sealed record DriverStatistics(
    ulong ClassifyCount,
    ulong RewriteSuccessCount,
    ulong RewriteFailureCount,
    ulong IoctlFailureCount,
    uint ActiveRuleCount);

/// <summary>
/// Clean, driver-implementation-free view of the kernel's diagnostics
/// response - a superset of DriverStatistics that additionally answers
/// "did classify() find the PID" (MatchedPidCount/UnmatchedPidCount) and
/// "was the rewritten address the expected one" (LastMatchedPid/
/// LastRewrittenAddress), the two links in the routing trace DriverStatistics
/// alone can't answer.
/// </summary>
public sealed record DriverDiagnostics(
    int ProtocolVersion,
    ulong ClassifyCount,
    ulong MatchedPidCount,
    ulong UnmatchedPidCount,
    ulong RewriteAttempts,
    ulong RewriteSuccessCount,
    ulong RewriteFailureCount,
    ulong IoctlFailureCount,
    uint ActiveRuleCount,
    int? LastMatchedPid,
    string? LastRewrittenAddress);

/// <summary>
/// Exact, unsummarized proof of why opening the driver's control device did
/// or didn't succeed - the evidence behind DiagnosticsSnapshotDto's
/// DriverConnected flag. Every field here is either a raw Win32 fact
/// (CreateFile's own return value and GetLastError(), captured
/// immediately, before any other API call can overwrite it) or an
/// independent, separately-verified fact (the symbolic link queried via
/// QueryDosDeviceW, the device object probed via a second CreateFile
/// through the \GLOBALROOT NT-namespace path - NOT inferred from the first
/// CreateFile's error code - and the SCM's own service state).
/// </summary>
public sealed record DeviceOpenDiagnostics(
    string DevicePathAttempted,
    string SymbolicLinkPath,
    bool CreateFileSucceeded,
    int Win32ErrorCode,
    string Win32ErrorMessage,
    bool SymbolicLinkExists,
    string? SymbolicLinkTarget,
    bool DeviceObjectExists,
    int DeviceObjectProbeWin32ErrorCode,
    string DeviceObjectProbeWin32ErrorMessage,
    string DriverServiceState,
    string DriverServiceStateDetail);

/// <summary>
/// Talks to the NetSplit driver's control device. Every call opens and
/// closes its own handle - no persistent connection to manage, and it's
/// fine if the driver isn't loaded yet (each call just fails independently,
/// returning false/null). The wire format here must stay byte-for-byte
/// identical to NetSplit.driver/Public.h.
///
/// The driver owns ONLY runtime (PID-based) rules - there is no executable
/// name anywhere in this contract, by design.
///
/// This is the ONLY class in the entire solution that calls CreateFile or
/// DeviceIoControl against the driver device - enforced by convention (no
/// other project references the Win32 P/Invoke signatures), matching the
/// architecture's "Driver.Interop owns all DeviceIoControl communication"
/// rule.
/// </summary>
public static class DriverClient
{
    private const string DevicePath = @"\\.\NetSplit";
    private const int AF_INET = 2;

    private const uint FILE_DEVICE_UNKNOWN = 0x00000022;
    private const uint METHOD_BUFFERED = 0;
    private const uint FILE_ANY_ACCESS = 0;

    private static uint CtlCode(uint function) =>
        (FILE_DEVICE_UNKNOWN << 16) | (FILE_ANY_ACCESS << 14) | (function << 2) | METHOD_BUFFERED;

    private static readonly uint IOCTL_ADD_RUNTIME_RULE = CtlCode(0x820);
    private static readonly uint IOCTL_REMOVE_RUNTIME_RULE = CtlCode(0x821);
    private static readonly uint IOCTL_CLEAR_RUNTIME_RULES = CtlCode(0x822);
    private static readonly uint IOCTL_GET_VERSION = CtlCode(0x823);
    private static readonly uint IOCTL_GET_STATISTICS = CtlCode(0x824);
    private static readonly uint IOCTL_GET_DIAGNOSTICS = CtlCode(0x825);

    private const uint CAPABILITY_IPV4_REDIRECT = 0x00000001u;
    private const uint CAPABILITY_IPV6_REDIRECT = 0x00000002u;

    [StructLayout(LayoutKind.Sequential)]
    private struct NETSPLIT_VERSION_INFO
    {
        public uint ProtocolVersion;
        public uint Capabilities;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NETSPLIT_STATISTICS
    {
        public ulong ClassifyCount;
        public ulong RewriteSuccessCount;
        public ulong RewriteFailureCount;
        public ulong IoctlFailureCount;
        public uint ActiveRuleCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NETSPLIT_DIAGNOSTICS
    {
        public uint ProtocolVersion;
        public uint Capabilities;
        public ulong ClassifyCount;
        public ulong MatchedPidCount;
        public ulong UnmatchedPidCount;
        public ulong RewriteAttempts;
        public ulong RewriteSuccessCount;
        public ulong RewriteFailureCount;
        public ulong IoctlFailureCount;
        public uint ActiveRuleCount;
        public int LastMatchedPid;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public byte[] LastRewrittenAddress;
    }

    // sizeof(NETSPLIT_RUNTIME_RULE): INT32 Pid + UINT16 AddressFamily + UCHAR[16] TargetAddress + BOOLEAN Enabled,
    // naturally aligned (4 + 2 + 16 + 1 = 23, padded to 24 for the trailing BOOLEAN's own alignment as the compiler lays it out;
    // computed via Marshal.SizeOf below rather than hardcoded, so a struct layout change can't silently desync the two sides).
    [StructLayout(LayoutKind.Sequential)]
    private struct NETSPLIT_RUNTIME_RULE
    {
        public int Pid;
        public ushort AddressFamily;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] TargetAddress;
        [MarshalAs(UnmanagedType.U1)]
        public bool Enabled;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NETSPLIT_REMOVE_RUNTIME_RULE_REQUEST
    {
        public int Pid;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        byte[]? lpInBuffer, uint nInBufferSize,
        byte[]? lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint QueryDosDeviceW(string lpDeviceName, char[] lpTargetPath, uint ucchMax);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManagerW(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenServiceW(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr hService, out SERVICE_STATUS lpServiceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceConfigW(IntPtr hService, IntPtr lpServiceConfig, uint cbBufSize, out uint pcbBytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr hSCObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct QUERY_SERVICE_CONFIGW
    {
        public uint dwServiceType;
        public uint dwStartType;
        public uint dwErrorControl;
        public IntPtr lpBinaryPathName;
        public IntPtr lpLoadOrderGroup;
        public uint dwTagId;
        public IntPtr lpDependencies;
        public IntPtr lpServiceStartName;
        public IntPtr lpDisplayName;
    }

    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SERVICE_QUERY_STATUS = 0x0004;
    private const uint SERVICE_QUERY_CONFIG = 0x0001;
    private const uint SERVICE_START = 0x0010;
    private const uint SERVICE_RUNNING = 0x00000004;
    private const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;
    private const int ERROR_ACCESS_DENIED = 5;

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint OPEN_EXISTING = 3;

    // \DosDevices\NetSplit, as created by the driver's IoCreateSymbolicLink
    // call (NETSPLIT_SYMLINK_NAME in Public.h) - this is what CreateFile's
    // "\\.\NetSplit" path resolves through to reach the device object.
    private const string SymbolicLinkPath = @"\DosDevices\NetSplit";
    private const string SymbolicLinkQueryName = "NetSplit";

    // Bypasses the \DosDevices symbolic link entirely and opens the device
    // object directly by its NT-namespace path (\Device\NetSplit) through
    // the well-known \\.\GLOBALROOT prefix. This is what makes
    // DeviceObjectExists an independently-verified fact rather than an
    // inference from the first CreateFile's error code: a dangling/missing
    // symlink and a missing device object now produce two different,
    // separately-observed results instead of being conflated into one.
    private const string GlobalRootDevicePath = @"\\.\GLOBALROOT\Device\NetSplit";

    private const string DriverServiceName = "NetSplit";

    /// <summary>
    /// Instrumented open: captures GetLastError() immediately after
    /// CreateFile, before any other Win32/managed call has a chance to
    /// overwrite it, so a caller asking for the raw error always gets the
    /// exact one CreateFile itself set.
    /// </summary>
    private static SafeFileHandle? OpenDevice(out int win32Error)
    {
        SafeFileHandle handle = CreateFile(
            DevicePath, GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        win32Error = Marshal.GetLastWin32Error();
        return handle.IsInvalid ? null : handle;
    }

    private static SafeFileHandle? OpenDevice() => OpenDevice(out _);

    private static byte[] StructToBytes<T>(T value) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        byte[] buffer = new byte[size];
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, ptr, false);
            Marshal.Copy(ptr, buffer, 0, size);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
        return buffer;
    }

    private static T BytesToStruct<T>(byte[] bytes) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(bytes, 0, ptr, size);
            return Marshal.PtrToStructure<T>(ptr)!;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>
    /// Pushes/updates the rule for one running process: this PID's outbound
    /// TCP binds will be rewritten to targetAddress. IPv4 only for now -
    /// the wire format reserves room for IPv6, but the driver rejects
    /// anything that isn't AF_INET this pass (matches the callout, which is
    /// only registered at the IPv4 bind-redirect layer today).
    /// </summary>
    public static bool AddRuntimeRule(int pid, IPAddress targetAddress)
    {
        if (targetAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        using SafeFileHandle? handle = OpenDevice();
        if (handle == null)
        {
            return false;
        }

        byte[] addressBytes = new byte[16];
        Array.Copy(targetAddress.GetAddressBytes(), addressBytes, 4);

        var request = new NETSPLIT_RUNTIME_RULE
        {
            Pid = pid,
            AddressFamily = AF_INET,
            TargetAddress = addressBytes,
            Enabled = true,
        };

        byte[] buffer = StructToBytes(request);
        return DeviceIoControl(handle, IOCTL_ADD_RUNTIME_RULE, buffer, (uint)buffer.Length, null, 0, out _, IntPtr.Zero);
    }

    /// <summary>
    /// Semantically distinct from AddRuntimeRule at the call site (updating
    /// an existing PID's target vs. creating a new rule), but the same wire
    /// operation - the kernel's AddRule already replaces any existing rule
    /// for the same PID, so there is no separate kernel-side "update" and
    /// none is needed.
    /// </summary>
    public static bool UpdateRuntimeRule(int pid, IPAddress targetAddress) => AddRuntimeRule(pid, targetAddress);

    public static bool RemoveRuntimeRule(int pid)
    {
        using SafeFileHandle? handle = OpenDevice();
        if (handle == null)
        {
            return false;
        }

        byte[] buffer = StructToBytes(new NETSPLIT_REMOVE_RUNTIME_RULE_REQUEST { Pid = pid });
        return DeviceIoControl(handle, IOCTL_REMOVE_RUNTIME_RULE, buffer, (uint)buffer.Length, null, 0, out _, IntPtr.Zero);
    }

    public static bool ClearRuntimeRules()
    {
        using SafeFileHandle? handle = OpenDevice();
        if (handle == null)
        {
            return false;
        }

        return DeviceIoControl(handle, IOCTL_CLEAR_RUNTIME_RULES, null, 0, null, 0, out _, IntPtr.Zero);
    }

    /// <summary>
    /// Queries the driver's protocol version and capabilities. Callers
    /// (DriverHost, at Service startup) must check the result's
    /// IsCompatible before issuing any other IOCTL - a version mismatch
    /// means the wire structs may not agree and must not be used blindly.
    /// Returns null if the driver isn't loaded/reachable at all.
    /// </summary>
    public static DriverVersionInfo? GetVersion()
    {
        using SafeFileHandle? handle = OpenDevice();
        if (handle == null)
        {
            return null;
        }

        byte[] outBuffer = new byte[Marshal.SizeOf<NETSPLIT_VERSION_INFO>()];
        if (!DeviceIoControl(handle, IOCTL_GET_VERSION, null, 0, outBuffer, (uint)outBuffer.Length, out uint bytesReturned, IntPtr.Zero)
            || bytesReturned < outBuffer.Length)
        {
            return null;
        }

        NETSPLIT_VERSION_INFO info = BytesToStruct<NETSPLIT_VERSION_INFO>(outBuffer);
        return new DriverVersionInfo(
            (int)info.ProtocolVersion,
            (info.Capabilities & CAPABILITY_IPV4_REDIRECT) != 0,
            (info.Capabilities & CAPABILITY_IPV6_REDIRECT) != 0);
    }

    /// <summary>Queries live driver counters for diagnostics. Returns null if the driver isn't loaded/reachable.</summary>
    public static DriverStatistics? GetStatistics()
    {
        using SafeFileHandle? handle = OpenDevice();
        if (handle == null)
        {
            return null;
        }

        byte[] outBuffer = new byte[Marshal.SizeOf<NETSPLIT_STATISTICS>()];
        if (!DeviceIoControl(handle, IOCTL_GET_STATISTICS, null, 0, outBuffer, (uint)outBuffer.Length, out uint bytesReturned, IntPtr.Zero)
            || bytesReturned < outBuffer.Length)
        {
            return null;
        }

        NETSPLIT_STATISTICS stats = BytesToStruct<NETSPLIT_STATISTICS>(outBuffer);
        return new DriverStatistics(
            stats.ClassifyCount, stats.RewriteSuccessCount, stats.RewriteFailureCount,
            stats.IoctlFailureCount, stats.ActiveRuleCount);
    }

    /// <summary>
    /// Queries the full diagnostics surface - everything DriverStatistics
    /// has, plus the matched/unmatched-PID and last-matched/last-rewritten
    /// counters needed to answer "did classify() find the PID" and "was the
    /// rewritten address the expected one". Returns null if the driver
    /// isn't loaded/reachable.
    /// </summary>
    public static DriverDiagnostics? GetDiagnostics()
    {
        using SafeFileHandle? handle = OpenDevice();
        if (handle == null)
        {
            return null;
        }

        byte[] outBuffer = new byte[Marshal.SizeOf<NETSPLIT_DIAGNOSTICS>()];
        if (!DeviceIoControl(handle, IOCTL_GET_DIAGNOSTICS, null, 0, outBuffer, (uint)outBuffer.Length, out uint bytesReturned, IntPtr.Zero)
            || bytesReturned < outBuffer.Length)
        {
            return null;
        }

        NETSPLIT_DIAGNOSTICS diag = BytesToStruct<NETSPLIT_DIAGNOSTICS>(outBuffer);
        return new DriverDiagnostics(
            (int)diag.ProtocolVersion,
            diag.ClassifyCount,
            diag.MatchedPidCount,
            diag.UnmatchedPidCount,
            diag.RewriteAttempts,
            diag.RewriteSuccessCount,
            diag.RewriteFailureCount,
            diag.IoctlFailureCount,
            diag.ActiveRuleCount,
            diag.LastMatchedPid > 0 ? diag.LastMatchedPid : null,
            diag.LastRewrittenAddress is [0, 0, 0, 0] ? null : string.Join('.', diag.LastRewrittenAddress));
    }

    /// <summary>
    /// Proves - does not infer, does not summarize - exactly why opening
    /// \\.\NetSplit did or didn't succeed. Every fact below is independently
    /// captured: CreateFile's own return value and the GetLastError() it set
    /// (via the instrumented OpenDevice overload, not a fresh guess);
    /// whether the \DosDevices\NetSplit symbolic link exists (QueryDosDeviceW,
    /// a completely separate OS call from CreateFile); whether the device
    /// object itself exists (a second, independent CreateFile through the
    /// \\.\GLOBALROOT\Device\NetSplit NT-namespace path, which bypasses the
    /// symbolic link entirely - this is what lets "symlink missing" and
    /// "device object missing" be told apart instead of conflated); and the
    /// SCM's own recorded state for the NetSplit kernel service.
    /// </summary>
    public static DeviceOpenDiagnostics GetDeviceOpenDiagnostics()
    {
        using SafeFileHandle? handle = OpenDevice(out int win32Error);
        bool createFileSucceeded = handle != null;
        string win32Message = new Win32Exception(win32Error).Message;

        bool symlinkExists = TryQuerySymbolicLink(out string? symlinkTarget);

        bool deviceObjectExists = TryOpenGlobalRootDevicePath(out int globalRootError);
        string globalRootMessage = new Win32Exception(globalRootError).Message;

        DriverServiceStateInfo scmState = GetDriverServiceState();

        return new DeviceOpenDiagnostics(
            DevicePathAttempted: DevicePath,
            SymbolicLinkPath: SymbolicLinkPath,
            CreateFileSucceeded: createFileSucceeded,
            Win32ErrorCode: win32Error,
            Win32ErrorMessage: win32Message,
            SymbolicLinkExists: symlinkExists,
            SymbolicLinkTarget: symlinkTarget,
            DeviceObjectExists: deviceObjectExists,
            DeviceObjectProbeWin32ErrorCode: globalRootError,
            DeviceObjectProbeWin32ErrorMessage: globalRootMessage,
            DriverServiceState: scmState.State.ToString(),
            DriverServiceStateDetail: scmState.Detail);
    }

    /// <summary>
    /// Queries the SCM directly for the NetSplit kernel service's own
    /// recorded state. Distinguishes NotInstalled from AccessDenied from
    /// Stopped from Running by their exact Win32 error codes, rather than
    /// inferring from one caught exception type the way
    /// System.ServiceProcess.ServiceController's API would. Never calls
    /// StartService/ControlService - starting the driver is a deployment
    /// step (see DEPLOYMENT_CHECKLIST.md), never something this method (or
    /// anything that calls it) does on the caller's behalf. The
    /// StartPermissionAvailable field is purely informational - a second,
    /// separate OpenService probe requesting SERVICE_START rights that is
    /// immediately closed without ever calling StartService.
    /// </summary>
    public static DriverServiceStateInfo GetDriverServiceState()
    {
        IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            string msg = new Win32Exception(err).Message;
            return new DriverServiceStateInfo(DriverServiceState.Unknown, $"OpenSCManager failed: {msg}", err, msg, false);
        }

        try
        {
            IntPtr svcQuery = OpenServiceW(scm, DriverServiceName, SERVICE_QUERY_STATUS);
            if (svcQuery == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                string msg = new Win32Exception(err).Message;

                if (err == ERROR_SERVICE_DOES_NOT_EXIST)
                {
                    return new DriverServiceStateInfo(
                        DriverServiceState.NotInstalled,
                        $"Service '{DriverServiceName}' is not installed (OpenService returned ERROR_SERVICE_DOES_NOT_EXIST): {msg}",
                        err, msg, false);
                }

                if (err == ERROR_ACCESS_DENIED)
                {
                    return new DriverServiceStateInfo(
                        DriverServiceState.AccessDenied,
                        $"Access denied querying service '{DriverServiceName}' status (OpenService returned ERROR_ACCESS_DENIED): {msg}",
                        err, msg, false);
                }

                return new DriverServiceStateInfo(DriverServiceState.Unknown, $"OpenService(SERVICE_QUERY_STATUS) failed: {msg}", err, msg, false);
            }

            DriverServiceState runState;
            try
            {
                if (!QueryServiceStatus(svcQuery, out SERVICE_STATUS status))
                {
                    int err = Marshal.GetLastWin32Error();
                    string msg = new Win32Exception(err).Message;
                    return new DriverServiceStateInfo(DriverServiceState.Unknown, $"QueryServiceStatus failed: {msg}", err, msg, false);
                }

                runState = status.dwCurrentState == SERVICE_RUNNING ? DriverServiceState.Running : DriverServiceState.Stopped;
            }
            finally
            {
                CloseServiceHandle(svcQuery);
            }

            // Informational-only probe: does this process hold SERVICE_START
            // rights? Opened and immediately closed - StartService is never
            // called here or anywhere this result is consumed.
            bool startPermissionAvailable = false;
            IntPtr svcStartProbe = OpenServiceW(scm, DriverServiceName, SERVICE_QUERY_STATUS | SERVICE_START);
            if (svcStartProbe != IntPtr.Zero)
            {
                startPermissionAvailable = true;
                CloseServiceHandle(svcStartProbe);
            }

            string detail = runState == DriverServiceState.Running
                ? $"SCM reports service '{DriverServiceName}' is Running."
                : $"SCM reports service '{DriverServiceName}' status = Stopped. Starting it is a deployment step (see DEPLOYMENT_CHECKLIST.md), not something NetSplit.Service performs automatically."
                  + (startPermissionAvailable ? string.Empty : " This process also does not currently hold SERVICE_START rights on it (would need to run elevated to start it manually).");

            return new DriverServiceStateInfo(runState, detail, null, null, startPermissionAvailable);
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    /// <summary>
    /// The .sys file's own on-disk metadata (last-write time, size),
    /// resolved via the SCM's ImagePath for the NetSplit service. Not a
    /// value the driver reports over IOCTL - there is no build identifier
    /// on that wire format, and this task does not add one.
    /// </summary>
    public static DriverBuildInfo GetDriverBuildInfo()
    {
        IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero)
        {
            return new DriverBuildInfo(null, null, null, $"OpenSCManager failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        }

        try
        {
            IntPtr svc = OpenServiceW(scm, DriverServiceName, SERVICE_QUERY_CONFIG);
            if (svc == IntPtr.Zero)
            {
                return new DriverBuildInfo(null, null, null, $"OpenService(SERVICE_QUERY_CONFIG) failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            }

            try
            {
                QueryServiceConfigW(svc, IntPtr.Zero, 0, out uint neededBytes);
                if (neededBytes == 0)
                {
                    return new DriverBuildInfo(null, null, null, $"QueryServiceConfig size probe failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
                }

                IntPtr buffer = Marshal.AllocHGlobal((int)neededBytes);
                try
                {
                    if (!QueryServiceConfigW(svc, buffer, neededBytes, out _))
                    {
                        return new DriverBuildInfo(null, null, null, $"QueryServiceConfig failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
                    }

                    QUERY_SERVICE_CONFIGW config = Marshal.PtrToStructure<QUERY_SERVICE_CONFIGW>(buffer);
                    string? imagePath = Marshal.PtrToStringUni(config.lpBinaryPathName);
                    if (string.IsNullOrEmpty(imagePath))
                    {
                        return new DriverBuildInfo(null, null, null, "Service config has no ImagePath.");
                    }

                    // Kernel-service ImagePaths are typically NT-namespace
                    // paths (\??\C:\...) rather than plain Win32 paths.
                    string normalizedPath = imagePath.StartsWith(@"\??\", StringComparison.Ordinal) ? imagePath[4..] : imagePath;

                    if (!File.Exists(normalizedPath))
                    {
                        return new DriverBuildInfo(normalizedPath, null, null, $"ImagePath '{normalizedPath}' does not exist on disk.");
                    }

                    var fileInfo = new FileInfo(normalizedPath);
                    return new DriverBuildInfo(
                        normalizedPath,
                        fileInfo.LastWriteTimeUtc,
                        fileInfo.Length,
                        "Build timestamp is the .sys file's last-write time on disk - the wire format has no in-driver build identifier.");
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseServiceHandle(svc);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    private static bool TryQuerySymbolicLink(out string? target)
    {
        char[] buffer = new char[260];
        uint length = QueryDosDeviceW(SymbolicLinkQueryName, buffer, (uint)buffer.Length);
        if (length == 0)
        {
            target = null;
            return false;
        }

        // QueryDosDeviceW returns one or more NUL-terminated strings packed
        // into the buffer; the symbolic link's target is the first one.
        int terminator = Array.IndexOf(buffer, '\0');
        target = new string(buffer, 0, terminator >= 0 ? terminator : (int)length);
        return true;
    }

    private static bool TryOpenGlobalRootDevicePath(out int win32Error)
    {
        using SafeFileHandle handle = CreateFile(
            GlobalRootDevicePath, GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        win32Error = Marshal.GetLastWin32Error();
        return !handle.IsInvalid;
    }

}
