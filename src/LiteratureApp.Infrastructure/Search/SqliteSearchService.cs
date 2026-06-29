using System.Text;
using Dapper;
using LiteratureApp.Core.Ids;
using LiteratureApp.Core.Results;
using LiteratureApp.Infrastructure.Database;
using LiteratureApp.Search;

namespace LiteratureApp.Infrastructure.Search;

public sealed class SqliteSearchService : ISearchService
{
    private const int MatchedUnitsPerPage = 5;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IQueryRewriter? _rewriter;

    public SqliteSearchService(SqliteConnectionFactory connectionFactory, IQueryRewriter? rewriter = null)
    {
        _connectionFactory = connectionFactory; _rewriter = rewriter;
    }

    public async Task<Result<SearchResultPage>> SearchLibraryAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return Result<SearchResultPage>.Failure(AppErrorCodes.ValidationFailed, "Search query is required.");
        }

        try
        {
            SearchRewritePlan? plan = null;
            if (_rewriter is not null)
            {
                await using var planConnection = _connectionFactory.CreateConnection(); await planConnection.OpenAsync(cancellationToken);
                var libraryText = await planConnection.ExecuteScalarAsync<string?>("select library_id from library_metadata limit 1;");
                if (libraryText is null) return Result<SearchResultPage>.Failure(AppErrorCodes.NotFound, "Current library was not found.");
                var planResult = await _rewriter.BuildRewritePlanAsync(request.Query, new SearchRewriteOptions(LibraryId.Parse(libraryText), request.ProfileId, null, request.ProfileAlias, request.PreviewRewriteOnly), cancellationToken);
                if (planResult.IsFailure) return Result<SearchResultPage>.Failure(planResult.ErrorCode!, planResult.ErrorMessage!);
                plan = planResult.Value;
                if (request.PreviewRewriteOnly) return Result<SearchResultPage>.Success(new SearchResultPage(Array.Empty<SearchPageResult>(), null, 0, SearchIndexStatusValue.Current, null, request.IncludeRewritePlan ? plan : null));
            }
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var libraryId = await connection.ExecuteScalarAsync<string?>("select library_id from library_metadata limit 1;");
            var status = libraryId is null ? null : await GetStatusAsync(connection, SearchIndexScopeType.Library, libraryId);
            var indexStatus = status?.Status ?? SearchIndexStatusValue.Current;
            if (indexStatus == SearchIndexStatusValue.Unavailable)
            {
                return Result<SearchResultPage>.Success(new SearchResultPage(Array.Empty<SearchPageResult>(), null, 0, indexStatus, status?.Reason ?? status?.AffectedScopesSummary));
            }

            var pageSize = Math.Clamp(request.PageSize <= 0 ? 20 : request.PageSize, 1, 100);
            var offset = DecodeCursor(request.Cursor);
            var queries = plan?.ExpandedQueries ?? [request.Query];
            var match = string.Join(" OR ", queries.Select(BuildFtsQuery).Distinct(StringComparer.Ordinal));
            var pageRows = (await connection.QueryAsync<PageHitRow>(
                """
                with matched_pages as (
                    select su.page_id as PageId, min(p.page_index) as PageIndex, count(*) as MatchCount
                    from search_units_fts f
                    join search_units su on su.unit_id = f.unit_id
                    join pages p on p.page_id = su.page_id
                    join document_instances di on di.document_instance_id = su.document_instance_id
                    where search_units_fts match @Match
                      and su.status = @Status
                      and (@DocumentInstanceId is null or su.document_instance_id = @DocumentInstanceId)
                      and (@IncludeDeprecated = 1 or di.status <> 'deprecated')
                    group by su.page_id
                )
                select PageId, PageIndex, MatchCount
                from matched_pages
                order by PageIndex, PageId
                limit @Limit offset @Offset;
                """,
                new
                {
                    Match = match,
                    Status = SearchUnitStatus.Current,
                    DocumentInstanceId = request.DocumentInstanceId?.ToString(),
                    IncludeDeprecated = request.IncludeDeprecatedInstances ? 1 : 0,
                    Limit = pageSize + 1,
                    Offset = offset
                })).ToArray();

            var selectedPages = pageRows.Take(pageSize).ToArray();
            var results = new List<SearchPageResult>();
            foreach (var page in selectedPages)
            {
                var matchedRows = (await connection.QueryAsync<UnitHitRow>(
                    """
                    select su.unit_id as UnitId, su.page_id as PageId, su.resolved_text as Text, su.node_type as NodeType,
                           su.reading_order as ReadingOrder, su.layout_revision_id as LayoutRevisionId,
                           i.item_id as ItemId, i.title as ItemTitle, di.document_instance_id as DocumentInstanceId,
                           p.page_label as PageLabel, p.page_index as PageIndex
                    from search_units_fts f
                    join search_units su on su.unit_id = f.unit_id
                    join pages p on p.page_id = su.page_id
                    join document_instances di on di.document_instance_id = su.document_instance_id
                    join items i on i.item_id = di.item_id
                    where search_units_fts match @Match
                      and su.page_id = @PageId
                      and su.status = @Status
                    order by su.reading_order, su.unit_id
                    limit @Limit;
                    """,
                    new { Match = match, PageId = page.PageId, Status = SearchUnitStatus.Current, Limit = MatchedUnitsPerPage + 1 })).ToArray();
                var first = matchedRows.First();
                results.Add(new SearchPageResult(
                    ItemId.Parse(first.ItemId),
                    first.ItemTitle,
                    DocumentInstanceId.Parse(first.DocumentInstanceId),
                    PageId.Parse(first.PageId),
                    first.PageLabel,
                    first.PageIndex,
                    matchedRows.Take(MatchedUnitsPerPage).Select(row => row.ToMatchedUnit(true)).ToArray(),
                    matchedRows.Length > MatchedUnitsPerPage,
                    indexStatus));
            }

            var nextCursor = pageRows.Length > pageSize ? EncodeCursor(offset + pageSize) : null;
            return Result<SearchResultPage>.Success(new SearchResultPage(results, nextCursor, null, indexStatus, status?.AffectedScopesSummary ?? status?.Reason, request.IncludeRewritePlan ? plan : null));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result<SearchResultPage>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}"); }
    }

    public async Task<Result<IReadOnlyList<SearchMatchedUnit>>> GetSearchResultContextAsync(SearchUnitId unitId, int before = 2, int after = 2, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var row = await connection.QuerySingleOrDefaultAsync<UnitHitRow>(
                "select unit_id as UnitId, page_id as PageId, resolved_text as Text, node_type as NodeType, reading_order as ReadingOrder, layout_revision_id as LayoutRevisionId from search_units where unit_id = @UnitId;",
                new { UnitId = unitId.ToString() });
            if (row is null)
            {
                return Result<IReadOnlyList<SearchMatchedUnit>>.Failure(AppErrorCodes.NotFound, "Search unit was not found.");
            }

            before = Math.Clamp(before, 0, 10);
            after = Math.Clamp(after, 0, 10);
            var siblings = (await connection.QueryAsync<UnitHitRow>(
                """
                select unit_id as UnitId, page_id as PageId, resolved_text as Text, node_type as NodeType,
                       reading_order as ReadingOrder, layout_revision_id as LayoutRevisionId
                from search_units
                where page_id = @PageId
                  and layout_revision_id = @RevisionId
                  and status = @Status
                order by reading_order, unit_id;
                """,
                new { row.PageId, RevisionId = row.LayoutRevisionId, Status = SearchUnitStatus.Current })).ToArray();
            var index = Array.FindIndex(siblings, s => s.UnitId == row.UnitId);
            if (index < 0)
            {
                return Result<IReadOnlyList<SearchMatchedUnit>>.Success(Array.Empty<SearchMatchedUnit>());
            }
            var start = Math.Max(0, index - before);
            var end = Math.Min(siblings.Length - 1, index + after);
            return Result<IReadOnlyList<SearchMatchedUnit>>.Success(siblings[start..(end + 1)].Select(s => s.ToMatchedUnit(s.UnitId == row.UnitId)).ToArray());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result<IReadOnlyList<SearchMatchedUnit>>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}"); }
    }

    private static async Task<SearchIndexStatus?> GetStatusAsync(Microsoft.Data.Sqlite.SqliteConnection connection, string scopeType, string scopeId)
    {
        var row = await connection.QuerySingleOrDefaultAsync<StatusRow>(
            "select scope_type as ScopeType, scope_id as ScopeId, status as Status, pending_document_count as PendingDocumentCount, pending_unit_count as PendingUnitCount, progress_percent as ProgressPercent, affected_scopes_summary as AffectedScopesSummary, reason as Reason, updated_at as UpdatedAt from search_index_status where scope_type = @ScopeType and scope_id = @ScopeId;",
            new { ScopeType = scopeType, ScopeId = scopeId });
        return row?.ToStatus();
    }

    private static string BuildFtsQuery(string query)
    {
        var tokens = new List<string>();
        var raw = query.Trim();
        tokens.Add(QuoteFts(raw));
        var cjk = raw.Where(IsCjk).ToArray();
        for (var i = 0; i < cjk.Length - 1; i++)
        {
            tokens.Add(QuoteFts($"{cjk[i]}{cjk[i + 1]}"));
        }
        foreach (var token in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            tokens.Add(QuoteFts(token));
        }
        return string.Join(" OR ", tokens.Distinct(StringComparer.Ordinal));
    }

    private static bool IsCjk(char c)
        => (c >= '\u3400' && c <= '\u9fff') || (c >= '\uf900' && c <= '\ufaff');

    private static string QuoteFts(string value)
        => "\"" + value.Replace("\"", "\"\"") + "\"";

    private static string? EncodeCursor(int offset)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(offset.ToString()));

    private static int DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return 0;
        try
        {
            return int.TryParse(Encoding.UTF8.GetString(Convert.FromBase64String(cursor)), out var offset) ? Math.Max(0, offset) : 0;
        }
        catch
        {
            return 0;
        }
    }

    private sealed class PageHitRow
    {
        public string PageId { get; set; } = "";
        public int PageIndex { get; set; }
        public int MatchCount { get; set; }
    }

    private sealed class UnitHitRow
    {
        public string UnitId { get; set; } = "";
        public string PageId { get; set; } = "";
        public string Text { get; set; } = "";
        public string NodeType { get; set; } = "";
        public int ReadingOrder { get; set; }
        public string LayoutRevisionId { get; set; } = "";
        public string ItemId { get; set; } = "";
        public string ItemTitle { get; set; } = "";
        public string DocumentInstanceId { get; set; } = "";
        public string? PageLabel { get; set; }
        public int PageIndex { get; set; }
        public SearchMatchedUnit ToMatchedUnit(bool isMatch) => new(SearchUnitId.Parse(UnitId), Core.Ids.PageId.Parse(PageId), Text, NodeType, ReadingOrder, Core.Ids.LayoutRevisionId.Parse(LayoutRevisionId), isMatch);
    }

    private sealed class StatusRow
    {
        public string ScopeType { get; set; } = "";
        public string ScopeId { get; set; } = "";
        public string Status { get; set; } = "";
        public int PendingDocumentCount { get; set; }
        public int PendingUnitCount { get; set; }
        public double? ProgressPercent { get; set; }
        public string? AffectedScopesSummary { get; set; }
        public string? Reason { get; set; }
        public string UpdatedAt { get; set; } = "";
        public SearchIndexStatus ToStatus() => new(ScopeType, ScopeId, Status, PendingDocumentCount, PendingUnitCount, ProgressPercent, AffectedScopesSummary, Reason, DateTimeOffset.Parse(UpdatedAt));
    }
}
