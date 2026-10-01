using System.Data.Common;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Search;
using Patchouli.Ocr;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Search;
using Patchouli.Infrastructure.Snapshots;

namespace Patchouli.Infrastructure.Bibliography;

public sealed class ItemPurgeService : IItemPurgeService
{
    private const string PurgeReason = "user_purge";
    private const int ReportQueryBatchSize = 400;

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly ILibraryIdentityService _libraryIdentityService;
    private readonly ISnapshotSyncBindingStore? _snapshotBindings;
    private readonly ILibraryRevisionService? _revisions;

    public ItemPurgeService(
        SqliteConnectionFactory connectionFactory,
        IClock clock,
        ILibraryIdentityService libraryIdentityService,
        ISnapshotSyncBindingStore? snapshotBindings = null,
        ILibraryRevisionService? revisions = null)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
        _libraryIdentityService = libraryIdentityService;
        _snapshotBindings = snapshotBindings;
        _revisions = revisions;
    }

    public async Task<Result<ItemPurgeDependencyReport>> BuildPurgeReportAsync(
        ItemId itemId,
        CancellationToken cancellationToken = default)
    {
        Result<IReadOnlyList<ItemPurgeDependencyReport>> reports =
            await BuildPurgeReportsAsync([itemId], cancellationToken);
        return reports.IsSuccess
            ? Result<ItemPurgeDependencyReport>.Success(reports.Value[0])
            : Result<ItemPurgeDependencyReport>.Failure(
                reports.ErrorCode!, reports.ErrorMessage!, reports.Conflicts, reports.Details);
    }

    public async Task<Result<IReadOnlyList<ItemPurgeDependencyReport>>> BuildPurgeReportsAsync(
        IReadOnlyList<ItemId> itemIds,
        CancellationToken cancellationToken = default)
    {
        if (itemIds.Count == 0)
        {
            return Result<IReadOnlyList<ItemPurgeDependencyReport>>.Success([]);
        }

        Result<LibraryMetadata> libraryResult = await _libraryIdentityService.GetCurrentLibraryAsync(cancellationToken);
        if (libraryResult.IsFailure)
        {
            return Result<IReadOnlyList<ItemPurgeDependencyReport>>.Failure(
                libraryResult.ErrorCode!, libraryResult.ErrorMessage!);
        }

        ItemId[] distinctIds = itemIds.Distinct().ToArray();
        string[] itemIdStrings = distinctIds.Select(static id => id.ToString()).ToArray();
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);

            int trashCount = 0;
            List<DocumentOwnerRow> documentRows = [];
            List<PurgeFileAssetRow> fileAssetRows = [];
            foreach (string[] itemBatch in itemIdStrings.Chunk(ReportQueryBatchSize))
            {
                trashCount += await connection.ExecuteScalarAsync<int>(
                    """
                    select count(1)
                    from items
                    where item_id in @ItemIds
                      and deleted_at is not null
                      and merged_into_item_id is null;
                    """,
                    new { ItemIds = itemBatch });
                documentRows.AddRange(await connection.QueryAsync<DocumentOwnerRow>(
                    "select item_id as ItemId, document_instance_id as DocumentInstanceId from document_instances where item_id in @ItemIds;",
                    new { ItemIds = itemBatch }));
                fileAssetRows.AddRange(await connection.QueryAsync<PurgeFileAssetRow>(
                    """
                    select d.item_id as ItemId, d.file_asset_id as FileAssetId
                    from document_instances d
                    where d.item_id in @ItemIds and d.file_asset_id is not null
                    union
                    select d.item_id as ItemId, refs.file_asset_id as FileAssetId
                    from file_asset_payload_refs refs
                    join document_tree_revisions revisions
                      on revisions.tree_revision_id = refs.tree_revision_id
                    join document_instances d
                      on d.document_instance_id = revisions.document_instance_id
                    where d.item_id in @ItemIds;
                    """,
                    new { ItemIds = itemBatch }));
            }

            if (trashCount != distinctIds.Length)
            {
                return Result<IReadOnlyList<ItemPurgeDependencyReport>>.Failure(AppErrorCodes.NotFound,
                    "One or more items were not found in trash.");
            }

            Dictionary<string, string[]> documentIdsByItem = documentRows
                .GroupBy(static row => row.ItemId, StringComparer.Ordinal)
                .ToDictionary(
                    static group => group.Key,
                    static group => group.Select(static row => row.DocumentInstanceId).ToArray(),
                    StringComparer.Ordinal);
            Dictionary<string, HashSet<FileAssetId>> fileAssetIdsByItem = new(StringComparer.Ordinal);
            foreach (PurgeFileAssetRow row in fileAssetRows)
            {
                // Derived payload references may contain legacy, non-asset strings.
                if (!Guid.TryParse(row.FileAssetId, out _))
                {
                    continue;
                }

                if (!fileAssetIdsByItem.TryGetValue(row.ItemId, out HashSet<FileAssetId>? ids))
                {
                    ids = [];
                    fileAssetIdsByItem[row.ItemId] = ids;
                }

                ids.Add(FileAssetId.Parse(row.FileAssetId));
            }

            string[] allDocumentIds = documentRows
                .Select(static row => row.DocumentInstanceId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            HashSet<string> activeOcrDocuments = new(StringComparer.Ordinal);
            HashSet<string> candidateOcrDocuments = new(StringComparer.Ordinal);
            HashSet<string> workingDocuments = new(StringComparer.Ordinal);
            foreach (string[] documentBatch in allDocumentIds.Chunk(ReportQueryBatchSize))
            {
                activeOcrDocuments.UnionWith(await connection.QueryAsync<string>(
                    "select distinct document_instance_id from ocr_runs where document_instance_id in @DocumentIds and state in (@Pending, @Running);",
                    new
                    {
                        DocumentIds = documentBatch,
                        Pending = OcrRunState.Pending,
                        Running = OcrRunState.Running
                    }));
                candidateOcrDocuments.UnionWith(await connection.QueryAsync<string>(
                    """
                    select distinct o.document_instance_id
                    from ocr_page_results r
                    join ocr_runs o on o.ocr_run_id = r.ocr_run_id
                    where o.document_instance_id in @DocumentIds
                      and r.working_tree_revision_id is not null;
                    """,
                    new { DocumentIds = documentBatch }));
                workingDocuments.UnionWith(await connection.QueryAsync<string>(
                    "select distinct document_instance_id from document_tree_revisions where document_instance_id in @DocumentIds and status = @Working;",
                    new { DocumentIds = documentBatch, Working = "working" }));
            }

            IReadOnlyDictionary<string, IReadOnlyList<string>> shardIdsByItem =
                await FindSnapshotShardIdsAsync(itemIdStrings, cancellationToken);
            Dictionary<string, ItemPurgeDependencyReport> reportsByItem = new(StringComparer.Ordinal);
            foreach (ItemId itemId in distinctIds)
            {
                string itemIdString = itemId.ToString();
                string[] documentIds = documentIdsByItem.GetValueOrDefault(itemIdString) ?? [];
                IReadOnlyList<string> snapshotShardIds =
                    shardIdsByItem.GetValueOrDefault(itemIdString) ?? Array.Empty<string>();
                HashSet<FileAssetId> assetIds = fileAssetIdsByItem.GetValueOrDefault(itemIdString) ?? [];
                reportsByItem[itemIdString] = new ItemPurgeDependencyReport(
                    itemId,
                    snapshotShardIds,
                    snapshotShardIds.Count,
                    documentIds.Any(activeOcrDocuments.Contains),
                    documentIds.Any(candidateOcrDocuments.Contains),
                    documentIds.Any(workingDocuments.Contains))
                {
                    FileAssetIds = assetIds.OrderBy(static id => id.ToString(), StringComparer.Ordinal).ToArray()
                };
            }

            return Result<IReadOnlyList<ItemPurgeDependencyReport>>.Success(
                itemIds.Select(itemId => reportsByItem[itemId.ToString()]).ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.item-purge"))
        {
            return Result<IReadOnlyList<ItemPurgeDependencyReport>>.Failure(
                AppErrorCodes.DatabaseError,
                $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result> PurgeItemsAsync(
        IReadOnlyList<ItemId> itemIds,
        CancellationToken cancellationToken = default)
    {
        if (itemIds.Count == 0)
        {
            return Result.Success();
        }

        Result<LibraryMetadata> libraryResult = await _libraryIdentityService.GetCurrentLibraryAsync(cancellationToken);
        if (libraryResult.IsFailure)
        {
            return Result.Failure(libraryResult.ErrorCode!, libraryResult.ErrorMessage!);
        }

        ItemId[] distinctIds = itemIds.Distinct().ToArray();
        string[] itemIdStrings = distinctIds.Select(id => id.ToString()).ToArray();

        try
        {
            using IDisposable writeLease = await _connectionFactory.EnterWriteAsync(cancellationToken);
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            int trashCount = await connection.ExecuteScalarAsync<int>(
                """
                select count(1)
                from items
                where item_id in @ItemIds
                  and deleted_at is not null
                  and merged_into_item_id is null;
                """,
                new { ItemIds = itemIdStrings },
                transaction);
            if (trashCount != itemIdStrings.Length)
            {
                return Result.Failure(AppErrorCodes.NotFound, "One or more items were not found in trash.");
            }

            Dictionary<string, int> documentCountByItem = (await connection.QueryAsync<(string ItemId, int Count)>(
                    """
                    select item_id as ItemId, count(1) as Count
                    from document_instances
                    where item_id in @ItemIds
                    group by item_id;
                    """,
                    new { ItemIds = itemIdStrings },
                    transaction))
                .ToDictionary(row => row.ItemId, row => row.Count, StringComparer.Ordinal);

            string[] documentIds = (await connection.QueryAsync<string>(
                "select document_instance_id from document_instances where item_id in @ItemIds;",
                new { ItemIds = itemIdStrings },
                transaction)).ToArray();

            string[] pageIds = documentIds.Length == 0
                ? []
                : (await connection.QueryAsync<string>(
                    "select page_id from pages where document_instance_id in @DocumentIds;",
                    new { DocumentIds = documentIds },
                    transaction)).ToArray();

            int activeOcrCount = documentIds.Length == 0
                ? 0
                : await connection.ExecuteScalarAsync<int>(
                    """
                    select count(1)
                    from ocr_runs
                    where document_instance_id in @DocumentIds
                      and state in (@Pending, @Running);
                    """,
                    new
                    {
                        DocumentIds = documentIds,
                        Pending = OcrRunState.Pending,
                        Running = OcrRunState.Running
                    },
                    transaction);
            if (activeOcrCount > 0)
            {
                return Result.Failure(
                    AppErrorCodes.InvalidState,
                    "Cannot purge items while OCR runs are pending or running.");
            }

            if (documentIds.Length > 0)
            {
                await SearchFtsCacheWriter.EnsureReadyAsync(connection, transaction, cancellationToken);

                await connection.ExecuteAsync(
                    """
                    delete from ocr_candidate_adoptions
                    where document_instance_id in @DocumentIds
                       or ocr_run_id in (
                           select ocr_run_id from ocr_runs where document_instance_id in @DocumentIds);
                    """,
                    new { DocumentIds = documentIds },
                    transaction);

                await connection.ExecuteAsync(
                    """
                    delete from ocr_page_results
                    where ocr_run_id in (
                        select ocr_run_id from ocr_runs where document_instance_id in @DocumentIds);
                    """,
                    new { DocumentIds = documentIds },
                    transaction);

                await connection.ExecuteAsync(
                    "delete from ocr_runs where document_instance_id in @DocumentIds;",
                    new { DocumentIds = documentIds },
                    transaction);

                await SearchFtsCacheWriter.DeleteForDocumentInstancesAsync(
                    connection, transaction, documentIds, cancellationToken);

                await connection.ExecuteAsync(
                    "delete from search_index_status where (scope_type = @DocumentScope and scope_id in @DocumentIds) or (scope_type = @PageScope and scope_id in @PageIds);",
                    new
                    {
                        DocumentScope = SearchIndexScopeType.DocumentInstance,
                        PageScope = SearchIndexScopeType.Page,
                        DocumentIds = documentIds,
                        PageIds = pageIds
                    },
                    transaction);

                await connection.ExecuteAsync(
                    "delete from search_units where document_instance_id in @DocumentIds;",
                    new { DocumentIds = documentIds },
                    transaction);

                await connection.ExecuteAsync(
                    "delete from document_commit_pages where commit_id in (select commit_id from document_commits where document_instance_id in @DocumentIds) or page_id in @PageIds;",
                    new { DocumentIds = documentIds, PageIds = pageIds },
                    transaction);

                await connection.ExecuteAsync(
                    "delete from document_commits where document_instance_id in @DocumentIds;",
                    new { DocumentIds = documentIds },
                    transaction);

                await connection.ExecuteAsync(
                    "delete from translation_boxes where page_id in @PageIds;",
                    new { PageIds = pageIds },
                    transaction);

                await connection.ExecuteAsync(
                    "delete from page_translations where page_id in @PageIds;",
                    new { PageIds = pageIds },
                    transaction);

                await connection.ExecuteAsync(
                    "delete from document_boxes where document_instance_id in @DocumentIds;",
                    new { DocumentIds = documentIds },
                    transaction);

                await connection.ExecuteAsync(
                    "delete from document_tree_revisions where document_instance_id in @DocumentIds;",
                    new { DocumentIds = documentIds },
                    transaction);

                await connection.ExecuteAsync(
                    "delete from pages where document_instance_id in @DocumentIds;",
                    new { DocumentIds = documentIds },
                    transaction);
            }

            await connection.ExecuteAsync(
                "delete from document_instances where item_id in @ItemIds;",
                new { ItemIds = itemIdStrings },
                transaction);

            await connection.ExecuteAsync(
                "delete from item_identifiers where item_id in @ItemIds;",
                new { ItemIds = itemIdStrings },
                transaction);

            await connection.ExecuteAsync(
                "delete from item_creators where item_id in @ItemIds;",
                new { ItemIds = itemIdStrings },
                transaction);

            await connection.ExecuteAsync(
                "delete from item_dates where item_id in @ItemIds;",
                new { ItemIds = itemIdStrings },
                transaction);

            await connection.ExecuteAsync(
                "delete from item_type_inferences where item_id in @ItemIds;",
                new { ItemIds = itemIdStrings },
                transaction);

            CollectionId[] affectedCollections = (await connection.QueryAsync<string>(
                    "select distinct collection_id from item_collections where item_id in @ItemIds;",
                    new { ItemIds = itemIdStrings }, transaction))
                .Select(CollectionId.Parse)
                .ToArray();
            await connection.ExecuteAsync(
                "delete from item_collections where item_id in @ItemIds;",
                new { ItemIds = itemIdStrings },
                transaction);

            await connection.ExecuteAsync(
                "delete from items where item_id in @ItemIds;",
                new { ItemIds = itemIdStrings },
                transaction);

            string now = FormatUtc(_clock.UtcNow);
            foreach (string itemId in itemIdStrings)
            {
                int documentCount = documentCountByItem.GetValueOrDefault(itemId);
                await connection.ExecuteAsync(
                    """
                    insert into item_purge_records (item_id, purged_at, purge_reason, payload_summary_json)
                    values (@ItemId, @PurgedAt, @PurgeReason, @PayloadSummary);
                    """,
                    new
                    {
                        ItemId = itemId,
                        PurgedAt = now,
                        PurgeReason = PurgeReason,
                        PayloadSummary = JsonSerializer.Serialize(new { document_instance_count = documentCount })
                    },
                    transaction);
            }

            Result<LibraryChangeSet?> revision = await IncrementRevisionAsync(
                connection,
                transaction,
                LibraryChangeSet.Empty with { ItemIds = distinctIds, CollectionIds = affectedCollections },
                cancellationToken);
            if (revision.IsFailure)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result.Failure(revision.ErrorCode!, revision.ErrorMessage!);
            }

            await transaction.CommitAsync(cancellationToken);
            PublishRevision(revision.Value);

            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.item-purge"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> FindSnapshotShardIdsAsync(
        IReadOnlyList<string> itemIds,
        CancellationToken cancellationToken)
    {
        string[] distinctItemIds = itemIds.Distinct(StringComparer.Ordinal).ToArray();
        Dictionary<string, HashSet<string>> shardIdsByItem = distinctItemIds
            .ToDictionary(static id => id, static _ => new HashSet<string>(StringComparer.Ordinal),
                StringComparer.Ordinal);
        if (_snapshotBindings is null)
        {
            return ToSnapshotShardMap(shardIdsByItem);
        }

        try
        {
            Result<SnapshotSyncBinding> bindingResult = await _snapshotBindings.GetBindingAsync(cancellationToken);
            if (bindingResult.IsFailure)
            {
                return ToSnapshotShardMap(shardIdsByItem);
            }

            SnapshotSyncBinding binding = bindingResult.Value;
            if (string.IsNullOrWhiteSpace(binding.SyncRoot) || !Directory.Exists(binding.SyncRoot))
            {
                return ToSnapshotShardMap(shardIdsByItem);
            }

            string syncRoot = Path.GetFullPath(binding.SyncRoot);
            string currentPath = Path.Combine(syncRoot, "current.json");
            SnapshotCurrentPointer? current =
                await SnapshotPublisher.ReadJsonAsync<SnapshotCurrentPointer>(currentPath, cancellationToken);
            if (current is null)
            {
                return ToSnapshotShardMap(shardIdsByItem);
            }

            string manifestPath = Path.Combine(syncRoot, current.ManifestPath);
            if (!SnapshotPublisher.IsPathInside(manifestPath, syncRoot))
            {
                return ToSnapshotShardMap(shardIdsByItem);
            }

            SnapshotManifest? manifest =
                await SnapshotPublisher.ReadJsonAsync<SnapshotManifest>(manifestPath, cancellationToken);
            if (manifest is null)
            {
                return ToSnapshotShardMap(shardIdsByItem);
            }

            foreach (SnapshotShard shard in manifest.Shards)
            {
                string shardPath = Path.Combine(syncRoot, shard.FileName);
                if (!SnapshotPublisher.IsPathInside(shardPath, syncRoot) || !File.Exists(shardPath))
                {
                    continue;
                }

                await using SqliteConnection shardConnection =
                    new(SnapshotPublisher.BuildConnectionString(shardPath, SqliteOpenMode.ReadOnly));
                await shardConnection.OpenAsync(cancellationToken);
                if (!await SnapshotPublisher.TableExistsAsync(shardConnection, "items"))
                {
                    continue;
                }

                foreach (string[] itemBatch in distinctItemIds.Chunk(ReportQueryBatchSize))
                {
                    string[] matchingItemIds = (await shardConnection.QueryAsync<string>(
                        "select item_id from items where item_id in @ItemIds;",
                        new { ItemIds = itemBatch })).ToArray();
                    foreach (string matchingItemId in matchingItemIds)
                    {
                        shardIdsByItem[matchingItemId].Add(shard.ShardId);
                    }
                }
            }

            return ToSnapshotShardMap(shardIdsByItem);
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.item-purge"))
        {
            return ToSnapshotShardMap(shardIdsByItem);
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ToSnapshotShardMap(
        IReadOnlyDictionary<string, HashSet<string>> shardIdsByItem)
    {
        return shardIdsByItem.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<string>)pair.Value.Order(StringComparer.Ordinal).ToArray(),
            StringComparer.Ordinal);
    }

    private async Task<Result<LibraryChangeSet?>> IncrementRevisionAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        LibraryChangeSet changeSet,
        CancellationToken cancellationToken)
    {
        if (_revisions is null)
        {
            return Result<LibraryChangeSet?>.Success(null);
        }

        Result<LibraryChangeSet> revision = await _revisions.IncrementInTransactionAsync(
            connection, transaction, changeSet, cancellationToken);
        return revision.IsSuccess
            ? Result<LibraryChangeSet?>.Success(revision.Value)
            : Result<LibraryChangeSet?>.Failure(revision.ErrorCode!, revision.ErrorMessage!);
    }

    private void PublishRevision(LibraryChangeSet? changeSet)
    {
        if (changeSet is not null)
        {
            _revisions!.PublishCommitted(changeSet);
        }
    }

    private sealed class DocumentOwnerRow
    {
        public string ItemId { get; set; } = "";
        public string DocumentInstanceId { get; set; } = "";
    }

    private sealed class PurgeFileAssetRow
    {
        public string ItemId { get; set; } = "";
        public string FileAssetId { get; set; } = "";
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O");
    }
}
