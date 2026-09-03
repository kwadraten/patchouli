using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Database;

namespace Patchouli.Infrastructure.LibraryIdentity;

/// <summary>
/// The first-screen library read model. It selects the core Item rows first and then aggregates
/// authors, primary document, page count, latest OCR, SearchUnit count, and source status for
/// only those selected IDs in batch — no per-row correlated subqueries over the whole table and
/// no extra read of every DocumentInstance just to find source paths. Reads use the read-only
/// connection pool.
/// </summary>
public sealed class LibraryItemQueryService : ILibraryItemQueryService
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public LibraryItemQueryService(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    private enum QueryScope
    {
        Active,
        Trash
    }

    public async Task<Result<IReadOnlyList<LibraryItemRow>>> ListRowsAsync(
        IReadOnlyList<string>? requiredTags = null,
        CancellationToken cancellationToken = default)
    {
        (Result<IReadOnlyList<LibraryItemRow>> result, _) =
            await QueryPageAsync(null, null, QueryScope.Active, cancellationToken, requiredTags: requiredTags);
        return result;
    }

    public async Task<Result<LibraryItemPage>> ListRowsAsync(
        int limit,
        LibraryItemCursor? after,
        IReadOnlyList<string>? requiredTags = null,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return Result<LibraryItemPage>.Failure(AppErrorCodes.ValidationFailed,
                "Page limit must be a positive integer.");
        }

        (Result<IReadOnlyList<LibraryItemRow>> result, bool hasMore) =
            await QueryPageAsync(limit, after, QueryScope.Active, cancellationToken, requiredTags: requiredTags);
        if (result.IsFailure)
        {
            return Result<LibraryItemPage>.Failure(result.ErrorCode!, result.ErrorMessage!);
        }

        LibraryItemRow[] rows = result.Value.ToArray();
        LibraryItemCursor? nextCursor = rows.Length == 0
            ? null
            : new LibraryItemCursor(rows[^1].ItemId, rows[^1].CreatedAt);
        return Result<LibraryItemPage>.Success(new LibraryItemPage(rows, nextCursor, hasMore));
    }

    public async Task<Result<IReadOnlyList<LibraryItemRow>>> GetRowsByIdsAsync(
        IReadOnlyCollection<ItemId> itemIds,
        CancellationToken cancellationToken = default)
    {
        if (itemIds.Count == 0)
        {
            return Result<IReadOnlyList<LibraryItemRow>>.Success([]);
        }

        (Result<IReadOnlyList<LibraryItemRow>> result, _) =
            await QueryPageAsync(null, null, QueryScope.Active, cancellationToken, itemIds, null);
        return result;
    }

    public async Task<Result<IReadOnlyList<LibraryItemRow>>> ListTrashedRowsAsync(
        CancellationToken cancellationToken = default)
    {
        (Result<IReadOnlyList<LibraryItemRow>> result, _) =
            await QueryPageAsync(null, null, QueryScope.Trash, cancellationToken);
        return result;
    }

    public async Task<Result<LibraryItemPage>> ListTrashedRowsAsync(
        int limit,
        LibraryItemCursor? after,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return Result<LibraryItemPage>.Failure(AppErrorCodes.ValidationFailed,
                "Page limit must be a positive integer.");
        }

        (Result<IReadOnlyList<LibraryItemRow>> result, bool hasMore) =
            await QueryPageAsync(limit, after, QueryScope.Trash, cancellationToken);
        if (result.IsFailure)
        {
            return Result<LibraryItemPage>.Failure(result.ErrorCode!, result.ErrorMessage!);
        }

        LibraryItemRow[] rows = result.Value.ToArray();
        LibraryItemCursor? nextCursor = rows.Length == 0
            ? null
            : new LibraryItemCursor(rows[^1].ItemId, rows[^1].CreatedAt);
        return Result<LibraryItemPage>.Success(new LibraryItemPage(rows, nextCursor, hasMore));
    }

    public async Task<Result<int>> CountUntaggedItemsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);
            int count = await connection.ExecuteScalarAsync<int>(
                """
                select count(1)
                from items i
                where i.deleted_at is null
                  and i.merged_into_item_id is null
                  and (i.tags_json is null
                       or not json_valid(i.tags_json)
                       or json_array_length(i.tags_json) = 0);
                """);
            return Result<int>.Success(count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.library-item-query"))
        {
            return Result<int>.Failure(AppErrorCodes.DatabaseError,
                $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<ItemId>>> GetItemIdsByDocumentInstanceIdsAsync(
        IReadOnlyCollection<DocumentInstanceId> documentInstanceIds,
        CancellationToken cancellationToken = default)
    {
        if (documentInstanceIds.Count == 0)
        {
            return Result<IReadOnlyList<ItemId>>.Success([]);
        }

        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);
            IEnumerable<string> ids = await connection.QueryAsync<string>(
                "select item_id from document_instances where document_instance_id in @Ids;",
                new { Ids = documentInstanceIds.Select(static id => id.ToString()).Distinct().ToArray() });
            return Result<IReadOnlyList<ItemId>>.Success(ids.Select(ItemId.Parse).ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.library-item-query"))
        {
            return Result<IReadOnlyList<ItemId>>.Failure(AppErrorCodes.DatabaseError,
                $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result<DocumentNavigationRow?>> GetDocumentNavigationAsync(
        DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);
            DocumentNavigationDbRow? row = await connection.QuerySingleOrDefaultAsync<DocumentNavigationDbRow>(
                """
                select di.item_id as ItemId,
                       di.document_instance_id as DocumentInstanceId,
                       di.file_asset_id as FileAssetId,
                       coalesce(fa.file_name, '') as FileName,
                       coalesce(fa.original_path, '') as SourcePath,
                       (select count(1) from pages p where p.document_instance_id = di.document_instance_id) as PageCount,
                       (select count(1) from search_units su
                        where su.document_instance_id = di.document_instance_id and su.status = 'current') as SearchUnitCount,
                       coalesce((select sis.status from search_index_status sis
                                 where sis.scope_type = 'document_instance'
                                   and sis.scope_id = di.document_instance_id), 'not_indexed') as IndexStatus
                from document_instances di
                left join file_assets fa on fa.file_asset_id = di.file_asset_id
                where di.document_instance_id = @DocumentInstanceId;
                """,
                new { DocumentInstanceId = documentInstanceId.ToString() });
            return Result<DocumentNavigationRow?>.Success(row is null
                ? null
                : new DocumentNavigationRow(
                    ItemId.Parse(row.ItemId),
                    DocumentInstanceId.Parse(row.DocumentInstanceId),
                    NullIfWhiteSpace(row.FileAssetId),
                    row.FileName,
                    row.SourcePath,
                    row.PageCount,
                    row.SearchUnitCount,
                    row.IndexStatus));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.library-item-query"))
        {
            return Result<DocumentNavigationRow?>.Failure(AppErrorCodes.DatabaseError,
                $"Database operation failed: {exception.Message}");
        }
    }

    private async Task<(Result<IReadOnlyList<LibraryItemRow>> Result, bool HasMore)> QueryPageAsync(
        int? limit,
        LibraryItemCursor? after,
        QueryScope scope,
        CancellationToken cancellationToken,
        IReadOnlyCollection<ItemId>? itemIds = null,
        IReadOnlyList<string>? requiredTags = null)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);

            bool probeHasMore = limit is not null;
            int take = limit is null ? int.MaxValue : limit.Value + 1;
            string itemIdFilter = itemIds is null
                ? ""
                : "and i.item_id in @ItemIds ";
            string lifecycleClause = scope == QueryScope.Trash
                ? "and i.deleted_at is not null and i.merged_into_item_id is null"
                : "and i.deleted_at is null and i.merged_into_item_id is null";
            string tagFilter = BuildTagFilter(requiredTags);
            IEnumerable<CoreRow> rows = await connection.QueryAsync<CoreRow>(
                string.Format(CultureInfo.InvariantCulture, CoreRowSqlTemplate, itemIdFilter, lifecycleClause,
                    tagFilter),
                new
                {
                    Limit = take,
                    AfterCreatedAt = after?.CreatedAt,
                    AfterItemId = after?.ItemId.ToString(),
                    ItemIds = itemIds?.Select(static id => id.ToString()).ToArray(),
                    RequiredTags = requiredTags?.ToArray(),
                    RequiredTagCount = requiredTags?.Count ?? 0
                });

            CoreRow[] coreRows = rows.ToArray();
            bool hasMore = probeHasMore && coreRows.Length > limit;
            if (probeHasMore && coreRows.Length > limit)
            {
                coreRows = coreRows.Take(limit.Value).ToArray();
            }

            if (coreRows.Length == 0)
            {
                return (Result<IReadOnlyList<LibraryItemRow>>.Success([]), false);
            }

            string[] itemIdTexts = coreRows.Select(static row => row.ItemId).ToArray();
            string[] docIdTexts = coreRows
                .Select(static row => row.DocumentInstanceId)
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Select(static id => id!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            Dictionary<string, string> authorsByItem = (await connection.QueryAsync<AuthorRow>(
                    AuthorRowSql, new { ItemIds = itemIdTexts }))
                .ToDictionary(static row => row.ItemId, static row => row.Authors, StringComparer.Ordinal);
            Dictionary<string, string> issuedDates = (await connection.QueryAsync<IssuedDateRow>(
                    IssuedDateRowSql, new { ItemIds = itemIdTexts }))
                .GroupBy(static row => row.ItemId, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.First().Literal, StringComparer.Ordinal);

            Dictionary<string, int> pageCounts = ToCountMap(await connection.QueryAsync<DocCountRow>(
                PageCountSql, new { DocIds = docIdTexts }));
            Dictionary<string, int> searchUnitCounts = ToCountMap(await connection.QueryAsync<DocCountRow>(
                SearchUnitCountSql, new { DocIds = docIdTexts }));
            HashSet<string> currentLayouts = (await connection.QueryAsync<string>(CurrentLayoutSql,
                new { DocIds = docIdTexts })).ToHashSet(StringComparer.Ordinal);
            HashSet<string> documentsWithOcrText = (await connection.QueryAsync<string>(OcrTextDocumentSql,
                new { DocIds = docIdTexts })).ToHashSet(StringComparer.Ordinal);
            Dictionary<string, string> indexStatusByDoc = (await connection.QueryAsync<IndexStatusRow>(
                    IndexStatusSql, new { DocIds = docIdTexts }))
                .ToDictionary(static row => row.DocumentInstanceId, static row => row.IndexStatus,
                    StringComparer.Ordinal);
            Dictionary<string, string> latestOcrStateByDoc = (await connection.QueryAsync<LatestOcrStateRow>(
                    LatestOcrStateSql, new { DocIds = docIdTexts }))
                .ToDictionary(static row => row.DocumentInstanceId, static row => row.State,
                    StringComparer.Ordinal);
            Dictionary<string, string> latestOcrErrorByDoc = (await connection.QueryAsync<LatestOcrErrorRow>(
                    LatestOcrErrorSql, new { DocIds = docIdTexts }))
                .GroupBy(static row => row.DocumentInstanceId, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.First().ErrorMessage,
                    StringComparer.Ordinal);

            List<LibraryItemRow> models = new(coreRows.Length);
            foreach (CoreRow row in coreRows)
            {
                int pageCount = row.DocumentInstanceId is not null &&
                                pageCounts.TryGetValue(row.DocumentInstanceId, out int pages)
                    ? pages
                    : 0;
                int searchUnitCount = row.DocumentInstanceId is not null &&
                                      searchUnitCounts.TryGetValue(row.DocumentInstanceId, out int units)
                    ? units
                    : 0;
                string indexStatus = row.DocumentInstanceId is not null &&
                                     indexStatusByDoc.TryGetValue(row.DocumentInstanceId, out string? status)
                    ? status
                    : "not_indexed";
                string? latestState = row.DocumentInstanceId is not null &&
                                      latestOcrStateByDoc.TryGetValue(row.DocumentInstanceId, out string? state)
                    ? state
                    : null;
                string? latestError = row.DocumentInstanceId is not null &&
                                      latestOcrErrorByDoc.TryGetValue(row.DocumentInstanceId, out string? error)
                    ? error
                    : null;

                authorsByItem.TryGetValue(row.ItemId, out string? authors);
                issuedDates.TryGetValue(row.ItemId, out string? issuedLiteral);

                models.Add(new LibraryItemRow(
                    ItemId.Parse(row.ItemId),
                    row.Title,
                    row.ItemType,
                    string.IsNullOrWhiteSpace(authors) ? FormatCreators(row.CreatorsJson) : authors,
                    string.IsNullOrWhiteSpace(issuedLiteral) ? NullIfWhiteSpace(row.Date) : issuedLiteral,
                    NullIfWhiteSpace(row.PublicationTitle),
                    NullIfWhiteSpace(row.Publisher),
                    string.IsNullOrWhiteSpace(row.DocumentInstanceId)
                        ? null
                        : DocumentInstanceId.Parse(row.DocumentInstanceId),
                    NullIfWhiteSpace(row.LinkedFileName),
                    NullIfWhiteSpace(row.FileAssetId),
                    row.SourcePath,
                    row.CreatedAt,
                    pageCount,
                    searchUnitCount,
                    row.DocumentInstanceId is not null && documentsWithOcrText.Contains(row.DocumentInstanceId),
                    PrimaryDocumentOcrIndexState.Resolve(row.DocumentInstanceId is not null, latestState, latestError,
                        row.DocumentInstanceId is not null && currentLayouts.Contains(row.DocumentInstanceId),
                        string.Equals(indexStatus, "current", StringComparison.Ordinal)),
                    indexStatus,
                    NullIfWhiteSpace(row.DeletedAt),
                    NullIfWhiteSpace(row.MergedIntoItemId),
                    ParseTags(row.TagsJson)));
            }

            return (Result<IReadOnlyList<LibraryItemRow>>.Success(models), hasMore);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.library-item-query"))
        {
            return (Result<IReadOnlyList<LibraryItemRow>>.Failure(AppErrorCodes.DatabaseError,
                $"Database operation failed: {exception.Message}"), false);
        }
    }

    private const string CoreRowSqlTemplate =
        """
        select
            i.item_id as ItemId,
            i.title as Title,
            i.item_type as ItemType,
            i.creators_json as CreatorsJson,
            i.date as Date,
            i.publication_title as PublicationTitle,
            i.publisher as Publisher,
            i.created_at as CreatedAt,
            i.deleted_at as DeletedAt,
            i.merged_into_item_id as MergedIntoItemId,
            i.tags_json as TagsJson,
            di.document_instance_id as DocumentInstanceId,
            fa.file_name as LinkedFileName,
            fa.file_asset_id as FileAssetId,
            coalesce(fa.original_path, '') as SourcePath
        from items i
        left join document_instances di on di.item_id = i.item_id and di.is_primary = 1
        left join file_assets fa on fa.file_asset_id = di.file_asset_id
        where 1 = 1
          {1}
          and (@AfterCreatedAt is null or i.created_at < @AfterCreatedAt
               or (i.created_at = @AfterCreatedAt and i.item_id < @AfterItemId))
          {0}
          {2}
        order by i.created_at desc, i.item_id desc
        limit @Limit;
        """;

    private const string AuthorRowSql =
        """
        select
            c.item_id as ItemId,
            group_concat(
                case
                    when length(trim(coalesce(c.literal, ''))) > 0 then c.literal
                    else trim(coalesce(c.given, '') || ' ' || coalesce(c.particles, '') || ' ' ||
                              coalesce(c.family, '') || ' ' || coalesce(c.suffix, ''))
                end,
                ', '
            ) as Authors
        from item_creators c
        where c.item_id in @ItemIds and c.role = 'author'
        group by c.item_id
        order by c.item_id;
        """;

    private const string IssuedDateRowSql =
        """
        select item_id as ItemId, literal as Literal
        from item_dates
        where item_id in @ItemIds and role = 'issued';
        """;

    private const string PageCountSql =
        """
        select document_instance_id as DocumentInstanceId, count(1) as Value
        from pages
        where document_instance_id in @DocIds
        group by document_instance_id;
        """;

    private const string SearchUnitCountSql =
        """
        select document_instance_id as DocumentInstanceId, count(1) as Value
        from search_units
        where document_instance_id in @DocIds and status = 'current'
        group by document_instance_id;
        """;

    private const string IndexStatusSql =
        """
        select scope_id as DocumentInstanceId, status as IndexStatus
        from search_index_status
        where scope_type = 'document_instance' and scope_id in @DocIds;
        """;

    private const string CurrentLayoutSql =
        """
        select document_instance_id
        from document_tree_revisions
        where document_instance_id in @DocIds and status = 'committed' and is_current = 1;
        """;

    private const string OcrTextDocumentSql =
        """
        select distinct r.document_instance_id
        from document_tree_revisions r
        join document_boxes b on b.tree_revision_id = r.tree_revision_id
        where r.document_instance_id in @DocIds
          and r.status = 'committed'
          and r.is_current = 1
          and b.suppressed = 0
          and b.payload_json is not null
          and json_valid(b.payload_json) = 1
          and (
              length(trim(coalesce(json_extract(b.payload_json, '$.markdown'), ''))) > 0
              or length(trim(coalesce(json_extract(b.payload_json, '$.latex'), ''))) > 0
              or length(trim(coalesce(json_extract(b.payload_json, '$.code'), ''))) > 0
              or length(trim(coalesce(json_extract(b.payload_json, '$.description'), ''))) > 0
          );
        """;

    private const string LatestOcrStateSql =
        """
        select document_instance_id as DocumentInstanceId, state as State
        from (
            select r.document_instance_id, r.state,
                   row_number() over (
                       partition by r.document_instance_id
                       order by r.created_at desc, r.ocr_run_id desc) as rn
            from ocr_runs r
            where r.document_instance_id in @DocIds and r.hidden = 0
        )
        where rn = 1;
        """;

    private const string LatestOcrErrorSql =
        """
        select document_instance_id as DocumentInstanceId, error_message as ErrorMessage
        from (
            select r.document_instance_id, pr.error_message,
                   row_number() over (
                       partition by r.document_instance_id
                       order by r.created_at desc, r.ocr_run_id desc, pr.created_at desc) as rn
            from ocr_runs r
            join ocr_page_results pr on pr.ocr_run_id = r.ocr_run_id
            where r.document_instance_id in @DocIds and r.hidden = 0
              and length(trim(coalesce(pr.error_message, ''))) > 0
        )
        where rn = 1;
        """;

    private static Dictionary<string, int> ToCountMap(IEnumerable<DocCountRow> rows)
    {
        return rows.ToDictionary(static row => row.DocumentInstanceId, static row => row.Value,
            StringComparer.Ordinal);
    }

    private static string BuildTagFilter(IReadOnlyList<string>? requiredTags)
    {
        if (requiredTags is null || requiredTags.Count == 0)
        {
            return "";
        }

        return """
               and (
                   select count(distinct value)
                   from json_each(i.tags_json)
                   where value in @RequiredTags
               ) = @RequiredTagCount
               """;
    }

    private static IReadOnlyList<string> ParseTags(string? tagsJson)
    {
        if (string.IsNullOrWhiteSpace(tagsJson))
        {
            return Array.Empty<string>();
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(tagsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            return document.RootElement.EnumerateArray()
                .Select(element => element.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string FormatCreators(string creatorsJson)
    {
        try
        {
            using JsonDocument document =
                JsonDocument.Parse(string.IsNullOrWhiteSpace(creatorsJson) ? "[]" : creatorsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            return string.Join(", ", document.RootElement.EnumerateArray()
                .Select(element =>
                    element.TryGetProperty("name", out JsonElement name) ? name.GetString() :
                    element.TryGetProperty("Name", out JsonElement upperName) ? upperName.GetString() :
                    null)
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        }
        catch
        {
            return string.Empty;
        }
    }

    private sealed class CoreRow
    {
        public string ItemId { get; init; } = "";
        public string Title { get; init; } = "";
        public string ItemType { get; init; } = "";
        public string CreatorsJson { get; init; } = "[]";
        public string? Date { get; init; }
        public string? PublicationTitle { get; init; }
        public string? Publisher { get; init; }
        public string CreatedAt { get; init; } = "";
        public string? DeletedAt { get; init; }
        public string? MergedIntoItemId { get; init; }
        public string TagsJson { get; init; } = "[]";
        public string? DocumentInstanceId { get; init; }
        public string? LinkedFileName { get; init; }
        public string? FileAssetId { get; init; }
        public string SourcePath { get; init; } = "";
    }

    private sealed class AuthorRow
    {
        public string ItemId { get; init; } = "";
        public string Authors { get; init; } = "";
    }

    private sealed class IssuedDateRow
    {
        public string ItemId { get; init; } = "";
        public string Literal { get; init; } = "";
    }

    private sealed class DocCountRow
    {
        public string DocumentInstanceId { get; init; } = "";
        public int Value { get; init; }
    }

    private sealed class IndexStatusRow
    {
        public string DocumentInstanceId { get; init; } = "";
        public string IndexStatus { get; init; } = "";
    }

    private sealed class LatestOcrStateRow
    {
        public string DocumentInstanceId { get; init; } = "";
        public string State { get; init; } = "";
    }

    private sealed class LatestOcrErrorRow
    {
        public string DocumentInstanceId { get; init; } = "";
        public string ErrorMessage { get; init; } = "";
    }

    private sealed class DocumentNavigationDbRow
    {
        public string ItemId { get; init; } = "";
        public string DocumentInstanceId { get; init; } = "";
        public string? FileAssetId { get; init; }
        public string FileName { get; init; } = "";
        public string SourcePath { get; init; } = "";
        public int PageCount { get; init; }
        public int SearchUnitCount { get; init; }
        public string IndexStatus { get; init; } = "";
    }
}
