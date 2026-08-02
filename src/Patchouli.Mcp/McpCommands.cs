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

public sealed record McpItemLongEntry(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("item_status")]
    string ItemStatus,
    [property: JsonPropertyName("primary_document_ocr_index_status")]
    string PrimaryDocumentOcrIndexStatus,
    [property: JsonPropertyName("citable")]
    bool Citable);

public sealed record McpTextLongEntry(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("item_uri")]
    string? ItemUri,
    [property: JsonPropertyName("item_status")]
    string? ItemStatus,
    [property: JsonPropertyName("document_status")]
    string DocumentStatus,
    [property: JsonPropertyName("source_status")]
    string SourceStatus,
    [property: JsonPropertyName("ocr_index_status")]
    string OcrIndexStatus,
    [property: JsonPropertyName("citable")]
    bool Citable);

public sealed record McpStyleLongEntry(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("style_enabled")]
    bool StyleEnabled);

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
    [property: JsonPropertyName("error")] string? Error);

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
    [property: JsonPropertyName("error")] string? Error);

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

/// <summary>Stable warning names rendered as compact terminal-style message lines.</summary>
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

    public static string ToTerminalLine(string warning)
    {
        return warning switch
        {
            WhitespaceQueryTreatedAsBrowse =>
                "WHITESPACE_QUERY_TREATED_AS_BROWSE: query contained only whitespace; browsing the selected scope.",
            CursorContextRestored =>
                "CURSOR_CONTEXT_RESTORED: cursor scope and filters replaced the conflicting request values.",
            RootDiscoveryPaginated =>
                "ROOT_DISCOVERY_PAGINATED: root discovery was paginated; continue with the returned cursor.",
            ResultSetMayHaveChanged =>
                "RESULT_SET_MAY_HAVE_CHANGED: live pagination may have changed since the previous page.",
            FileUriSingletonScope =>
                "FILE_URI_SINGLETON_SCOPE: a file URI selects one resource; limit and cursor do not apply.",
            WhereValueContainsEquals =>
                "WHERE_VALUE_CONTAINS_EQUALS: the filter value contains '='; text after the first '=' was preserved.",
            DuplicateWhereKeyLastWins =>
                "DUPLICATE_WHERE_KEY_LAST_WINS: the last value for each repeated filter key was used.",
            LibraryChangedSinceLastResponse =>
                "LIBRARY_CHANGED_SINCE_LAST_RESPONSE: the Library changed since this MCP session's previous response.",
            _ when warning.Contains(": ", StringComparison.Ordinal) => warning,
            _ => $"{warning}: the host adjusted the request or result."
        };
    }
}
