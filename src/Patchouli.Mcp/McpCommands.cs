using System.Text.Json;
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

/// <summary>
/// Translation-directory entry for one document. The progress counters are scope-specific and
/// intentionally present in the default projection: the whole point of browsing
/// <c>patchouli://translations/</c> is to find pages that still need translation work.
/// </summary>
public sealed record McpTranslationDocumentEntry(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("page_count")]
    int PageCount,
    [property: JsonPropertyName("translated_page_count")]
    int TranslatedPageCount,
    [property: JsonPropertyName("partial_page_count")]
    int PartialPageCount,
    [property: JsonPropertyName("untranslated_page_count")]
    int UntranslatedPageCount,
    [property: JsonPropertyName("stale_page_count")]
    int StalePageCount);

/// <summary>Translation-directory entry for one page, including its current status.</summary>
public sealed record McpTranslationPageEntry(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("translation_status")]
    string TranslationStatus,
    [property: JsonPropertyName("translated_box_count")]
    int TranslatedBoxCount,
    [property: JsonPropertyName("total_box_count")]
    int TotalBoxCount);

/// <summary>
/// Long projection of the fixed <c>patchouli://library.yml</c> singleton. It carries only the
/// universal entry fields because library identity, tags, and collections are already the
/// resource content returned by <c>fetch</c>; it is never an Item projection.
/// </summary>
public sealed record McpLibraryLongEntry(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("type")] string Type);

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
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("translation")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    McpTranslationStatusProjection? Translation = null,
    [property: JsonPropertyName("content_type")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ContentType = null);

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
    int ContentBytes,
    [property: JsonPropertyName("translation_errors")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<McpTranslationStructureError>? TranslationErrors = null,
    [property: JsonPropertyName("source_revision")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SourceRevision = null,
    [property: JsonPropertyName("translation_version")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? TranslationVersion = null,
    [property: JsonPropertyName("modified_box_ids")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? ModifiedBoxIds = null,
    [property: JsonPropertyName("box_errors")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<McpBoxPutError>? BoxErrors = null);

public sealed record McpBoxPutError(
    [property: JsonPropertyName("box_id")] string BoxId,
    [property: JsonPropertyName("message")]
    string Message);

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

/// <summary>Issue #11: request to import one host-local file into the library.</summary>
/// <param name="Path">Host-local file path. URLs are rejected.</param>
/// <param name="Kind">Optional explicit kind ("pdf" or "csl"); null content-sniffs.</param>
public sealed record McpAddRequest(
    string Path,
    string? Kind = null);

public sealed record McpAddMeta(
    [property: JsonPropertyName("library_revision")]
    string LibraryRevision);

public sealed record McpAddIssue(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")]
    string Message);

public sealed record McpAddResult(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("reused")] bool Reused,
    [property: JsonPropertyName("moved")] bool Moved,
    [property: JsonPropertyName("item_uri")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ItemUri = null,
    [property: JsonPropertyName("text_uri")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? TextUri = null,
    [property: JsonPropertyName("style_uri")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? StyleUri = null,
    [property: JsonPropertyName("page_count")]
    int PageCount = 0,
    [property: JsonPropertyName("import_status")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ImportStatus = null,
    [property: JsonPropertyName("body_status")]
    string BodyStatus = "ingested",
    [property: JsonPropertyName("issues")] IReadOnlyList<McpAddIssue>? Issues = null);

/// <summary>Issue #11: request to read one source image for text-image collation.</summary>
/// <param name="Uri">Page, box/evidence or translation page/box URI.</param>
/// <param name="Context">"page" (default) or "region".</param>
/// <param name="MaxEdge">Longest-edge pixel cap; null selects the validated default.</param>
public sealed record McpViewRequest(
    string Uri,
    string? Context = null,
    int? MaxEdge = null);

public sealed record McpViewMeta(
    [property: JsonPropertyName("library_revision")]
    string LibraryRevision);

/// <summary>
/// Issue #11: image metadata carried in the text content block of a view response.
/// The base64 PNG itself travels in the native MCP image content block, never inside
/// this text envelope.
/// </summary>
public sealed record McpViewImageMetadata(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("view_type")]
    string ViewType,
    [property: JsonPropertyName("mime_type")]
    string MimeType,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("crop")] IReadOnlyList<double> Crop,
    [property: JsonPropertyName("image_index")]
    int ImageIndex);

/// <summary>
/// Issue #11: the full outcome of a view command. The envelope carries the text
/// metadata; <see cref="PngBytes"/> carries the image bytes for the native image
/// content block (null when the command failed).
/// </summary>
public sealed record McpViewCommandResult(
    McpCommandResult<McpViewMeta, McpViewImageMetadata> Result,
    byte[]? PngBytes);

public sealed record McpOcrResult(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("task_id")]
    string TaskId,
    [property: JsonPropertyName("status_uri")]
    string StatusUri,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("engine_id")]
    string EngineId,
    [property: JsonPropertyName("preset_id")]
    string PresetId,
    [property: JsonPropertyName("preset_version_id")]
    string PresetVersionId,
    [property: JsonPropertyName("reused")] bool Reused = false);

/// <summary>Invoke one typed domain Action.</summary>
public sealed record McpActionSendRequest(string Target, string Action, JsonElement Args);

public sealed record McpActionSendMeta(
    [property: JsonPropertyName("library_revision")]
    string LibraryRevision,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("state")] string State);

public sealed record McpActionSendResult(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("result")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    object? Result,
    [property: JsonPropertyName("run_uri")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? RunUri);
