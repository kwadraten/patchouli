using System.Text.Json.Serialization;

namespace Patchouli.Mcp;

public sealed record McpFindRequest(
    string? Query,
    string? In,
    IReadOnlyList<McpWhereClause>? Where,
    bool Literal,
    bool Regex,
    int Limit = 20,
    string? Cursor = null);

public sealed record McpWhereClause(string Key, string Value);

public sealed record McpFindMatch(string? Evidence, string Preview, int Ordinal);

public sealed record McpFindResultRow(
    string Uri,
    string Kind,
    string Label,
    string? Revision,
    string? Preview,
    bool Writable,
    bool Citable,
    IReadOnlyList<McpFindMatch>? Matches,
    string? ItemUri = null,
    string? ParentUri = null,
    string? CitationTarget = null);

public sealed record McpFindResponse(
    IReadOnlyList<McpFindResultRow> Results,
    string? Continuation,
    IReadOnlyList<string> Warnings);

public sealed record McpFetchRequest(string Uri, string? Range, string? Revision, int? LimitBytes);

public sealed record McpFetchResponse(
    string Uri,
    string Kind,
    string? Revision,
    bool Writable,
    bool Citable,
    object Content,
    bool Complete = true,
    bool Truncated = false,
    int? ReturnedBytes = null,
    int? LimitBytes = null,
    string? NextRange = null,
    string? ItemUri = null,
    string? ParentUri = null,
    string? CitationTarget = null);

public sealed record McpCiteRequest(
    IReadOnlyList<string> Refs,
    string? Style,
    string? Locale,
    bool BibliographyOnly,
    bool Html);

public sealed record McpCiteResponse(
    string? Style,
    string? Locale,
    string? Bibliography,
    string? Html,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<McpCiteReferenceResult>? References = null,
    string? EffectiveStyle = null);

public sealed record McpCiteReferenceResult(
    string Ref,
    string Status,
    string? ItemUri = null,
    string? CitationTarget = null,
    McpToolError? Error = null);

public sealed record McpFetchTextContent(string Text);

public sealed record McpFetchOutlineContent(
    string? Title,
    string? Revision,
    IReadOnlyList<McpDocumentPageRef> Pages,
    string? ItemUri = null);

public sealed record McpFetchPageContent(
    string Text,
    string? PageLabel,
    int PageIndex,
    string Uri,
    string? ItemUri = null,
    string? ParentUri = null);

public sealed record McpFetchPagesContent(IReadOnlyList<McpFetchPageContent> Pages);

public sealed record McpFetchEvidenceContent(
    string Status,
    string? SourceTitle,
    string? PageLabel,
    int PageIndex,
    string? DocumentUri,
    string? PageUri,
    string? PinnedText,
    string? ItemUri = null);

public sealed record McpToolError(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string Message)
{
    [JsonPropertyName("name")]
    public string Name => Code switch
    {
        (int)McpErrorCode.InvalidArgument => "INVALID_ARGUMENT",
        (int)McpErrorCode.NotFound => "NOT_FOUND",
        (int)McpErrorCode.PermissionDenied => "PERMISSION_DENIED",
        (int)McpErrorCode.RevisionConflict => "REVISION_CONFLICT",
        (int)McpErrorCode.InvalidContent => "INVALID_CONTENT",
        (int)McpErrorCode.ResponseTruncated => "RESPONSE_TRUNCATED",
        (int)McpErrorCode.Unavailable => "UNAVAILABLE",
        (int)McpErrorCode.NotCitable => "NOT_CITABLE",
        _ => "UNKNOWN"
    };
}

public sealed record McpCommandResult<T>(McpEnvelope<T>? Envelope, McpToolError? Error)
{
    public bool IsSuccess => Error is null;

    public static McpCommandResult<T> Ok(McpEnvelope<T> envelope)
    {
        return new McpCommandResult<T>(envelope, null);
    }

    public static McpCommandResult<T> Fail(int code, string message)
    {
        return new McpCommandResult<T>(null, new McpToolError(code, message));
    }

    public static McpCommandResult<T> Fail(McpErrorCode code, string message)
    {
        return Fail((int)code, message);
    }

    public static McpCommandResult<T> Partial(McpEnvelope<T> envelope, McpErrorCode code, string message)
    {
        McpToolError error = new((int)code, message);
        return new McpCommandResult<T>(envelope with { Error = error }, error);
    }
}
