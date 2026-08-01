using System.Text.Json.Serialization;

namespace Patchouli.Mcp;

/// <summary>
/// v3 unified response envelope shared by the MCP tools and the patchouli-cli executable.
/// Every tool response is strictly { meta, continuation, message?, entries }.
/// `message` is omitted for a clean success; a response without `message` is a clean success.
/// Only find may return a top-level `continuation`; fetch continuation data lives on its
/// per-entry next_range/continuation and put/cite always return null.
/// </summary>
public sealed record McpEnvelope<TMeta, TEntry>(
    [property: JsonPropertyName("meta")] TMeta Meta,
    [property: JsonPropertyName("continuation")]
    string? Continuation,
    [property: JsonPropertyName("message")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    McpMessage? Message,
    [property: JsonPropertyName("entries")]
    IReadOnlyList<TEntry> Entries)
{
    public static McpEnvelope<TMeta, TEntry> Create(
        TMeta meta,
        IReadOnlyList<TEntry> entries,
        string? continuation = null,
        McpMessage? message = null)
    {
        return new McpEnvelope<TMeta, TEntry>(meta, continuation, message, entries);
    }
}

/// <summary>
/// The optional message slot of a response. Present exactly when there are stable warnings
/// and/or a request-level error; never carries free-text success prose.
/// </summary>
public sealed record McpMessage(
    [property: JsonPropertyName("warnings")]
    IReadOnlyList<string> Warnings,
    [property: JsonPropertyName("error")] McpToolError? Error);

/// <summary>
/// Strict, closed error value: { code, name, correlation_id }. code and name always come
/// from the same error-table row. It never carries free-text exception details.
/// </summary>
public sealed record McpToolError(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("correlation_id")]
    string? CorrelationId)
{
    /// <summary>Non-serialized diagnostic detail for CLI/transport display only.</summary>
    [JsonIgnore]
    public string? Detail { get; init; }

    public static McpToolError From(McpErrorCode code, string? detail = null, string? correlationId = null)
    {
        return new McpToolError((int)code, ErrorName(code), correlationId) { Detail = detail };
    }

    public static string ErrorName(McpErrorCode code)
    {
        return code switch
        {
            McpErrorCode.Ok => "OK",
            McpErrorCode.Internal => "INTERNAL",
            McpErrorCode.InvalidArgument => "INVALID_ARGUMENT",
            McpErrorCode.NotFound => "NOT_FOUND",
            McpErrorCode.PermissionDenied => "PERMISSION_DENIED",
            McpErrorCode.Reserved => "RESERVED",
            McpErrorCode.InvalidContent => "INVALID_CONTENT",
            McpErrorCode.ResponseTruncated => "RESPONSE_TRUNCATED",
            McpErrorCode.Unavailable => "UNAVAILABLE",
            McpErrorCode.NotCitable => "NOT_CITABLE",
            McpErrorCode.DeadlineExceeded => "DEADLINE_EXCEEDED",
            McpErrorCode.Cancelled => "CANCELLED",
            _ => "UNKNOWN"
        };
    }
}

/// <summary>
/// Command result carrying either a full response envelope or a request-level error.
/// A partial success keeps its envelope and sets <see cref="Error"/> so the transport can
/// mark the tool response isError while preserving usable entries.
/// </summary>
public sealed record McpCommandResult<TMeta, TEntry>(
    McpEnvelope<TMeta, TEntry>? Envelope,
    McpToolError? Error)
{
    public bool IsSuccess => Error is null;

    public static McpCommandResult<TMeta, TEntry> Ok(McpEnvelope<TMeta, TEntry> envelope)
    {
        return new McpCommandResult<TMeta, TEntry>(envelope, null);
    }

    public static McpCommandResult<TMeta, TEntry> Fail(McpErrorCode code, string detail,
        string? correlationId = null)
    {
        return new McpCommandResult<TMeta, TEntry>(null, McpToolError.From(code, detail, correlationId));
    }

    public static McpCommandResult<TMeta, TEntry> Partial(
        McpEnvelope<TMeta, TEntry> envelope, McpErrorCode code, string detail, string? correlationId = null)
    {
        return new McpCommandResult<TMeta, TEntry>(envelope, McpToolError.From(code, detail, correlationId));
    }
}
