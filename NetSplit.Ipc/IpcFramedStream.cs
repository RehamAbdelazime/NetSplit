using System.Text.Json;

namespace NetSplit.Ipc;

/// <summary>
/// Reads/writes one IpcEnvelope at a time over a Stream (a pipe, in
/// practice) using simple length-prefixed framing: a 4-byte little-endian
/// message length, followed by that many UTF-8 JSON bytes. Length-prefixed
/// rather than newline-delimited specifically so payloads can contain
/// arbitrary content (including newlines) and so "large message support"
/// and a future chunked/streaming payload are just "a bigger length
/// prefix" - nothing about the framing itself changes.
///
/// No binary serialization framework - deliberately plain JSON, so every
/// message is human-readable in a packet capture or a debug log, at the
/// cost of a few more bytes on the wire than a binary format would use.
/// For a local named pipe carrying rule edits (not a hot data path), that
/// tradeoff favors debuggability.
/// </summary>
public static class IpcFramedStream
{
    // Refuses to allocate an unbounded buffer for a corrupt/malicious
    // length prefix - 16 MiB is far larger than any message this protocol
    // sends today (rule lists are at most a few KB) while still leaving
    // headroom for a future bulk/streaming payload.
    private const int MaxMessageBytes = 16 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task WriteMessageAsync(Stream stream, IpcEnvelope envelope, CancellationToken cancellationToken = default)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        byte[] lengthPrefix = BitConverter.GetBytes(json.Length);

        await stream.WriteAsync(lengthPrefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(json, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns null if the stream was closed cleanly before a new message started.</summary>
    public static async Task<IpcEnvelope?> ReadMessageAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        byte[] lengthPrefix = new byte[4];
        if (!await ReadExactAsync(stream, lengthPrefix, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        int length = BitConverter.ToInt32(lengthPrefix, 0);
        if (length < 0 || length > MaxMessageBytes)
        {
            throw new InvalidDataException($"IPC message length {length} is out of the allowed range (0..{MaxMessageBytes}).");
        }

        byte[] payload = new byte[length];
        if (!await ReadExactAsync(stream, payload, cancellationToken).ConfigureAwait(false))
        {
            throw new EndOfStreamException("Connection closed mid-message.");
        }

        return JsonSerializer.Deserialize<IpcEnvelope>(payload, JsonOptions);
    }

    /// <summary>Fills the buffer completely, or returns false if the stream ended before any bytes were read (a clean close between messages).</summary>
    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return offset == 0 ? false : throw new EndOfStreamException("Connection closed mid-message.");
            }
            offset += read;
        }
        return true;
    }

    public static string ToJson<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    public static T? FromJson<T>(string? json) => json == null ? default : JsonSerializer.Deserialize<T>(json, JsonOptions);
}
