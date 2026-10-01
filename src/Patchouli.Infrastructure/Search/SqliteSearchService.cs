using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Database;
using Patchouli.Core.Search;
using Patchouli.Infrastructure.LibraryIdentity;

namespace Patchouli.Infrastructure.Search;

public sealed class SqliteSearchService : ISearchService
{
    private const int MatchedUnitsPerPage = 5;
    private const string ExactContainsFunction = "patchouli_exact_contains";
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
            if (_rewriter is not null && !request.DisableQueryRewrite)
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

                LibraryId rewriteLibraryId = LibraryId.Parse(libraryText);
                if (await _rewriter.IsRewriteEnabledAsync(rewriteLibraryId, cancellationToken))
                {
                    Result<SearchRewritePlan> planResult = await _rewriter.BuildRewritePlanAsync(request.Query,
                        new SearchRewriteOptions(rewriteLibraryId, request.ProfileId, null,
                            request.ProfileAlias, request.PreviewRewriteOnly), cancellationToken);
                    if (planResult.IsFailure)
                    {
                        return Result<SearchResultPage>.Failure(planResult.ErrorCode!, planResult.ErrorMessage!);
                    }

                    plan = planResult.Value;
                }

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
            string[] queries = (plan?.ExpandedQueries ?? [request.Query])
                .Select(query => query.Trim())
                .Where(query => query.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string[] ftsQueries = queries
                .Select(BuildFtsQuery)
                .Where(query => query is not null)
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            bool useFtsCandidates = ftsQueries.Length == queries.Length;
            string match = string.Join(" OR ", ftsQueries.Select(query => $"({query})"));
            string candidateSource = useFtsCandidates
                ? "from search_units_fts join fts_row_map fm on fm.fts_row_id = search_units_fts.rowid " +
                  "join search_units su on su.unit_id = fm.unit_id"
                : "from search_units su";
            string candidatePredicate = useFtsCandidates ? "search_units_fts match @Match" : "1 = 1";
            string ftsDocumentScopePredicate = useFtsCandidates && request.DocumentInstanceId is not null
                ? "and search_units_fts.rowid in " +
                  "(select fts_row_id from fts_row_map where document_instance_id = @DocumentInstanceId)"
                : string.Empty;
            string documentScopePredicate = request.DocumentInstanceId is null
                ? string.Empty
                : "and su.document_instance_id = @DocumentInstanceId";
            string exactPredicate = string.Join(" OR ",
                queries.Select((_, index) => $"{ExactContainsFunction}(su.resolved_text, @ExactQuery{index})"));
            connection.CreateFunction<string, string, bool>(ExactContainsFunction,
                static (text, query) => text.Contains(query, StringComparison.OrdinalIgnoreCase),
                true);
            (string filterSql, Dictionary<string, object?> filterParameters) =
                LibraryItemQueryService.BuildFilterSql(request.ItemFilters ?? Array.Empty<BibliographicSearchFilter>(),
                    "ItemFilter");
            DynamicParameters pageParameters = new();
            if (useFtsCandidates)
            {
                pageParameters.Add("Match", match);
            }

            AddExactQueryParameters(pageParameters, queries);
            pageParameters.Add("Status", SearchUnitStatus.Current);
            if (request.DocumentInstanceId is not null)
            {
                pageParameters.Add("DocumentInstanceId", request.DocumentInstanceId.ToString());
            }

            pageParameters.Add("IncludeDeprecated", request.IncludeDeprecatedInstances ? 1 : 0);
            pageParameters.Add("Limit", pageSize + 1);
            pageParameters.Add("Offset", offset);
            foreach (KeyValuePair<string, object?> pair in filterParameters)
            {
                pageParameters.Add(pair.Key, pair.Value);
            }

            PageHitRow[] pageRows = (await connection.QueryAsync<PageHitRow>(
                $"""
                 with matched_pages as (
                     select su.page_id as PageId, min(p.page_index) as PageIndex, count(*) as MatchCount
                     {candidateSource}
                     join pages p on p.page_id = su.page_id
                     join document_instances di on di.document_instance_id = su.document_instance_id
                     join items i on i.item_id = di.item_id
                     where {candidatePredicate}
                       {ftsDocumentScopePredicate}
                       and ({exactPredicate})
                       and su.status = @Status
                       {documentScopePredicate}
                       and (@IncludeDeprecated = 1 or di.status <> 'deprecated')
                       and i.deleted_at is null
                       and i.merged_into_item_id is null
                       {filterSql}
                     group by su.page_id
                 )
                 select PageId, PageIndex, MatchCount
                 from matched_pages
                 order by PageIndex, PageId
                 limit @Limit offset @Offset;
                 """,
                pageParameters)).ToArray();

            PageHitRow[] selectedPages = pageRows.Take(pageSize).ToArray();
            List<SearchPageResult> results = new();
            foreach (PageHitRow page in selectedPages)
            {
                DynamicParameters unitParameters = new();
                if (useFtsCandidates)
                {
                    unitParameters.Add("Match", match);
                }

                AddExactQueryParameters(unitParameters, queries);
                if (request.DocumentInstanceId is not null)
                {
                    unitParameters.Add("DocumentInstanceId", request.DocumentInstanceId.ToString());
                }

                unitParameters.Add("PageId", page.PageId);
                unitParameters.Add("Status", SearchUnitStatus.Current);
                unitParameters.Add("Limit", MatchedUnitsPerPage + 1);
                UnitHitRow[] matchedRows = (await connection.QueryAsync<UnitHitRow>(
                    $"""
                     select su.unit_id as UnitId, su.page_id as PageId, su.box_id as BoxId,
                            su.resolved_text as Text, su.box_type as BoxType,
                            su.ordinal as Ordinal, su.tree_revision_id as TreeRevisionId,
                            i.item_id as ItemId, i.title as ItemTitle, di.document_instance_id as DocumentInstanceId,
                            p.page_label as PageLabel, p.page_index as PageIndex
                     {candidateSource}
                     join pages p on p.page_id = su.page_id
                     join document_instances di on di.document_instance_id = su.document_instance_id
                     join items i on i.item_id = di.item_id
                     where {candidatePredicate}
                       {ftsDocumentScopePredicate}
                       and ({exactPredicate})
                       and su.page_id = @PageId
                       and su.status = @Status
                       {documentScopePredicate}
                       and i.deleted_at is null
                       and i.merged_into_item_id is null
                     order by su.ordinal, su.unit_id
                     limit @Limit;
                     """,
                    unitParameters)).ToArray();
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
            DynamicParameters contextParameters = new();
            contextParameters.Add("UnitId", row.UnitId);
            contextParameters.Add("Status", SearchUnitStatus.Current);
            contextParameters.Add("Before", before);
            contextParameters.Add("After", after);
            UnitHitRow[] contextRows = (await connection.QueryAsync<UnitHitRow>(
                """
                with target as materialized (
                    select unit_id as UnitId, page_id as PageId, box_id as BoxId,
                           resolved_text as Text, box_type as BoxType, ordinal as Ordinal,
                           tree_revision_id as TreeRevisionId
                    from search_units
                    where unit_id = @UnitId and status = @Status
                ),
                before_units as (
                    select su.unit_id as UnitId, su.page_id as PageId, su.box_id as BoxId,
                           su.resolved_text as Text, su.box_type as BoxType, su.ordinal as Ordinal,
                           su.tree_revision_id as TreeRevisionId
                    from target t
                    join search_units su on su.page_id = t.PageId
                                        and su.tree_revision_id = t.TreeRevisionId
                                        and su.status = @Status
                    where (su.ordinal, su.unit_id) < (t.Ordinal, t.UnitId)
                    order by su.ordinal desc, su.unit_id desc
                    limit @Before
                ),
                after_units as (
                    select su.unit_id as UnitId, su.page_id as PageId, su.box_id as BoxId,
                           su.resolved_text as Text, su.box_type as BoxType, su.ordinal as Ordinal,
                           su.tree_revision_id as TreeRevisionId
                    from target t
                    join search_units su on su.page_id = t.PageId
                                        and su.tree_revision_id = t.TreeRevisionId
                                        and su.status = @Status
                    where (su.ordinal, su.unit_id) > (t.Ordinal, t.UnitId)
                    order by su.ordinal, su.unit_id
                    limit @After
                )
                select UnitId, PageId, BoxId, Text, BoxType, Ordinal, TreeRevisionId
                from (
                    select * from before_units
                    union all
                    select * from target
                    union all
                    select * from after_units
                )
                order by Ordinal, UnitId;
                """,
                contextParameters)).ToArray();
            SearchMatchedUnit[] context = contextRows
                .Select(unit => unit.ToMatchedUnit(unit.UnitId == row.UnitId))
                .ToArray();
            return Result<IReadOnlyList<SearchMatchedUnit>>.Success(context);
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

    private static string? BuildFtsQuery(string query)
    {
        string raw = query.Trim();
        string[] tokens = SearchTextAnalyzer.BuildQueryTokens(raw).Select(QuoteFts).ToArray();
        if (tokens.Length == 0)
        {
            return null;
        }

        return string.Join(" AND ", tokens.Distinct(StringComparer.Ordinal));
    }

    private static void AddExactQueryParameters(DynamicParameters parameters, IReadOnlyList<string> queries)
    {
        for (int index = 0; index < queries.Count; index++)
        {
            parameters.Add($"ExactQuery{index}", queries[index]);
        }
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
