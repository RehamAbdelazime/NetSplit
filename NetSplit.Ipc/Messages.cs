using NetSplit.Core;

namespace NetSplit.Ipc;

// Payload DTOs, one per Method value. Reuses NetSplit.Core's own records
// (RoutingRule, AdapterPreference, RuleTargetType) directly rather than
// duplicating them - they're already plain, JSON-friendly immutable data.

public static class IpcMethods
{
    public const string GetRules = nameof(GetRules);
    public const string GetRule = nameof(GetRule);
    public const string AddRule = nameof(AddRule);
    public const string UpdateRule = nameof(UpdateRule);
    public const string DeleteRule = nameof(DeleteRule);
    public const string EnableRule = nameof(EnableRule);
    public const string DisableRule = nameof(DisableRule);
    public const string GetStatus = nameof(GetStatus);
    public const string GetDiagnostics = nameof(GetDiagnostics);

    /// <summary>Notification method: pushed by the server whenever the rule store changes, from any cause (this client's own edit, another client's edit, or a load-time change).</summary>
    public const string RulesChanged = nameof(RulesChanged);
}

public sealed record GetRuleRequest(Guid Id);

public sealed record AddRuleRequest(string ProcessName, int? ProcessId, RuleTargetType TargetType, AdapterPreference TargetAdapter, bool Enabled);

public sealed record UpdateRuleRequest(Guid Id, string ProcessName, int? ProcessId, RuleTargetType TargetType, AdapterPreference TargetAdapter);

public sealed record RuleIdRequest(Guid Id);

public sealed record BoolResponse(bool Value);

/// <summary>
/// Answer to GetStatus - the UI's window into Service/driver health without
/// ever seeing a raw kernel struct. Numbers only; the UI decides how (or
/// whether) to display them.
/// </summary>
public sealed record ServiceStatusDto(
    bool DriverLoaded,
    int? DriverProtocolVersion,
    bool DriverProtocolCompatible,
    int PermanentRuleCount,
    int RuntimeRuleCount,
    ulong DriverClassifyCount,
    ulong DriverRewriteSuccessCount,
    ulong DriverRewriteFailureCount,
    ulong DriverIoctlFailureCount,
    DateTimeOffset ServiceStartedAt,
    Guid SessionId);

/// <summary>One runtime (PID-based) rule, plus what the DriverSynchronizer believes it actually pushed - the "Runtime Rule -> IOCTL" link in the routing trace.</summary>
public sealed record RuntimeRuleDto(
    Guid PermanentRuleId,
    int Pid,
    string ProcessName,
    int TargetInterfaceIndex,
    string TargetAdapterName,
    string? PushedAddress);

/// <summary>One adapter as the Service currently sees it - the "Current Adapter Cache" diagnostic.</summary>
public sealed record AdapterSnapshotDto(int InterfaceIndex, string FriendlyName, string? IPv4);

/// <summary>
/// Exact, unsummarized proof of why opening the driver's control device did
/// or didn't succeed - mirrors NetSplit.Driver.Interop.DeviceOpenDiagnostics
/// field-for-field so the UI never has to guess at what a flag means.
/// </summary>
public sealed record DeviceOpenDiagnosticsDto(
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

/// <summary>Raw SCM-level state for the NetSplit kernel service - independent of whether the control device can be opened. Mirrors NetSplit.Driver.Interop.DriverServiceStateInfo.</summary>
public sealed record ScmStateDto(
    string State,
    string Detail,
    int? Win32ErrorCode,
    string? Win32ErrorMessage,
    bool StartPermissionAvailable);

/// <summary>The on-disk driver .sys file's own metadata - not a value the driver reports over IOCTL. Mirrors NetSplit.Driver.Interop.DriverBuildInfo.</summary>
public sealed record DriverBuildInfoDto(
    string? ImagePath,
    DateTimeOffset? FileWriteTimeUtc,
    long? FileSizeBytes,
    string Detail);

/// <summary>
/// The full end-to-end diagnostics snapshot: every value needed to answer,
/// for any routing attempt, exactly which stage of
/// chrome.exe -> PID -> Runtime Rule -> IOCTL -> Kernel Cache -> Classify()
/// -> Rewrite -> Permit last succeeded. Distinct from ServiceStatusDto
/// (which stays small and cheap for frequent polling) - this is the
/// developer-diagnostics-window payload, refreshed at most once a second.
/// </summary>
public sealed record DiagnosticsSnapshotDto(
    // Service layer
    IReadOnlyList<RoutingRule> PermanentRules,
    IReadOnlyList<RuntimeRuleDto> RuntimeRules,
    bool DriverConnected,
    int? DriverProtocolVersion,
    bool DriverProtocolCompatible,
    string DriverAvailabilityState,
    string DriverAvailabilityDetail,
    ScmStateDto ScmState,
    DriverBuildInfoDto DriverBuild,
    DateTimeOffset? LastSuccessfulSyncAt,
    DateTimeOffset? LastFailedSyncAt,
    string? LastFailedSyncReason,
    int ConnectedUiClients,
    IReadOnlyList<AdapterSnapshotDto> AdapterCache,
    long SyncPushSuccessCount,
    long SyncPushFailureCount,
    // Driver layer
    ulong DriverClassifyCount,
    ulong DriverMatchedPidCount,
    ulong DriverUnmatchedPidCount,
    ulong DriverRewriteAttempts,
    ulong DriverRewriteSuccessCount,
    ulong DriverRewriteFailureCount,
    ulong DriverIoctlFailureCount,
    uint DriverActiveRuleCount,
    int? DriverLastMatchedPid,
    string? DriverLastRewrittenAddress,
    DeviceOpenDiagnosticsDto DeviceOpen,
    DateTimeOffset SnapshotTakenAt);
