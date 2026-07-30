using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Patchouli.Infrastructure.Shell;

public static class ShellRpcProtocol
{
    public const string Version = "1";
}

public sealed record ShellRpcErrorDto(string Code, string Message);

public sealed class ShellRpcEnvelope
{
    [JsonPropertyName("protocol_version")] public string ProtocolVersion { get; set; } = ShellRpcProtocol.Version;

    [JsonPropertyName("message_type")] public string MessageType { get; set; } = "request";

    [JsonPropertyName("request_id")] public ulong? RequestId { get; set; }

    [JsonPropertyName("execution_id")] public ulong? ExecutionId { get; set; }

    [JsonPropertyName("method")] public string? Method { get; set; }

    [JsonPropertyName("payload")] public JsonElement? Payload { get; set; }

    [JsonPropertyName("error")] public ShellRpcErrorDto? Error { get; set; }
}

public static class ShellRpcFraming
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    public static async Task WriteFrameAsync(Stream stream, ShellRpcEnvelope envelope, int maxFrameBytes,
        CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        if (payload.Length > maxFrameBytes)
        {
            throw new InvalidOperationException("RPC frame exceeds maximum size.");
        }

        byte[] length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)payload.Length);
        await stream.WriteAsync(length, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<ShellRpcEnvelope?> ReadFrameAsync(Stream stream, int maxFrameBytes,
        CancellationToken cancellationToken)
    {
        byte[] lengthBytes = new byte[4];
        int read = await ReadExactAsync(stream, lengthBytes, cancellationToken);
        if (read == 0)
        {
            return null;
        }

        if (read < 4)
        {
            throw new InvalidOperationException("Unexpected EOF while reading frame length.");
        }

        uint length = BinaryPrimitives.ReadUInt32BigEndian(lengthBytes);
        if (length == 0 || length > maxFrameBytes)
        {
            throw new InvalidOperationException("Invalid RPC frame length.");
        }

        byte[] payload = new byte[length];
        int payloadRead = await ReadExactAsync(stream, payload, cancellationToken);
        if (payloadRead < payload.Length)
        {
            throw new InvalidOperationException("Unexpected EOF while reading frame payload.");
        }

        return JsonSerializer.Deserialize<ShellRpcEnvelope>(payload, JsonOptions)
               ?? throw new InvalidOperationException("Failed to deserialize RPC envelope.");
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
            if (read == 0)
            {
                return offset;
            }

            offset += read;
        }

        return offset;
    }

    public static string ToUtf8(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? ""
            : element.GetRawText();
    }
}
