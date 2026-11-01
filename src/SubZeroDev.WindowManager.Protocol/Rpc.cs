using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SubZeroDev.WindowManager.Protocol;

/// <summary>One JSON-RPC 2.0 message: a request (id and method), a notification (method only) or a response (id and result or error).</summary>
public sealed class RpcMessage
{
    public string Jsonrpc { get; init; } = "2.0";

    public long? Id { get; init; }

    public string? Method { get; init; }

    public JsonElement? Params { get; init; }

    public JsonElement? Result { get; init; }

    public RpcError? Error { get; init; }

    [JsonIgnore]
    public bool IsRequest => Method is not null && Id is not null;

    [JsonIgnore]
    public bool IsNotification => Method is not null && Id is null;

    [JsonIgnore]
    public bool IsResponse => Method is null && Id is not null;

    public static RpcMessage Request(long id, string method, object? parameters) =>
        new() { Id = id, Method = method, Params = ProtocolJson.ToElement(parameters) };

    public static RpcMessage Notification(string method, object? parameters) =>
        new() { Method = method, Params = ProtocolJson.ToElement(parameters) };

    public static RpcMessage Success(long id, object? result) =>
        new() { Id = id, Result = ProtocolJson.ToElement(result ?? new JsonObject()) };

    public static RpcMessage Failure(long? id, int code, string message, JsonNode? data = null) =>
        new() { Id = id, Error = new RpcError(code, message, data) };

    public T? ParamsAs<T>() => Params is { } element ? element.Deserialize<T>(ProtocolJson.Options) : default;

    public T? ResultAs<T>() => Result is { } element ? element.Deserialize<T>(ProtocolJson.Options) : default;
}

public sealed record RpcError(int Code, string Message, JsonNode? Data = null);

/// <summary>Thrown for a JSON-RPC error response, or raised locally to produce one.</summary>
public sealed class RpcException(int code, string message, JsonNode? data = null) : Exception(message)
{
    public int Code { get; } = code;

    public JsonNode? ErrorData { get; } = data;
}

public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = Create();

    public static byte[] Serialize(RpcMessage message) => JsonSerializer.SerializeToUtf8Bytes(message, Options);

    public static RpcMessage Deserialize(ReadOnlySpan<byte> utf8) =>
        JsonSerializer.Deserialize<RpcMessage>(utf8, Options) ?? throw new JsonException("Empty message.");

    public static JsonElement? ToElement(object? value) =>
        value is null ? null : JsonSerializer.SerializeToElement(value, value.GetType(), Options);

    public static JsonNode? ToNode(object? value) =>
        value is null ? null : JsonSerializer.SerializeToNode(value, value.GetType(), Options);

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

/// <summary>Message framing: a 4-byte little-endian length, then that many bytes of UTF-8 JSON.</summary>
public static class Framing
{
    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (payload.Length > ProtocolInfo.MaxMessageBytes)
        {
            throw new InvalidDataException($"Message of {payload.Length} bytes exceeds the {ProtocolInfo.MaxMessageBytes}-byte limit.");
        }

        var buffer = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, payload.Length);
        payload.CopyTo(buffer.AsMemory(4));
        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one frame; null at a clean end of stream.</summary>
    public static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = new byte[4];
        if (!await FillAsync(stream, header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > ProtocolInfo.MaxMessageBytes)
        {
            throw new InvalidDataException($"Frame length {length} is out of range.");
        }

        var payload = new byte[length];
        if (!await FillAsync(stream, payload, cancellationToken).ConfigureAwait(false))
        {
            throw new EndOfStreamException("Stream ended inside a frame.");
        }

        return payload;
    }

    private static async Task<bool> FillAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                if (read == 0)
                {
                    return false;
                }

                throw new EndOfStreamException("Stream ended inside a frame.");
            }

            read += count;
        }

        return true;
    }
}
