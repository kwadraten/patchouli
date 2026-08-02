using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Database;
using Patchouli.Core.Search;

namespace Patchouli.Infrastructure.Search;

public sealed class SqliteSearchService : ISearchService
{
    private const int MatchedUnitsPerPage = 5;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IQueryRewriter? _rewriter;

    public SqliteSearchService(SqliteConnectionFactory connectionFactory, IQueryRewriter? rewriter = null)
    {
        _connectionFactory = connectionFactory;
        _rewriter = rewriter;
    }

    public async Task<Result<SearchResultPage>> SearchLibraryAsync(SearchRequest request,
        CancellationToken cancellationToken = default)
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
                await using SqliteConnection planConnection = _connectionFactory.CreateReadConnection();
                await planConnection.OpenAsync(cancellationToken);
                string? libraryText =
                    await planConnection.ExecuteScalarAsync<string?>(
                        "select library_id from library_metadata limit 1;");
                if (libraryText is null)
                {
                    return Result<SearchResultPage>.Failure(AppErrorCodes.NotFound, "Current library was not found.");
                }

                Result<SearchRewritePlan> planResult = await _rewriter.BuildRewritePlanAsync(request.Query,
                    new SearchRewriteOptions(LibraryId.Parse(libraryText), request.ProfileId, null,
                        request.ProfileAlias, request.PreviewRewriteOnly), cancellationToken);
                if (planResult.IsFailure)
                {
                    return Result<SearchResultPage>.Failure(planResult.ErrorCode!, planResult.ErrorMessage!);
                }

                plan = planResult.Value;
                if (request.PreviewRewriteOnly)
                {
                    return Result<SearchResultPage>.Success(new SearchResultPage(Array.Empty<SearchPageResult>(), null,
                        0, SearchIndexStatusValue.Current, null, request.IncludeRewritePlan ? plan : null));
                }
            }

            await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);
            string? libraryId =
                await connection.ExecuteScalarAsync<string?>("select library_id from library_metadata limit 1;");
            SearchIndexStatus? status = libraryId is null
                ? null
                : await GetStatusAsync(connection, SearchIndexScopeType.Library, libraryId);
            string indexStatus = status?.Status ?? SearchIndexStatusValue.Current;
            if (indexStatus == SearchIndexStatusValue.Unavailable)
            {
                return Result<SearchResultPage>.Success(new SearchResultPage(Array.Empty<SearchPageResult>(), null, 0,
                    indexStatus, status?.Reason ?? status?.AffectedScopesSummary));
            }

            int pageSize = Math.Clamp(request.PageSize <= 0 ? 20 : request.PageSize, 1, 100);
            int offset = DecodeCursor(request.Cursor);
            IReadOnlyList<string> queries = plan?.ExpandedQueries ?? [request.Query];
            string match = string.Join(" OR ", queries.Select(BuildFtsQuery).Distinct(StringComparer.Ordinal));
            PageHitRow[] pageRows = (await connection.QueryAsync<PageHitRow>(
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

            PageHitRow[] selectedPages = pageRows.Take(pageSize).ToArray();
            List<SearchPageResult> results = new();
            foreach (PageHitRow page in selectedPages)
            {
                UnitHitRow[] matchedRows = (await connection.QueryAsync<UnitHitRow>(
                    """
                    select su.unit_id as UnitId, su.page_id as PageId, su.box_id as BoxId,
                           su.resolved_text as Text, su.box_type as BoxType,
                           su.ordinal as Ordinal, su.tree_revision_id as TreeRevisionId,
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
                    order by su.ordinal, su.unit_id
                    limit @Limit;
                    """,
                    new
                    {
                        Match = match, PageId = page.PageId, Status = SearchUnitStatus.Current,
                        Limit = MatchedUnitsPerPage + 1
                    })).ToArray();
                UnitHitRow first = matchedRows.First();
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

            string? nextCursor = pageRows.Length > pageSize ? EncodeCursor(offset + pageSize) : null;
            return Result<SearchResultPage>.Success(new SearchResultPage(results, nextCursor, null, indexStatus,
                status?.AffectedScopesSummary ?? status?.Reason, request.IncludeRewritePlan ? plan : null));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.sqlite-search"))
        {
            return Result<SearchResultPage>.Failure(AppErrorCodes.DatabaseError,
                $"Database operation failed: {ex.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<SearchMatchedUnit>>> GetSearchResultContextAsync(SearchUnitId unitId,
        int before = 2, int after = 2, CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);
            UnitHitRow? row = await connection.QuerySingleOrDefaultAsync<UnitHitRow>(
                "select unit_id as UnitId, page_id as PageId, box_id as BoxId, resolved_text as Text, box_type as BoxType, ordinal as Ordinal, tree_revision_id as TreeRevisionId from search_units where unit_id = @UnitId;",
                new { UnitId = unitId.ToString() });
            if (row is null)
            {
                return Result<IReadOnlyList<SearchMatchedUnit>>.Failure(AppErrorCodes.NotFound,
                    "Search unit was not found.");
            }

            before = Math.Clamp(before, 0, 10);
            after = Math.Clamp(after, 0, 10);
            UnitHitRow[] siblings = (await connection.QueryAsync<UnitHitRow>(
                """
                select unit_id as UnitId, page_id as PageId, box_id as BoxId, resolved_text as Text, box_type as BoxType,
                       ordinal as Ordinal, tree_revision_id as TreeRevisionId
                from search_units
                where page_id = @PageId
                  and tree_revision_id = @RevisionId
                  and status = @Status
                order by ordinal, unit_id;
                """,
                new { row.PageId, RevisionId = row.TreeRevisionId, Status = SearchUnitStatus.Current })).ToArray();
            int index = Array.FindIndex(siblings, s => s.UnitId == row.UnitId);
            if (index < 0)
            {
                return Result<IReadOnlyList<SearchMatchedUnit>>.Success(Array.Empty<SearchMatchedUnit>());
            }

            int start = Math.Max(0, index - before);
            int end = Math.Min(siblings.Length - 1, index + after);
            return Result<IReadOnlyList<SearchMatchedUnit>>.Success(siblings[start..(end + 1)]
                .Select(s => s.ToMatchedUnit(s.UnitId == row.UnitId)).ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.sqlite-search"))
        {
            return Result<IReadOnlyList<SearchMatchedUnit>>.Failure(AppErrorCodes.DatabaseError,
                $"Database operation failed: {ex.Message}");
        }
    }

    private static async Task<SearchIndexStatus?> GetStatusAsync(SqliteConnection connection,
        string scopeType, string scopeId)
    {
        StatusRow? row = await connection.QuerySingleOrDefaultAsync<StatusRow>(
            "select scope_type as ScopeType, scope_id as ScopeId, status as Status, pending_document_count as PendingDocumentCount, pending_unit_count as PendingUnitCount, progress_percent as ProgressPercent, affected_scopes_summary as AffectedScopesSummary, reason as Reason, updated_at as UpdatedAt from search_index_status where scope_type = @ScopeType and scope_id = @ScopeId;",
            new { ScopeType = scopeType, ScopeId = scopeId });
        return row?.ToStatus();
    }

    private static string BuildFtsQuery(string query)
    {
        string raw = query.Trim();
        string[] tokens = SearchTextAnalyzer.BuildQueryTokens(raw).Select(QuoteFts).ToArray();
        if (tokens.Length == 0)
        {
            return QuoteFts(raw);
        }

        return string.Join(" OR ", tokens.Distinct(StringComparer.Ordinal));
    }

    private static string QuoteFts(string value)
    {
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string? EncodeCursor(int offset)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(offset.ToString()));
    }

    private static int DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return 0;
        }

        try
        {
            return int.TryParse(Encoding.UTF8.GetString(Convert.FromBase64String(cursor)), out int offset)
                ? Math.Max(0, offset)
                : 0;
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
        public string BoxId { get; set; } = "";
        public string Text { get; set; } = "";
        public string BoxType { get; set; } = "";
        public int Ordinal { get; set; }
        public string TreeRevisionId { get; set; } = "";
        public string ItemId { get; set; } = "";
        public string ItemTitle { get; set; } = "";
        public string DocumentInstanceId { get; set; } = "";
        public string? PageLabel { get; set; }
        public int PageIndex { get; set; }

        public SearchMatchedUnit ToMatchedUnit(bool isMatch)
        {
            return new SearchMatchedUnit(SearchUnitId.Parse(UnitId), Core.Ids.PageId.Parse(PageId),
                DocumentBoxId.Parse(BoxId), Text, BoxType, Ordinal,
                DocumentTreeRevisionId.Parse(TreeRevisionId), isMatch);
        }
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

        public SearchIndexStatus ToStatus()
        {
            return new SearchIndexStatus(ScopeType, ScopeId, Status, PendingDocumentCount, PendingUnitCount,
                ProgressPercent,
                AffectedScopesSummary, Reason, DateTimeOffset.Parse(UpdatedAt));
        }
    }
}
