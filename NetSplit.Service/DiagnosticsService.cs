using NetSplit.Core;
using NetSplit.Driver.Interop;
using NetSplit.Ipc;
using NetSplit.Runtime;

namespace NetSplit.Service;

/// <summary>
/// Aggregates everything the product should always be able to answer about
/// its own health into one clean, driver-implementation-free DTO. Nothing
/// downstream (the IPC layer, eventually the UI) ever sees a raw kernel
/// struct - GetStatus()/GetDiagnostics() are the only translation points.
/// </summary>
public sealed class DiagnosticsService
{
    private readonly IRoutingRuleService _rules;
    private readonly IDriverHost _driverHost;
    private readonly RuntimeResolver _resolver;
    private readonly RuntimeRuleSynchronizer _synchronizer;
    private readonly DiscoverySnapshotPump _pump;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly Guid _sessionId = Guid.NewGuid();

    public DiagnosticsService(
        IRoutingRuleService rules,
        IDriverHost driverHost,
        RuntimeResolver resolver,
        RuntimeRuleSynchronizer synchronizer,
        DiscoverySnapshotPump pump)
    {
        _rules = rules;
        _driverHost = driverHost;
        _resolver = resolver;
        _synchronizer = synchronizer;
        _pump = pump;
    }

    public ServiceStatusDto GetStatus()
    {
        DriverStatistics? stats = _driverHost.IsLoaded ? DriverClient.GetStatistics() : null;

        return new ServiceStatusDto(
            DriverLoaded: _driverHost.IsLoaded,
            DriverProtocolVersion: _driverHost.LastKnownVersion?.ProtocolVersion,
            DriverProtocolCompatible: _driverHost.LastKnownVersion?.IsCompatible ?? false,
            PermanentRuleCount: _rules.GetRules().Count,
            RuntimeRuleCount: _resolver.CurrentRuntimeRules.Count,
            DriverClassifyCount: stats?.ClassifyCount ?? 0,
            DriverRewriteSuccessCount: stats?.RewriteSuccessCount ?? 0,
            DriverRewriteFailureCount: stats?.RewriteFailureCount ?? 0,
            DriverIoctlFailureCount: stats?.IoctlFailureCount ?? 0,
            ServiceStartedAt: _startedAt,
            SessionId: _sessionId);
    }

    /// <summary>
    /// The full end-to-end trace snapshot. connectedUiClients is supplied by
    /// the caller (RoutingRuleIpcServer, which owns the client list) rather
    /// than fetched here, to avoid a circular Service&lt;-&gt;Ipc dependency.
    /// </summary>
    public DiagnosticsSnapshotDto GetDiagnostics(int connectedUiClients)
    {
        DriverDiagnostics? diag = _driverHost.IsLoaded ? DriverClient.GetDiagnostics() : null;

        // Queried unconditionally, regardless of IsLoaded - this is the
        // instrumented proof of *why* IsLoaded is what it is, not something
        // that should be skipped just because the driver looks unreachable.
        DeviceOpenDiagnostics deviceOpen = DriverClient.GetDeviceOpenDiagnostics();
        var deviceOpenDto = new DeviceOpenDiagnosticsDto(
            deviceOpen.DevicePathAttempted,
            deviceOpen.SymbolicLinkPath,
            deviceOpen.CreateFileSucceeded,
            deviceOpen.Win32ErrorCode,
            deviceOpen.Win32ErrorMessage,
            deviceOpen.SymbolicLinkExists,
            deviceOpen.SymbolicLinkTarget,
            deviceOpen.DeviceObjectExists,
            deviceOpen.DeviceObjectProbeWin32ErrorCode,
            deviceOpen.DeviceObjectProbeWin32ErrorMessage,
            deviceOpen.DriverServiceState,
            deviceOpen.DriverServiceStateDetail);

        DriverServiceStateInfo scmState = DriverClient.GetDriverServiceState();
        var scmStateDto = new ScmStateDto(
            scmState.State.ToString(),
            scmState.Detail,
            scmState.Win32ErrorCode,
            scmState.Win32ErrorMessage,
            scmState.StartPermissionAvailable);

        DriverBuildInfo buildInfo = DriverClient.GetDriverBuildInfo();
        var buildInfoDto = new DriverBuildInfoDto(
            buildInfo.ImagePath,
            buildInfo.FileWriteTimeUtc,
            buildInfo.FileSizeBytes,
            buildInfo.Detail);

        List<RuntimeRuleDto> runtimeRules = _resolver.CurrentRuntimeRules
            .Select(r => new RuntimeRuleDto(
                r.PermanentRuleId,
                r.Pid,
                r.ProcessName,
                r.TargetAdapter.InterfaceIndex,
                r.TargetAdapter.AdapterName,
                _synchronizer.PushedAddresses.TryGetValue(r.Pid, out System.Net.IPAddress? address) ? address.ToString() : null))
            .ToList();

        List<AdapterSnapshotDto> adapterCache = _pump.LatestAdapters
            .Select(a => new AdapterSnapshotDto(a.InterfaceIndex, a.FriendlyName, a.IPv4?.ToString()))
            .ToList();

        return new DiagnosticsSnapshotDto(
            PermanentRules: _rules.GetRules(),
            RuntimeRules: runtimeRules,
            DriverConnected: _driverHost.IsLoaded,
            DriverProtocolVersion: _driverHost.LastKnownVersion?.ProtocolVersion,
            DriverProtocolCompatible: _driverHost.LastKnownVersion?.IsCompatible ?? false,
            DriverAvailabilityState: _driverHost.State.ToString(),
            DriverAvailabilityDetail: _driverHost.StateDetail,
            ScmState: scmStateDto,
            DriverBuild: buildInfoDto,
            LastSuccessfulSyncAt: _synchronizer.LastSuccessfulSyncAt,
            LastFailedSyncAt: _synchronizer.LastFailedSyncAt,
            LastFailedSyncReason: _synchronizer.LastFailedSyncReason,
            ConnectedUiClients: connectedUiClients,
            AdapterCache: adapterCache,
            SyncPushSuccessCount: _synchronizer.PushSuccessCount,
            SyncPushFailureCount: _synchronizer.PushFailureCount,
            DriverClassifyCount: diag?.ClassifyCount ?? 0,
            DriverMatchedPidCount: diag?.MatchedPidCount ?? 0,
            DriverUnmatchedPidCount: diag?.UnmatchedPidCount ?? 0,
            DriverRewriteAttempts: diag?.RewriteAttempts ?? 0,
            DriverRewriteSuccessCount: diag?.RewriteSuccessCount ?? 0,
            DriverRewriteFailureCount: diag?.RewriteFailureCount ?? 0,
            DriverIoctlFailureCount: diag?.IoctlFailureCount ?? 0,
            DriverActiveRuleCount: diag?.ActiveRuleCount ?? 0,
            DriverLastMatchedPid: diag?.LastMatchedPid,
            DriverLastRewrittenAddress: diag?.LastRewrittenAddress,
            DeviceOpen: deviceOpenDto,
            SnapshotTakenAt: DateTimeOffset.UtcNow);
    }
}
