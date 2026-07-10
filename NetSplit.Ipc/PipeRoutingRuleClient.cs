using System.Collections.Concurrent;
using System.IO.Pipes;
using NetSplit.Core;

namespace NetSplit.Ipc;

/// <summary>
/// UI-side IRoutingRuleService implementation that proxies every call to
/// the Service over the named pipe. Because IRoutingRuleService's methods
/// are synchronous, this class bridges to async pipe I/O with a bounded
/// wait (RequestTimeout) rather than exposing a separate async surface -
/// this is what lets MainViewModel/ProcessViewModel/every View in
/// NetSplit.App work completely unchanged: they already only know
/// IRoutingRuleService, never a concrete type.
///
/// Reconnects automatically: if the pipe is down when a call is made, one
/// connect attempt is made inline before failing that call; a background
/// heartbeat loop also periodically verifies the connection and reconnects
/// proactively so the *next* user-initiated call doesn't have to pay a
/// connect delay after the Service restarts.
/// </summary>
public sealed class PipeRoutingRuleClient : IRoutingRuleService, IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);

    private readonly object _connectionGate = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<IpcEnvelope>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private NamedPipeClientStream? _pipe;
    private Task? _readLoopTask;
    private volatile bool _connected;

    public event EventHandler? RulesChanged;

    /// <summary>Raised when the pipe connects/disconnects - a host (e.g. the UI shell) can use this to show a "Service unavailable" indicator; purely informational, every IRoutingRuleService call still works transparently (it just reconnects or times out).</summary>
    public event EventHandler<bool>? ConnectionStateChanged;

    public PipeRoutingRuleClient()
    {
        _ = Task.Run(HeartbeatLoopAsync);
    }

    private async Task HeartbeatLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (!_connected)
                {
                    await ConnectAsync(_cts.Token).ConfigureAwait(false);
                }
                else
                {
                    await SendAsync(new IpcEnvelope { Kind = IpcMessageKind.Heartbeat, MessageId = Guid.NewGuid() }, _cts.Token).ConfigureAwait(false);
                }
            }
            catch
            {
                // Connect/heartbeat failure just means we try again next
                // tick - handled uniformly, no special-casing needed here.
            }

            try
            {
                await Task.Delay(_connected ? HeartbeatInterval : ReconnectDelay, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        NamedPipeClientStream pipe = new(".", IpcProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(RequestTimeout);
        await pipe.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);

        Guid helloId = Guid.NewGuid();
        await IpcFramedStream.WriteMessageAsync(pipe, new IpcEnvelope
        {
            Kind = IpcMessageKind.Hello,
            MessageId = helloId,
            ProtocolVersion = IpcProtocol.Version,
        }, ct).ConfigureAwait(false);

        IpcEnvelope? ack = await IpcFramedStream.ReadMessageAsync(pipe, ct).ConfigureAwait(false);
        if (ack == null || ack.Kind != IpcMessageKind.HelloAck)
        {
            pipe.Dispose();
            throw new IOException(ack?.ErrorMessage ?? "Service rejected the connection.");
        }

        lock (_connectionGate)
        {
            _pipe = pipe;
        }

        _connected = true;
        ConnectionStateChanged?.Invoke(this, true);
        _readLoopTask = Task.Run(() => ReadLoopAsync(pipe, _cts.Token));
    }

    private async Task ReadLoopAsync(NamedPipeClientStream pipe, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                IpcEnvelope? message = await IpcFramedStream.ReadMessageAsync(pipe, ct).ConfigureAwait(false);
                if (message == null)
                {
                    break; // server closed the connection
                }

                switch (message.Kind)
                {
                    case IpcMessageKind.Response or IpcMessageKind.Error:
                        if (message.CorrelationId is Guid correlationId &&
                            _pending.TryRemove(correlationId, out TaskCompletionSource<IpcEnvelope>? tcs))
                        {
                            tcs.TrySetResult(message);
                        }
                        break;

                    case IpcMessageKind.Notification when message.Method == IpcMethods.RulesChanged:
                        RulesChanged?.Invoke(this, EventArgs.Empty);
                        break;
                }
            }
        }
        catch (Exception)
        {
            // Falls through to the disconnect handling below regardless of cause.
        }
        finally
        {
            _connected = false;
            lock (_connectionGate)
            {
                if (ReferenceEquals(_pipe, pipe))
                {
                    _pipe = null;
                }
            }
            pipe.Dispose();
            ConnectionStateChanged?.Invoke(this, false);

            // Any request still waiting on this connection will never get
            // an answer - fail them now instead of hanging until their own timeout.
            foreach (KeyValuePair<Guid, TaskCompletionSource<IpcEnvelope>> kv in _pending)
            {
                kv.Value.TrySetException(new IOException("Connection to NetSplit service was lost."));
            }
            _pending.Clear();
        }
    }

    private async Task SendAsync(IpcEnvelope envelope, CancellationToken ct)
    {
        NamedPipeClientStream? pipe;
        lock (_connectionGate)
        {
            pipe = _pipe;
        }

        if (pipe == null)
        {
            throw new IOException("Not connected to NetSplit service.");
        }

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await IpcFramedStream.WriteMessageAsync(pipe, envelope, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<IpcEnvelope> RequestAsync(string method, object? payload)
    {
        if (!_connected)
        {
            await ConnectAsync(_cts.Token).ConfigureAwait(false);
        }

        var envelope = new IpcEnvelope
        {
            Kind = IpcMessageKind.Request,
            MessageId = Guid.NewGuid(),
            Method = method,
            PayloadJson = payload == null ? null : IpcFramedStream.ToJson(payload),
        };

        var tcs = new TaskCompletionSource<IpcEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[envelope.MessageId] = tcs;

        using CancellationTokenSource timeoutCts = new(RequestTimeout);
        await using CancellationTokenRegistration registration = timeoutCts.Token.Register(() =>
        {
            if (_pending.TryRemove(envelope.MessageId, out TaskCompletionSource<IpcEnvelope>? pendingTcs))
            {
                pendingTcs.TrySetException(new TimeoutException($"IPC call '{method}' timed out after {RequestTimeout}."));
            }
        });

        await SendAsync(envelope, timeoutCts.Token).ConfigureAwait(false);

        IpcEnvelope response = await tcs.Task.ConfigureAwait(false);

        if (response.Kind == IpcMessageKind.Error)
        {
            throw response.ErrorCode switch
            {
                IpcErrorCode.DuplicateRule => new DuplicateRoutingRuleException(response.ErrorMessage ?? "Duplicate rule."),
                IpcErrorCode.NotFound => new KeyNotFoundException(response.ErrorMessage ?? "Not found."),
                _ => new InvalidOperationException(response.ErrorMessage ?? $"IPC call '{method}' failed."),
            };
        }

        return response;
    }

    // Every IRoutingRuleService method blocks on the async round trip -
    // matches the interface's existing synchronous contract exactly, so
    // nothing above this class (MainViewModel, ProcessViewModel, any View)
    // needs to change to consume it.

    public IReadOnlyList<RoutingRule> GetRules() =>
        IpcFramedStream.FromJson<List<RoutingRule>>(RequestAsync(IpcMethods.GetRules, null).GetAwaiter().GetResult().PayloadJson) ?? new List<RoutingRule>();

    public RoutingRule? GetRule(Guid id) =>
        IpcFramedStream.FromJson<RoutingRule?>(RequestAsync(IpcMethods.GetRule, new GetRuleRequest(id)).GetAwaiter().GetResult().PayloadJson);

    public RoutingRule AddRule(string processName, int? processId, RuleTargetType targetType, AdapterPreference targetAdapter, bool enabled = true) =>
        IpcFramedStream.FromJson<RoutingRule>(RequestAsync(IpcMethods.AddRule, new AddRuleRequest(processName, processId, targetType, targetAdapter, enabled)).GetAwaiter().GetResult().PayloadJson)!;

    public RoutingRule UpdateRule(Guid id, string processName, int? processId, RuleTargetType targetType, AdapterPreference targetAdapter) =>
        IpcFramedStream.FromJson<RoutingRule>(RequestAsync(IpcMethods.UpdateRule, new UpdateRuleRequest(id, processName, processId, targetType, targetAdapter)).GetAwaiter().GetResult().PayloadJson)!;

    public bool DeleteRule(Guid id) =>
        IpcFramedStream.FromJson<BoolResponse>(RequestAsync(IpcMethods.DeleteRule, new RuleIdRequest(id)).GetAwaiter().GetResult().PayloadJson)!.Value;

    public bool EnableRule(Guid id) =>
        IpcFramedStream.FromJson<BoolResponse>(RequestAsync(IpcMethods.EnableRule, new RuleIdRequest(id)).GetAwaiter().GetResult().PayloadJson)!.Value;

    public bool DisableRule(Guid id) =>
        IpcFramedStream.FromJson<BoolResponse>(RequestAsync(IpcMethods.DisableRule, new RuleIdRequest(id)).GetAwaiter().GetResult().PayloadJson)!.Value;

    public ServiceStatusDto GetStatus() =>
        IpcFramedStream.FromJson<ServiceStatusDto>(RequestAsync(IpcMethods.GetStatus, null).GetAwaiter().GetResult().PayloadJson)!;

    /// <summary>Not part of IRoutingRuleService - only the developer Diagnostics window calls this.</summary>
    public DiagnosticsSnapshotDto GetDiagnostics() =>
        IpcFramedStream.FromJson<DiagnosticsSnapshotDto>(RequestAsync(IpcMethods.GetDiagnostics, null).GetAwaiter().GetResult().PayloadJson)!;

    public void Dispose()
    {
        _cts.Cancel();
        lock (_connectionGate)
        {
            _pipe?.Dispose();
            _pipe = null;
        }
        _cts.Dispose();
        _writeLock.Dispose();
    }
}
