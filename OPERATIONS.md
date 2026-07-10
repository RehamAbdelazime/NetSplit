# NetSplit — Recovery and Deployment

This document covers two things that are architectural but not expressed as
runtime code: what happens when each component fails (recovery), and how the
three components (Driver, Service, UI) get onto a machine (deployment). Both
were explicit requirements for the Service migration; this is the design
record for the parts that are either already wired in code (referenced by
file/line) or that require an install-time step this dev environment can't
perform (unelevated, no code-signing cert, no WiX/MSI tooling installed).

## Recovery scenarios

| Failure | Detection | Recovery | Where |
|---|---|---|---|
| Driver unloaded/crashed | `DriverHostService.CheckAndRecoverAsync` calls `DriverClient.GetVersion()` every tick (2s); failure means the device handle is gone | Starts the `NetSplit` driver service via `ServiceController`, fires `DriverReconnected` | `NetSplit.Service/DriverHostService.cs` |
| Driver's kernel rule table came back empty after a reconnect | `DriverReconnected` event | `RuntimeRuleSynchronizer.ResetPushedState()` clears the "already pushed" cache so the next tick re-pushes every runtime rule instead of wrongly skipping it | `Worker.cs:55`, `RuntimeRuleSynchronizer.cs` |
| One bad discovery tick (transient adapter/process enumeration failure) | try/catch around the tick body | Logged, loop continues at the next 2s tick — one failure never stops the service | `Worker.cs:67-86` |
| One dead/misbehaving IPC client | Per-client `HandleClientAsync` task, isolated by its own try/catch | That client's pipe instance is disposed and removed from `_clients`; every other client and the accept loop are unaffected | `RoutingRuleIpcServer.cs:111-180` |
| UI process crashes or is closed | N/A — the UI holds no state the Service depends on | Nothing to recover: rules live in the Service (`rules.json`), rule enforcement continues unattended. Reopening the UI just reconnects the pipe client | `PipeRoutingRuleClient.cs` (`HeartbeatLoopAsync` reconnects every 2s while disconnected) |
| Service pipe not yet up when UI starts, or drops mid-session | Client's heartbeat/read loop failing | `PipeRoutingRuleClient` reconnects automatically (2s backoff) and re-sends `Hello`; UI shows the last-known rule state until reconnected | `PipeRoutingRuleClient.cs` |
| Adapter added/removed, IP changed (DHCP renewal) | Next 2s discovery tick re-enumerates adapters unconditionally | `RuntimeRuleSynchronizer.SynchronizeRuntimeRules` re-resolves every runtime rule's target address and pushes only the ones that changed | `Worker.cs:71-79`, `RuntimeRuleSynchronizer.cs` |
| Service process crashes | Windows SCM, *if* configured for restart-on-failure at install time | **Not configurable from this environment** — requires `sc.exe failure NetSplit reset=86400 actions=restart/5000/restart/5000/restart/5000` run elevated at install time. See Deployment below. | install-time step |
| Service restarts after a crash/reboot | `FileRoutingRuleService` loads `rules.json` in its constructor; `RuntimeResolver`'s dictionary starts empty and rebuilds itself from the first `UpdateSnapshot` tick; `RuntimeRuleSynchronizer`'s pushed-cache starts empty so it re-pushes everything | State is fully rebuilt from disk + the current process/adapter snapshot within one tick (2s) of startup — no separate "restore" step needed by design | `FileRoutingRuleService.cs`, `Worker.cs` |
| Graceful service stop (`net stop`, reboot, upgrade) | `Worker.StopAsync` override | Clears the kernel rule table before exiting, so no PID's traffic is silently still being redirected after the Service is gone | `Worker.cs:99-111` |
| Ungraceful service stop (kill -9, power loss) | none — by design | The driver keeps enforcing whatever rules were last pushed until it's told otherwise. This is intentional: losing the Service should not silently stop rule enforcement mid-session. The next *clean* Service startup is responsible for reconciling driver state again via the normal tick, not via any special "was it a crash" detection | — |

## Deployment (design scope, not implemented this pass)

Building an actual MSI/WiX installer requires tooling (WiX Toolset, a
code-signing certificate, an elevated build/test environment) not available
here. This section fixes the design so implementation is a mechanical
follow-up, not a new architecture decision.

**Package contents**
- `NetSplit.driver.sys` (test-signed in dev; production requires EV
  code-signing + attestation or WHQL signing for the target OS)
- `NetSplit.Service` publish output (self-contained or framework-dependent
  .NET 8 runtime)
- `NetSplit.App` publish output
- Shared managed assemblies (`NetSplit.Core`, `NetSplit.Network`,
  `NetSplit.Driver.Interop`, `NetSplit.Ipc`) — each component's build already
  copies these to its own output; the installer just needs one copy per app
  directory, not a shared GAC-style location.

**Install-time steps** (in order, all require elevation)
1. `sc create NetSplit type= kernel binPath= <path>\NetSplit.driver.sys
   start= demand` — driver service, demand-start (the Service starts it via
   `DriverHostService`, not the SCM at boot, so a driver update doesn't
   require a reboot to take effect).
2. `sc create NetSplit-Svc binPath= <path>\NetSplit.Service.exe
   start= auto obj= LocalSystem` — Service, auto-start, LocalSystem (needed
   for the driver's Administrators/SYSTEM-only device ACL).
3. `sc failure NetSplit-Svc reset= 86400 actions= restart/5000/restart/5000/restart/5000`
   — the one recovery path that can't be expressed in application code (see
   table above).
4. Grant the pipe's `AuthenticatedUserSid` ACL rule (already in
   `RoutingRuleIpcServer.CreatePipeServer`) — no separate install step, it's
   applied by the Service itself every time it creates a pipe instance.
5. Start `NetSplit-Svc`.
6. Install `NetSplit.App` as a normal per-user or per-machine application;
   no service registration, no elevation, no manifest privilege beyond
   `asInvoker` (already set in `app.manifest`).

**Upgrade**
- Driver: `sc stop NetSplit` → replace `.sys` → next `DriverHostService`
  recovery tick (≤2s after `NetSplit-Svc` next runs `CheckAndRecoverAsync`)
  starts the new version. `IOCTL_NETSPLIT_GET_VERSION`'s
  `ProtocolVersion`/`Capabilities` fields exist specifically so
  `DriverClient`/`DiagnosticsService` can detect a version skew between an
  old driver and a new Service (or vice versa) instead of silently
  misinterpreting IOCTL buffers.
- Service: `sc stop NetSplit-Svc` → replace binaries → `sc start
  NetSplit-Svc`. `rules.json` is untouched by a binary replace, so permanent
  rules survive the upgrade with no migration step as long as the DTO shape
  stays additive (new optional fields only — this is already how
  `FileRoutingRuleService.Load()` is written: it skips entries it can't
  parse instead of crash-looping).
- UI: ordinary app replace; it holds no persistent state of its own.

**Rollback**
- Driver/Service: same mechanism as upgrade, in reverse — `sc stop`, replace
  binary with the previous version, `sc start`. Because protocol version
  negotiation is explicit (`NETSPLIT_PROTOCOL_VERSION`, `IpcProtocol.Version`),
  a rollback that crosses a protocol version boundary fails loudly
  (`IpcErrorCode.ProtocolVersionMismatch` / a `DriverVersionInfo.IsCompatible
  == false` check) rather than silently corrupting rule state.

**Not yet implemented, explicitly out of scope for this pass**: the actual
WiX/MSI project, a code-signing pipeline, and an uninstaller that undoes the
`sc create`/`sc failure` steps. These are packaging work, not architecture —
nothing above requires a design decision to build, only tooling access this
environment doesn't have.
