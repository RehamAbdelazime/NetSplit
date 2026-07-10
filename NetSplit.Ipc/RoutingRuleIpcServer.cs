using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using NetSplit.Core;

namespace NetSplit.Ipc;

/// <summary>
/// Hosts the UI-facing named pipe. Lives in the Service; wraps an
/// IRoutingRuleService (whatever persistence-backed implementation the
/// Service constructed) and exposes it to any number of connected UI
/// clients. Same-machine-only by construction (named pipes with no
/// "\\server\pipe\..." remote form used here) plus an explicit ACL
/// restricting the pipe to Administrators and the interactive user -
/// authentication is "you hold a handle the OS only handed out to an
/// allowed SID," the standard local-IPC pattern.
/// </summary>
public sealed class RoutingRuleIpcServer : IDisposable
{
    private readonly IRoutingRuleService _rules;
    private readonly Func<ServiceStatusDto> _statusProvider;
    private readonly Func<int, DiagnosticsSnapshotDto>? _diagnosticsProvider;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<ClientConnection> _clients = new();
    private readonly object _clientsGate = new();
    private Task? _acceptLoopTask;

    /// <summary>Currently connected UI clients - the "Connected UI Clients" diagnostic.</summary>
    public int ConnectedClientCount
    {
        get
        {
            lock (_clientsGate)
            {
                return _clients.Count;
            }
        }
    }

    private sealed class ClientConnection
    {
        public required NamedPipeServerStream Stream;
        public required SemaphoreSlim WriteLock;
    }

    public RoutingRuleIpcServer(
        IRoutingRuleService rules,
        Func<ServiceStatusDto>? statusProvider = null,
        Func<int, DiagnosticsSnapshotDto>? diagnosticsProvider = null)
    {
        _rules = rules;
        _statusProvider = statusProvider ?? (() => new ServiceStatusDto(false, null, false, _rules.GetRules().Count, 0, 0, 0, 0, 0, DateTimeOffset.UtcNow, Guid.Empty));
        _diagnosticsProvider = diagnosticsProvider;
        _rules.RulesChanged += OnRulesChanged;
    }

    public void Start()
    {
        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = CreatePipeServer();
            }
            catch (IOException)
            {
                // All instance slots busy for a moment - back off briefly and retry.
                await Task.Delay(TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                server.Dispose();
                break;
            }
            catch
            {
                server.Dispose();
                continue;
            }

            var connection = new ClientConnection { Stream = server, WriteLock = new SemaphoreSlim(1, 1) };
            lock (_clientsGate)
            {
                _clients.Add(connection);
            }

            _ = Task.Run(() => HandleClientAsync(connection, ct), ct);
        }
    }

    private static NamedPipeServerStream CreatePipeServer()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        // AuthenticatedUserSid, not InteractiveSid: this covers any locally
        // authenticated account regardless of logon type (interactive,
        // service, batch) - the Service itself may run as LocalSystem or a
        // service account in production, and different client processes
        // may not always hold an "Interactive" logon type. Still local-
        // machine-only by construction (this pipe has no remote form).
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            IpcProtocol.PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity: security);
    }

    private async Task HandleClientAsync(ClientConnection connection, CancellationToken ct)
    {
        try
        {
            IpcEnvelope? hello = await IpcFramedStream.ReadMessageAsync(connection.Stream, ct).ConfigureAwait(false);
            if (hello == null || hello.Kind != IpcMessageKind.Hello)
            {
                return;
            }

            if (hello.ProtocolVersion != IpcProtocol.Version)
            {
                await SendAsync(connection, new IpcEnvelope
                {
                    Kind = IpcMessageKind.Error,
                    MessageId = Guid.NewGuid(),
                    CorrelationId = hello.MessageId,
                    ErrorCode = IpcErrorCode.ProtocolVersionMismatch,
                    ErrorMessage = $"Server protocol version {IpcProtocol.Version}, client sent {hello.ProtocolVersion}.",
                }, ct).ConfigureAwait(false);
                return;
            }

            await SendAsync(connection, new IpcEnvelope
            {
                Kind = IpcMessageKind.HelloAck,
                MessageId = Guid.NewGuid(),
                CorrelationId = hello.MessageId,
                ProtocolVersion = IpcProtocol.Version,
            }, ct).ConfigureAwait(false);

            while (!ct.IsCancellationRequested)
            {
                IpcEnvelope? request = await IpcFramedStream.ReadMessageAsync(connection.Stream, ct).ConfigureAwait(false);
                if (request == null)
                {
                    break; // clean disconnect
                }

                if (request.Kind == IpcMessageKind.Heartbeat)
                {
                    await SendAsync(connection, new IpcEnvelope { Kind = IpcMessageKind.Heartbeat, MessageId = Guid.NewGuid() }, ct).ConfigureAwait(false);
                    continue;
                }

                if (request.Kind != IpcMessageKind.Request)
                {
                    continue; // Cancel and anything else: no-op in this version, reserved for future long-running methods
                }

                IpcEnvelope response = Dispatch(request);
                await SendAsync(connection, response, ct).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Connection dropped/faulted mid-stream - not a server error,
            // just stop servicing this one client. Other clients unaffected.
        }
        finally
        {
            lock (_clientsGate)
            {
                _clients.Remove(connection);
            }

            connection.Stream.Dispose();
            connection.WriteLock.Dispose();
        }
    }

    private IpcEnvelope Dispatch(IpcEnvelope request)
    {
        try
        {
            string? responseJson = request.Method switch
            {
                IpcMethods.GetRules =>
                    IpcFramedStream.ToJson(_rules.GetRules()),

                IpcMethods.GetRule =>
                    IpcFramedStream.ToJson(_rules.GetRule(Payload<GetRuleRequest>(request).Id)),

                IpcMethods.AddRule =>
                    IpcFramedStream.ToJson(AddRuleFromPayload(request)),

                IpcMethods.UpdateRule =>
                    IpcFramedStream.ToJson(UpdateRuleFromPayload(request)),

                IpcMethods.DeleteRule =>
                    IpcFramedStream.ToJson(new BoolResponse(_rules.DeleteRule(Payload<RuleIdRequest>(request).Id))),

                IpcMethods.EnableRule =>
                    IpcFramedStream.ToJson(new BoolResponse(_rules.EnableRule(Payload<RuleIdRequest>(request).Id))),

                IpcMethods.DisableRule =>
                    IpcFramedStream.ToJson(new BoolResponse(_rules.DisableRule(Payload<RuleIdRequest>(request).Id))),

                IpcMethods.GetStatus =>
                    IpcFramedStream.ToJson(_statusProvider()),

                IpcMethods.GetDiagnostics =>
                    IpcFramedStream.ToJson(GetDiagnosticsSnapshot()),

                _ => throw new InvalidOperationException($"Unknown IPC method '{request.Method}'."),
            };

            return new IpcEnvelope
            {
                Kind = IpcMessageKind.Response,
                MessageId = Guid.NewGuid(),
                CorrelationId = request.MessageId,
                PayloadJson = responseJson,
            };
        }
        catch (DuplicateRoutingRuleException ex)
        {
            return ErrorEnvelope(request.MessageId, IpcErrorCode.DuplicateRule, ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            return ErrorEnvelope(request.MessageId, IpcErrorCode.NotFound, ex.Message);
        }
        catch (Exception ex)
        {
            return ErrorEnvelope(request.MessageId, IpcErrorCode.InternalError, ex.Message);
        }
    }

    private DiagnosticsSnapshotDto GetDiagnosticsSnapshot()
    {
        if (_diagnosticsProvider == null)
        {
            throw new InvalidOperationException("Diagnostics are not available - no diagnostics provider was configured for this server.");
        }

        // ConnectedClientCount is supplied here, not baked into the provider
        // delegate, specifically to avoid a circular dependency: the count
        // is owned by this class, while DiagnosticsService (the provider)
        // is otherwise independent of the IPC layer entirely.
        return _diagnosticsProvider(ConnectedClientCount);
    }

    private RoutingRule AddRuleFromPayload(IpcEnvelope request)
    {
        AddRuleRequest r = Payload<AddRuleRequest>(request);
        return _rules.AddRule(r.ProcessName, r.ProcessId, r.TargetType, r.TargetAdapter, r.Enabled);
    }

    private RoutingRule UpdateRuleFromPayload(IpcEnvelope request)
    {
        UpdateRuleRequest r = Payload<UpdateRuleRequest>(request);
        return _rules.UpdateRule(r.Id, r.ProcessName, r.ProcessId, r.TargetType, r.TargetAdapter);
    }

    private static T Payload<T>(IpcEnvelope request) =>
        IpcFramedStream.FromJson<T>(request.PayloadJson) ?? throw new InvalidOperationException($"Missing payload for '{request.Method}'.");

    private static IpcEnvelope ErrorEnvelope(Guid correlationId, IpcErrorCode code, string message) => new()
    {
        Kind = IpcMessageKind.Error,
        MessageId = Guid.NewGuid(),
        CorrelationId = correlationId,
        ErrorCode = code,
        ErrorMessage = message,
    };

    private void OnRulesChanged(object? sender, EventArgs e)
    {
        var notification = new IpcEnvelope { Kind = IpcMessageKind.Notification, MessageId = Guid.NewGuid(), Method = IpcMethods.RulesChanged };

        List<ClientConnection> clients;
        lock (_clientsGate)
        {
            clients = _clients.ToList();
        }

        // Fire-and-forget per client - one slow/dead client must not delay
        // the notification reaching everyone else.
        foreach (ClientConnection client in clients)
        {
            _ = SendAsync(client, notification, CancellationToken.None);
        }
    }

    private static async Task SendAsync(ClientConnection connection, IpcEnvelope envelope, CancellationToken ct)
    {
        try
        {
            await connection.WriteLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await IpcFramedStream.WriteMessageAsync(connection.Stream, envelope, ct).ConfigureAwait(false);
            }
            finally
            {
                connection.WriteLock.Release();
            }
        }
        catch (Exception)
        {
            // A dead client's write failing is handled by its own read loop
            // noticing the disconnect and cleaning up - nothing to do here.
        }
    }

    public void Dispose()
    {
        _rules.RulesChanged -= OnRulesChanged;
        _cts.Cancel();

        List<ClientConnection> clients;
        lock (_clientsGate)
        {
            clients = _clients.ToList();
            _clients.Clear();
        }

        foreach (ClientConnection client in clients)
        {
            client.Stream.Dispose();
            client.WriteLock.Dispose();
        }

        _cts.Dispose();
    }
}
