using Patchouli.Core.Ids;

namespace Patchouli.Search;

public sealed record SearchUnit(
    SearchUnitId UnitId,
    DocumentInstanceId DocumentInstanceId,
    PageId PageId,
    DocumentBoxId BoxId,
    DocumentTreeRevisionId TreeRevisionId,
    string ResolvedText,
    string BBoxJson,
    string BoxType,
    int Ordinal,
    string Status,
    SearchUnitId? SupersedesUnitId,
    SearchUnitId? SupersededByUnitId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public static class SearchIndexScopeType
{
    public const string Library = "library";
    public const string DocumentInstance = "document_instance";
    public const string Page = "page";
}

public static class SearchIndexStatusValue
{
    public const string Current = "current";
    public const string Stale = "stale";
    public const string Partial = "partial";
    public const string Unavailable = "unavailable";
}

public static class SearchUnitStatus
{
    public const string Current = "current";
    public const string Stale = "stale";
    public const string Deleted = "deleted";
    public const string Hidden = "hidden";
}

public sealed record SearchRequest(
    string Query,
    DocumentInstanceId? DocumentInstanceId = null,
    int PageSize = 20,
    string? Cursor = null,
    bool IncludeDeprecatedInstances = false,
    SearchProfileId? ProfileId = null,
    string? ProfileAlias = null,
    bool PreviewRewriteOnly = false,
    bool IncludeRewritePlan = true);

public sealed record SearchPageResult(
    ItemId ItemId,
    string ItemTitle,
    DocumentInstanceId DocumentInstanceId,
    PageId PageId,
    string? PageLabel,
    int PageIndex,
    IReadOnlyList<SearchMatchedUnit> MatchedUnits,
    bool MatchedUnitsHasMore,
    string IndexStatus);

public sealed record SearchMatchedUnit(
    SearchUnitId UnitId,
    PageId PageId,
    DocumentBoxId BoxId,
    string Text,
    string BoxType,
    int Ordinal,
    DocumentTreeRevisionId TreeRevisionId,
    bool IsMatch);

public sealed record SearchResultPage(
    IReadOnlyList<SearchPageResult> Results,
    string? NextCursor,
    int? EstimatedTotal,
    string IndexStatus,
    string? AffectedScopesSummary,
    SearchRewritePlan? RewritePlan = null);

public sealed record SearchIndexStatus(
    string ScopeType,
    string ScopeId,
    string Status,
    int PendingDocumentCount,
    int PendingUnitCount,
    double? ProgressPercent,
    string? AffectedScopesSummary,
    string? Reason,
    DateTimeOffset UpdatedAt);

public interface ISearchUnitBuilder
{
    Task<Core.Results.Result> RebuildForDocumentInstanceAsync(DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default);

    Task<Core.Results.Result> RebuildForPageAsync(PageId pageId, DocumentTreeRevisionId treeRevisionId,
        CancellationToken cancellationToken = default);

    Task<Core.Results.Result> MarkDocumentInstanceDirtyAsync(DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default);
}

public interface ISearchDirtyMarker
{
    Task<Core.Results.Result> MarkDocumentInstanceDirtyAsync(DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default);
}

public interface ISearchIndexRebuilder
{
    Task<Core.Results.Result> RebuildFtsForDocumentInstanceAsync(DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default);

    Task<Core.Results.Result> RebuildFtsForLibraryAsync(CancellationToken cancellationToken = default);

    Task<Core.Results.Result> SetIndexUnavailableAsync(string scopeType, string scopeId, string reason,
        CancellationToken cancellationToken = default);
}
