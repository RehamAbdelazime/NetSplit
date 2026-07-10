using Microsoft.Extensions.Logging;
using NetSplit.Driver.Interop;

namespace NetSplit.Service;

/// <summary>
/// Read-only driver availability watcher. Queries the SCM (via
/// DriverClient.GetDriverServiceState, raw advapi32 P/Invoke - exact Win32
/// error codes, not ServiceController's exception-based ambiguity) and, if
/// the SCM reports Running, the control device itself
/// (DriverClient.GetVersion). Never calls StartService/ControlService or
/// anything that would - starting the driver is a deployment step (see
/// DEPLOYMENT_CHECKLIST.md), not something this class performs on the
/// caller's behalf. That split exists specifically so State always reflects
/// what NetSplit.Service can currently observe, never what it attempted to
/// change.
/// </summary>
public sealed class DriverHostService : IDriverHost
{
    private readonly ILogger<DriverHostService> _logger;
    private bool _wasLoaded;

    public bool IsLoaded { get; private set; }
    public DriverVersionInfo? LastKnownVersion { get; private set; }
    public DriverAvailabilityState State { get; private set; } = DriverAvailabilityState.Unknown;
    public string StateDetail { get; private set; } = "Not yet checked.";

    public event EventHandler? DriverReconnected;

    public DriverHostService(ILogger<DriverHostService> logger)
    {
        _logger = logger;
    }

    public Task RefreshAsync(CancellationToken cancellationToken)
    {
        DriverServiceStateInfo scmState = DriverClient.GetDriverServiceState();
        Console.WriteLine($"[TRACE DriverHostService] SCM GetDriverServiceState() = {scmState.State}, detail=\"{scmState.Detail}\""); // TEMPORARY INSTRUMENTATION

        if (scmState.State != DriverServiceState.Running)
        {
            Console.WriteLine("[TRACE DriverHostService] SCM state != Running -> SetState(loaded:false), GetVersion() is NOT called. THIS IS WHY DriverConnected=false."); // TEMPORARY INSTRUMENTATION
            SetState(ToAvailability(scmState.State), scmState.Detail, loaded: false, version: null);
            return Task.CompletedTask;
        }

        // SCM says Running - that only proves the service process exists;
        // it does not prove the control device is open and answering, so
        // this is verified independently rather than trusted.
        DriverVersionInfo? version = DriverClient.GetVersion();
        Console.WriteLine( // TEMPORARY INSTRUMENTATION
            $"[TRACE DriverHostService] DriverClient.GetVersion() = {(version == null ? "null" : $"ProtocolVersion={version.ProtocolVersion} IsCompatible={version.IsCompatible}")}");

        if (version == null)
        {
            Console.WriteLine("[TRACE DriverHostService] GetVersion() returned null -> DriverAvailabilityState.DeviceUnavailable, IsLoaded=false. THIS IS WHY DriverConnected=false."); // TEMPORARY INSTRUMENTATION
            SetState(
                DriverAvailabilityState.DeviceUnavailable,
                "SCM reports the driver service is Running, but its control device '\\\\.\\NetSplit' could not be opened (see DeviceOpenDiagnostics for the exact Win32 error). The service process may still be initializing, or WFP/device registration failed.",
                loaded: false,
                version: null);
            return Task.CompletedTask;
        }

        if (!version.IsCompatible)
        {
            Console.WriteLine($"[TRACE DriverHostService] version.IsCompatible=false (driver reports {version.ProtocolVersion}, expected {DriverProtocol.Version}) -> IsLoaded=false. THIS IS WHY DriverConnected=false."); // TEMPORARY INSTRUMENTATION
            SetState(
                DriverAvailabilityState.Running,
                $"Driver running and reachable, but protocol version {version.ProtocolVersion} is incompatible with this Service build (expects {DriverProtocol.Version}). Runtime rule synchronization is disabled until versions match.",
                loaded: false,
                version: version);
            return Task.CompletedTask;
        }

        SetState(DriverAvailabilityState.Running, "Driver running and reachable; protocol version compatible.", loaded: true, version: version);

        if (!_wasLoaded)
        {
            _logger.LogInformation(
                "Driver connected: protocol version {ProtocolVersion}, IPv4 redirect={IPv4}, IPv6 redirect={IPv6}.",
                version.ProtocolVersion, version.SupportsIPv4Redirect, version.SupportsIPv6Redirect);
            DriverReconnected?.Invoke(this, EventArgs.Empty);
        }

        _wasLoaded = true;
        return Task.CompletedTask;
    }

    private void SetState(DriverAvailabilityState state, string detail, bool loaded, DriverVersionInfo? version)
    {
        if (!loaded)
        {
            _wasLoaded = false;
        }

        // Only log on an actual change - this runs every discovery tick
        // (every 2s), and re-logging an unchanged "Stopped" state that
        // deployment simply hasn't addressed yet would just be noise.
        if (State != state || StateDetail != detail)
        {
            _logger.LogInformation("Driver availability: {State} - {Detail}", state, detail);
        }

        State = state;
        StateDetail = detail;
        IsLoaded = loaded;

        if (version != null)
        {
            LastKnownVersion = version;
        }
    }

    private static DriverAvailabilityState ToAvailability(DriverServiceState scmState) => scmState switch
    {
        DriverServiceState.NotInstalled => DriverAvailabilityState.NotInstalled,
        DriverServiceState.AccessDenied => DriverAvailabilityState.AccessDenied,
        DriverServiceState.Stopped => DriverAvailabilityState.Stopped,
        DriverServiceState.Running => DriverAvailabilityState.Running,
        _ => DriverAvailabilityState.Unknown,
    };
}
