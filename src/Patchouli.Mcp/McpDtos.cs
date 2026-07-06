using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Search;

namespace Patchouli.Mcp;

public sealed record McpSearchLibraryRequest(
    string Query,
    int PageSize = 20,
    string? Cursor = null,
    DocumentInstanceId? DocumentInstanceId = null,
    bool IncludeEvidenceRefs = true,
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
    string? EvidenceRef,
    string Text,
    string NodeType,
    int ReadingOrder,
    LayoutRevisionId LayoutRevisionId,
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
    IReadOnlyList<McpItemCreator> Creators,
    IReadOnlyList<McpItemDate> Dates,
    IReadOnlyList<McpItemIdentifier> Identifiers);

public sealed record McpDocumentStatusResponse(
    DocumentInstanceId DocumentInstanceId,
    bool HasOcrText,
    bool HasCurrentLayout,
    bool IsSearchIndexed,
    string SourceFileStatus,
    string? Warning);

public sealed record McpPageTextRequest(
    PageId PageId,
    string ReadMode = McpReadMode.Current,
    string? EvidenceRef = null,
    bool IncludeAnnotations = false);

public sealed record McpPageTextResponse(
    PageId PageId,
    string? PageLabel,
    int PageIndex,
    string Text,
    string ReadMode,
    string? EvidenceRef,
    IReadOnlyList<string> Warnings);

public sealed record McpPageBlocksRequest(
    PageId PageId,
    string ReadMode = McpReadMode.Current,
    string? EvidenceRef = null,
    bool IncludeBbox = false,
    bool IncludeAnnotations = false);

public sealed record McpPageBlocksResponse(
    PageId PageId,
    string? PageLabel,
    int PageIndex,
    IReadOnlyList<McpPageBlock> Blocks,
    string ReadMode,
    IReadOnlyList<string> Warnings);

public sealed record McpPageBlock(
    LayoutNodeId NodeId,
    string NodeType,
    string Text,
    int ReadingOrder,
    string? EvidenceRef,
    NormalizedBBox? BBox);

public sealed record McpSearchContextRequest(
    SearchUnitId SearchUnitId,
    int Before = 2,
    int After = 2,
    bool IncludeEvidenceRefs = true);

public sealed record McpSearchContextResponse(
    IReadOnlyList<McpContextUnit> Units,
    string? Warning);

public sealed record McpContextUnit(
    SearchUnitId UnitId,
    string? EvidenceRef,
    string Text,
    NormalizedBBox? BBox,
    bool IsMatch,
    int ReadingOrder,
    PageId PageId,
    LayoutRevisionId LayoutRevisionId);

public static class McpReadMode
{
    public const string Current = "current";
    public const string Pinned = "pinned";
    public const string Compare = "compare";
}

public static class McpSourceFileStatus
{
    public const string Available = "available";
    public const string Missing = "missing";
    public const string OfflineRoot = "offline_root";
    public const string Changed = "changed";
    public const string Conflict = "conflict";
    public const string Unknown = "unknown";
}
