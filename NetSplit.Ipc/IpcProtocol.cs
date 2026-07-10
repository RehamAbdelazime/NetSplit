namespace NetSplit.Ipc;

/// <summary>
/// The UI &lt;-&gt; Service named-pipe protocol version. A client's very first
/// message must be a Hello carrying this number; the server rejects
/// anything whose major version doesn't match with
/// IpcErrorCode.ProtocolVersionMismatch rather than guessing at
/// compatibility. Bump this only for a wire-breaking change to
/// IpcEnvelope itself; a new Method value or a new field on an existing
/// payload DTO does not require a bump - old clients that don't send the
/// new field still parse fine (System.Text.Json ignores unknown/missing
/// members by default), which is what "support future expansion without
/// breaking old clients" means in practice here.
/// </summary>
public static class IpcProtocol
{
    public const int Version = 1;
    public const string PipeName = "NetSplit";
}

public enum IpcMessageKind
{
    Hello,        // first message on a new connection: client announces its protocol version
    HelloAck,     // server's reply: accepted, or ProtocolVersionMismatch
    Request,      // client -> server: call a method
    Response,     // server -> client: successful result for a Request
    Error,        // server -> client: failed result for a Request (or a Hello rejection)
    Notification, // server -> client: unsolicited push (e.g. RulesChanged)
    Heartbeat,    // either direction: liveness ping; the receiver need not reply
    Cancel,       // client -> server: abandon a still-pending Request by MessageId (reserved for future long-running/streaming methods; nothing today runs long enough to need it, but the wire shape exists so adding one later isn't a protocol change)
}

public enum IpcErrorCode
{
    Unknown = 0,
    ValidationFailed,
    NotFound,
    DuplicateRule,
    InternalError,
    ProtocolVersionMismatch,
    Timeout,
    Cancelled,
}

/// <summary>
/// The only shape that ever goes on the wire. Every message - request,
/// response, notification, heartbeat - is one of these, length-prefixed and
/// JSON-encoded (see IpcFramedStream). Method-specific data lives inside
/// PayloadJson rather than as typed envelope fields, so adding a new method
/// or growing an existing payload's DTO never changes this envelope's own
/// shape - the actual "won't break old clients" guarantee.
/// </summary>
public sealed class IpcEnvelope
{
    public required IpcMessageKind Kind { get; init; }
    public required Guid MessageId { get; init; }

    /// <summary>Set on Response/Error/HelloAck: the MessageId of the Request/Hello this answers.</summary>
    public Guid? CorrelationId { get; init; }

    /// <summary>Set on Hello only.</summary>
    public int? ProtocolVersion { get; init; }

    /// <summary>Set on Request/Notification: which operation (e.g. "GetRules", "AddRule", "RulesChanged").</summary>
    public string? Method { get; init; }

    /// <summary>Method-specific payload, JSON-encoded. Large payloads are supported as-is - see IpcFramedStream's length prefix.</summary>
    public string? PayloadJson { get; init; }

    public IpcErrorCode? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}
