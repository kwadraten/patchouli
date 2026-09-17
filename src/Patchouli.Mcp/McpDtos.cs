using System.Text.Json.Serialization;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Search;

namespace Patchouli.Mcp;

public sealed record McpSearchLibraryRequest(
    string Query,
    int PageSize = 20,
    string? Cursor = null,
    DocumentInstanceId? DocumentInstanceId = null,
    SearchProfileId? ProfileId = null,
    string? ProfileAlias = null,
    bool IncludeRewritePlan = true,
    bool PreviewRewriteOnly = false);

public sealed record McpSearchLibraryResponse(
    IReadOnlyList<McpSearchPageResult> Results,
    string? NextCursor,
    int? EstimatedTotal,
    string IndexStatus,
    string? AffectedScopesSummary,
    string? Warning,
    SearchRewritePlan? RewritePlan = null);

public sealed record McpSearchPageResult(
    string ItemTitle,
    ItemId ItemId,
    DocumentInstanceId DocumentInstanceId,
    PageId PageId,
    string? PageLabel,
    int PageIndex,
    IReadOnlyList<McpMatchedUnit> MatchedUnits,
    string SourceFileStatus);

public sealed record McpMatchedUnit(
    SearchUnitId UnitId,
    string Text,
    string BoxType,
    int Ordinal,
    DocumentTreeRevisionId TreeRevisionId,
    DocumentBoxId BoxId,
    bool IsMatch);

public sealed record McpItemIdentifier(string Scheme, string Value, string? Note);

public sealed record McpItemCreator(
    string Role,
    string? Family,
    string? Given,
    string? Literal,
    string? Suffix,
    string? Particles,
    int SequenceIndex,
    string DisplayName);

public sealed record McpItemDate(
    string Role,
    string DatePartsJson,
    bool Circa,
    string? Season,
    string? Literal);

public sealed record McpItemMetadataResponse(
    ItemId ItemId,
    string ItemType,
    string CitationKey,
    string Title,
    string? Subtitle,
    string? TitleShort,
    string CreatorsJson,
    string? Date,
    string? PublicationTitle,
    string? ContainerTitleShort,
    string? CollectionTitle,
    string? Publisher,
    string? Place,
    string? Edition,
    string? Genre,
    string? Number,
    string? ChapterNumber,
    string? Volume,
    string? Version,
    string? Issue,
    string? Pages,
    string? Language,
    string? Status,
    string? Note,
    string? Abstract,
    string TagsJson,
    string CollectionsJson,
    string CustomFieldsJson,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<McpItemCreator> Creators,
    IReadOnlyList<McpItemDate> Dates,
    IReadOnlyList<McpItemIdentifier> Identifiers);

public sealed record McpDocumentStatusResponse(
    DocumentInstanceId DocumentInstanceId,
    bool HasOcrText,
    bool HasCurrentLayout,
    bool IsSearchIndexed,
    string SourceFileStatus,
    string? Warning,
    string DocumentStatus = "missing_source");

public sealed record McpPageTextRequest(
    PageId PageId,
    bool IncludeSuppressed = false);

public sealed record McpPageTextResponse(
    PageId PageId,
    DocumentInstanceId DocumentInstanceId,
    string? PageLabel,
    int PageIndex,
    string Text,
    DocumentTreeRevisionId TreeRevisionId,
    IReadOnlyList<string> Warnings);

public sealed record McpPageBlocksRequest(
    PageId PageId,
    bool IncludeBbox = false,
    bool IncludeSuppressed = false);

public sealed record McpPageBlocksResponse(
    PageId PageId,
    string? PageLabel,
    int PageIndex,
    DocumentTreeRevisionId TreeRevisionId,
    IReadOnlyList<McpPageBlock> Blocks,
    IReadOnlyList<string> Warnings);

public sealed record McpPageBlock(
    DocumentBoxId BoxId,
    DocumentTreeRevisionId TreeRevisionId,
    string BoxType,
    string Text,
    int Ordinal,
    bool Suppressed,
    NormalizedBBox? BBox);

/// <summary>Request for the derived translation of one page.</summary>
public sealed record McpPageTranslationRequest(DocumentInstanceId DocumentInstanceId, PageId PageId);

/// <summary>
/// Translation progress of one page projected for agents. <see cref="StaleBoxIds"/> lists the
/// content boxes of the current revision that still render their source text, so an agent can
/// translate exactly those boxes next.
/// </summary>
public sealed record McpTranslationStatusProjection(
    [property: JsonPropertyName("translated_box_count")]
    int TranslatedBoxCount,
    [property: JsonPropertyName("total_box_count")]
    int TotalBoxCount,
    [property: JsonPropertyName("stale_box_ids")]
    IReadOnlyList<string> StaleBoxIds,
    [property: JsonPropertyName("source_tree_revision_id")]
    string SourceTreeRevisionId,
    [property: JsonPropertyName("current")]
    bool IsCurrent);

public sealed record McpPageTranslationResponse(
    PageId PageId,
    DocumentInstanceId DocumentInstanceId,
    string? PageLabel,
    int PageIndex,
    string Markdown,
    DocumentTreeRevisionId TreeRevisionId,
    McpTranslationStatusProjection Status);

/// <summary>One structural mismatch between a submitted translation and its source page.</summary>
public sealed record McpTranslationStructureError(
    [property: JsonPropertyName("block_index")]
    int BlockIndex,
    [property: JsonPropertyName("expected")]
    string Expected,
    [property: JsonPropertyName("actual")] string Actual);

public sealed record McpSearchContextRequest(
    SearchUnitId SearchUnitId,
    int Before = 2,
    int After = 2);

public sealed record McpSearchContextResponse(
    IReadOnlyList<McpContextUnit> Units,
    string? Warning);

/// <summary>
/// The persistent identity and monotonic protocol revision of the host's current Library.
/// library_revision is always formatted lib:&lt;positive decimal integer&gt; and strictly
/// increases after every successful protocol-visible write, surviving host handoffs.
/// </summary>
public sealed record McpLibraryStateResponse(
    string LibraryId,
    string LibraryRevision);

/// <summary>The fixed Library identity projection exposed at patchouli://library.toon.</summary>
public sealed record McpLibraryProjection(
    [property: JsonPropertyName("library_id")]
    string LibraryId,
    [property: JsonPropertyName("display_name")]
    string DisplayName,
    [property: JsonPropertyName("tags")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<McpLibraryTag>? Tags,
    [property: JsonPropertyName("collections")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<McpLibraryCollection>? Collections);

public sealed record McpLibraryTag(
    [property: JsonPropertyName("tag")] string Tag,
    [property: JsonPropertyName("item_count")]
    int ItemCount);

public sealed record McpLibraryCollection(
    [property: JsonPropertyName("collection_id")]
    string CollectionId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("item_count")]
    int ItemCount);

public sealed record McpContextUnit(
    SearchUnitId UnitId,
    string Text,
    NormalizedBBox? BBox,
    bool IsMatch,
    int Ordinal,
    PageId PageId,
    DocumentTreeRevisionId TreeRevisionId,
    DocumentBoxId BoxId);

public sealed record McpCslStyleSummary(
    string StyleId,
    string DisplayName,
    string? Locale,
    bool Enabled);

public sealed record McpCslStyleResponse(
    string StyleId,
    string DisplayName,
    string? Locale,
    bool Enabled,
    string? SourceUrl,
    string ContentHash,
    string ContentXml);

public sealed record McpRenderBibliographyRequest(
    IReadOnlyList<ItemId> ItemIds,
    string? StyleId = null,
    string? Locale = null,
    bool AllowGeneralAsMisc = true);

public sealed record McpRenderBibliographyResponse(
    string StyleId,
    string StyleDisplayName,
    string? Locale,
    IReadOnlyList<ItemId> ItemIds,
    string RenderedText,
    string RenderedHtml,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);

public static class McpSourceFileStatus
{
    public const string Available = "available";
    public const string Missing = "missing";
    public const string OfflineRoot = "offline_root";
    public const string Changed = "changed";
    public const string Conflict = "conflict";
    public const string Unknown = "unknown";
}

public static class McpErrorCodes
{
    public const string Disabled = "disabled";
    public const string ToolUnavailable = "tool_unavailable";
    public const string Unauthorized = "unauthorized";
    public const string UnsafeConfiguration = "unsafe_configuration";
}
