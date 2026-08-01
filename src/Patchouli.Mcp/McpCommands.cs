using System.Text.Json.Serialization;

namespace Patchouli.Mcp;

public sealed record McpFindRequest(
    string? Query,
    string? In,
    IReadOnlyList<McpWhereClause>? Where,
    bool Literal = false,
    int Limit = 20,
    string? Cursor = null,
    bool Long = false);

public sealed record McpWhereClause(string Key, string Value);

public sealed record McpFindMeta(
    [property: JsonPropertyName("library_revision")]
    string LibraryRevision,
    [property: JsonPropertyName("domain_total")]
    int DomainTotal,
    [property: JsonPropertyName("filtered_total")]
    int FilteredTotal,
    [property: JsonPropertyName("shown_total")]
    int ShownTotal);

public sealed record McpFindEntry(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("type")] string Type);

public sealed record McpFindLongEntry(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("item_uri")]
    string? ItemUri,
    [property: JsonPropertyName("document_instance_id")]
    string? DocumentInstanceId,
    [property: JsonPropertyName("page_index")]
    int? PageIndex,
    [property: JsonPropertyName("evidence_ref")]
    string? EvidenceRef,
    [property: JsonPropertyName("item_status")]
    string? ItemStatus,
    [property: JsonPropertyName("document_status")]
    string? DocumentStatus,
    [property: JsonPropertyName("source_status")]
    string? SourceStatus,
    [property: JsonPropertyName("style_enabled")]
    bool? StyleEnabled,
    [property: JsonPropertyName("citable")]
    bool Citable);

public sealed record McpFetchRequest(IReadOnlyList<string> Uris, string? Range, int? LimitBytes);

public sealed record McpFetchMeta(
    [property: JsonPropertyName("library_revision")]
    string LibraryRevision);

public sealed record McpFetchResult(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("resource_type")]
    string? ResourceType,
    [property: JsonPropertyName("item_uri")]
    string? ItemUri,
    [property: JsonPropertyName("content")]
    string? Content,
    [property: JsonPropertyName("complete")]
    bool Complete,
    [property: JsonPropertyName("truncated")]
    bool Truncated,
    [property: JsonPropertyName("returned_bytes")]
    int ReturnedBytes,
    [property: JsonPropertyName("limit_bytes")]
    int LimitBytes,
    [property: JsonPropertyName("continuation")]
    string? Continuation,
    [property: JsonPropertyName("next_range")]
    string? NextRange,
    [property: JsonPropertyName("error")] McpToolError? Error);

public sealed record McpCiteRequest(
    IReadOnlyList<string> Refs,
    string? Style,
    string? Locale,
    bool Bibliography,
    bool Html);

public sealed record McpCiteMeta(
    [property: JsonPropertyName("library_revision")]
    string LibraryRevision,
    [property: JsonPropertyName("effective_style_uri")]
    string? EffectiveStyleUri,
    [property: JsonPropertyName("effective_locale")]
    string? EffectiveLocale,
    [property: JsonPropertyName("render_format")]
    string RenderFormat,
    [property: JsonPropertyName("bibliography")]
    string? Bibliography);

public sealed record McpCitationResult(
    [property: JsonPropertyName("ref")] string Ref,
    [property: JsonPropertyName("item_uri")]
    string? ItemUri,
    [property: JsonPropertyName("citation")]
    string? Citation,
    [property: JsonPropertyName("error")] McpToolError? Error);

public sealed record McpPutMeta(
    [property: JsonPropertyName("library_revision")]
    string LibraryRevision);

public sealed record McpPutResult(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("resource_type")]
    string ResourceType,
    [property: JsonPropertyName("committed")]
    bool Committed,
    [property: JsonPropertyName("content_bytes")]
    int ContentBytes);

/// <summary>Stable find warning codes carried in message.warnings.</summary>
public static class McpWarningCodes
{
    public const string ResultSetMayHaveChanged = "RESULT_SET_MAY_HAVE_CHANGED";
    public const string WhitespaceQueryTreatedAsBrowse = "WHITESPACE_QUERY_TREATED_AS_BROWSE";
    public const string CursorContextRestored = "CURSOR_CONTEXT_RESTORED";
    public const string RootDiscoveryPaginated = "ROOT_DISCOVERY_PAGINATED";
    public const string FileUriSingletonScope = "FILE_URI_SINGLETON_SCOPE";
    public const string WhereValueContainsEquals = "WHERE_VALUE_CONTAINS_EQUALS";
    public const string DuplicateWhereKeyLastWins = "DUPLICATE_WHERE_KEY_LAST_WINS";
    public const string LibraryChangedSinceLastResponse = "LIBRARY_CHANGED_SINCE_LAST_RESPONSE";
}

/// <summary>Stable system document-status values used by detailed projections and filters.</summary>
public static class McpDocumentStatusValue
{
    public const string Indexed = "indexed";
    public const string Layout = "layout";
    public const string Ocr = "ocr";
    public const string Unavailable = "unavailable";
}
