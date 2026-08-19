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
/// The optional terminal-style message slot of a response. Present exactly when there are
/// warnings and/or a request-level error; never carries success prose.
/// </summary>
public sealed record McpMessage(
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("warnings")]
    IReadOnlyList<string> Warnings);

/// <summary>
/// Internal error classification used to produce compact, sanitized terminal diagnostics at
/// the protocol boundary.
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

    public string ToTerminalLine()
    {
        string reference = string.IsNullOrWhiteSpace(CorrelationId) ? string.Empty : $"; ref {CorrelationId}";
        string detail = Code == (int)McpErrorCode.Internal || string.IsNullOrWhiteSpace(Detail)
            ? DefaultDetail((McpErrorCode)Code)
            : McpOutputSanitizer.Sanitize(Detail);
        return $"{Name} [code {Code}{reference}]: {detail}";
    }

    public static bool TryGetCode(string? terminalLine, out McpErrorCode code)
    {
        const string marker = "[code ";
        int start = terminalLine?.IndexOf(marker, StringComparison.Ordinal) ?? -1;
        if (start >= 0)
        {
            start += marker.Length;
            int end = start;
            while (end < terminalLine!.Length && char.IsDigit(terminalLine[end]))
            {
                end++;
            }

            if (end > start && int.TryParse(terminalLine[start..end], out int numeric) &&
                Enum.IsDefined((McpErrorCode)numeric) && numeric != (int)McpErrorCode.Ok)
            {
                code = (McpErrorCode)numeric;
                return true;
            }
        }

        code = McpErrorCode.Internal;
        return false;
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
            McpErrorCode.ItemInTrash => "ITEM_IN_TRASH",
            McpErrorCode.ItemMerged => "ITEM_MERGED",
            _ => "UNKNOWN"
        };
    }

    private static string DefaultDetail(McpErrorCode code)
    {
        return code switch
        {
            McpErrorCode.Internal => "The host could not complete the request.",
            McpErrorCode.InvalidArgument => "The request is invalid.",
            McpErrorCode.NotFound => "The requested resource was not found.",
            McpErrorCode.PermissionDenied => "The requested operation is not permitted.",
            McpErrorCode.InvalidContent => "The supplied content is invalid.",
            McpErrorCode.ResponseTruncated => "The response was truncated.",
            McpErrorCode.Unavailable => "The requested service is unavailable.",
            McpErrorCode.NotCitable => "The requested resource cannot be cited.",
            McpErrorCode.DeadlineExceeded => "The request exceeded its deadline.",
            McpErrorCode.Cancelled => "The request was cancelled.",
            McpErrorCode.ItemInTrash => "The requested item is in trash.",
            McpErrorCode.ItemMerged => "The requested item has been merged into another item.",
            _ => "The request failed."
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
