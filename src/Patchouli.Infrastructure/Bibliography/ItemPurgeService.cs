using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Ocr;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Documents;
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
    private readonly IAppLogger? _logger;

    public ItemPurgeService(
        SqliteConnectionFactory connectionFactory,
        IClock clock,
        ILibraryIdentityService libraryIdentityService,
        ISnapshotSyncBindingStore? snapshotBindings = null,
        ILibraryRevisionService? revisions = null,
        IAppLogger? logger = null)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
        _libraryIdentityService = libraryIdentityService;
        _snapshotBindings = snapshotBindings;
        _revisions = revisions;
        _logger = logger;
    }

    public async Task<Result<ItemPurgeDependencyReport>> BuildPurgeReportAsync(
        ItemId itemId,
        CancellationToken cancellationToken = default,
        IProgress<ItemPurgeProgress>? progress = null)
    {
        Result<IReadOnlyList<ItemPurgeDependencyReport>> reports =
            await BuildPurgeReportsAsync([itemId], cancellationToken, progress);
        return reports.IsSuccess
            ? Result<ItemPurgeDependencyReport>.Success(reports.Value[0])
            : Result<ItemPurgeDependencyReport>.Failure(
                reports.ErrorCode!, reports.ErrorMessage!, reports.Conflicts, reports.Details);
    }

    public async Task<Result<IReadOnlyList<ItemPurgeDependencyReport>>> BuildPurgeReportsAsync(
        IReadOnlyList<ItemId> itemIds,
        CancellationToken cancellationToken = default,
        IProgress<ItemPurgeProgress>? progress = null)
    {
        long started = StartPhase(progress, "report_total", "正在生成永久删除依赖报告...");
        try
        {
            return await BuildPurgeReportsCoreAsync(itemIds, cancellationToken, progress);
        }
        finally
        {
            await FinishPhaseAsync(progress, "report_total", "依赖报告读取已结束。", started);
        }
    }

    private async Task<Result<IReadOnlyList<ItemPurgeDependencyReport>>> BuildPurgeReportsCoreAsync(
        IReadOnlyList<ItemId> itemIds, CancellationToken cancellationToken, IProgress<ItemPurgeProgress>? progress)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (itemIds.Count == 0)
        {
            return Result<IReadOnlyList<ItemPurgeDependencyReport>>.Success([]);
        }

        long databaseStarted = StartPhase(progress, "report_database", "正在读取题录与文档依赖...");
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
                cancellationToken.ThrowIfCancellationRequested();
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
                cancellationToken.ThrowIfCancellationRequested();
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

            await FinishPhaseAsync(progress, "report_database", "题录与文档依赖已读取。", databaseStarted);
            long snapshotStarted = StartPhase(progress, "snapshot_scan", "正在检查快照分片依赖...");
            IReadOnlyDictionary<string, IReadOnlyList<string>> shardIdsByItem;
            try
            {
                shardIdsByItem = await FindSnapshotShardIdsAsync(itemIdStrings, cancellationToken);
            }
            finally
            {
                await FinishPhaseAsync(progress, "snapshot_scan", "快照依赖检查已结束。", snapshotStarted);
            }

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
        CancellationToken cancellationToken = default,
        IProgress<ItemPurgeProgress>? progress = null)
    {
        long started = StartPhase(progress, "purge_total", "正在永久删除题录及页面历史...");
        try
        {
            return await PurgeItemsCoreAsync(itemIds, cancellationToken, progress);
        }
        finally
        {
            await FinishPhaseAsync(progress, "purge_total", "永久删除事务已结束。", started);
        }
    }

    private async Task<Result> PurgeItemsCoreAsync(IReadOnlyList<ItemId> itemIds,
        CancellationToken cancellationToken, IProgress<ItemPurgeProgress>? progress)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
            using IDisposable writeLease = await EnterWriteWithTimingAsync(cancellationToken, progress);
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction =
                await BeginTransactionWithTimingAsync(connection, cancellationToken, progress);
            cancellationToken.ThrowIfCancellationRequested();
            long validationStarted = StartPhase(progress, "validation", "正在重新检查回收站和活动 OCR 状态...");

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

            string[] pageIds = await DocumentPayloadCleanup.ListPageIdsAsync(
                connection, transaction, documentIds, cancellationToken);

            int activeOcrCount = await DocumentPayloadCleanup.CountActiveOcrRunsAsync(
                connection, transaction, documentIds, cancellationToken);
            if (activeOcrCount > 0)
            {
                return Result.Failure(
                    AppErrorCodes.InvalidState,
                    "Cannot purge items while OCR runs are pending or running.");
            }

            await FinishPhaseAsync(progress, "validation", "删除范围和活动任务检查已完成。", validationStarted);

            if (documentIds.Length > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long ftsStarted = StartPhase(progress, "fts_prepare", "正在准备全文索引缓存...");
                bool rebuilt = await DocumentPayloadCleanup.EnsureFtsCacheReadyAsync(
                    connection, transaction, cancellationToken);
                await FinishPhaseAsync(progress, "fts_prepare", rebuilt ? "全文索引缓存已重建。" : "全文索引缓存已就绪，无需重建。", ftsStarted,
                    rebuilt);

                cancellationToken.ThrowIfCancellationRequested();
                long ocrStarted = StartPhase(progress, "ocr_history_delete", "正在删除 OCR 识别和采纳记录...");
                await DocumentPayloadCleanup.DeleteOcrHistoryAsync(
                    connection, transaction, documentIds, cancellationToken);
                await FinishPhaseAsync(progress, "ocr_history_delete", "OCR 识别和采纳记录已清理。", ocrStarted);

                cancellationToken.ThrowIfCancellationRequested();
                long ftsDeleteStarted = StartPhase(progress, "fts_delete", "正在清理全文索引和搜索单元...");
                await DocumentPayloadCleanup.DeleteSearchIndexAsync(
                    connection, transaction, documentIds, pageIds, cancellationToken);
                await FinishPhaseAsync(progress, "fts_delete", "全文索引和搜索单元已清理。", ftsDeleteStarted);

                cancellationToken.ThrowIfCancellationRequested();
                long historyStarted = StartPhase(progress, "document_history_delete", "正在删除页面、译文、框树和修订历史...");
                await DocumentPayloadCleanup.DeleteDocumentHistoryAsync(
                    connection, transaction, documentIds, pageIds, cancellationToken);
                await FinishPhaseAsync(progress, "document_history_delete", "页面及正文历史已清理。", historyStarted);
            }

            cancellationToken.ThrowIfCancellationRequested();
            long itemStarted = StartPhase(progress, "item_delete", "正在删除题录附属信息并登记清除记录...");
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

            await FinishPhaseAsync(progress, "item_delete", "题录和清除记录已更新。", itemStarted);

            cancellationToken.ThrowIfCancellationRequested();
            long commitStarted = StartPhase(progress, "commit", "正在提交整批永久删除事务...");
            await transaction.CommitAsync(cancellationToken);
            await FinishPhaseAsync(progress, "commit", "整批永久删除已提交。", commitStarted);
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

    private async Task<IDisposable> EnterWriteWithTimingAsync(CancellationToken cancellationToken,
        IProgress<ItemPurgeProgress>? progress)
    {
        long started = StartPhase(progress, "write_lease_wait", "正在等待书库写入许可...");
        try
        {
            return await _connectionFactory.EnterWriteAsync(cancellationToken);
        }
        finally
        {
            await FinishPhaseAsync(progress, "write_lease_wait", "书库写入许可等待已结束。", started);
        }
    }

    private async Task<DbTransaction> BeginTransactionWithTimingAsync(SqliteConnection connection,
        CancellationToken cancellationToken, IProgress<ItemPurgeProgress>? progress)
    {
        // Microsoft.Data.Sqlite's non-deferred transaction acquires the SQLite write lock here.
        // Keep native lock acquisition (including busy waiting) separate from payload SQL timings.
        long started = StartPhase(progress, "sqlite_write_lock", "正在获取 SQLite 写入锁并开启事务...");
        int? errorCode = null;
        try
        {
            return await connection.BeginTransactionAsync(cancellationToken);
        }
        catch (SqliteException exception)
        {
            errorCode = exception.SqliteErrorCode;
            throw;
        }
        finally
        {
            await FinishPhaseAsync(progress, "sqlite_write_lock", "SQLite 写入锁获取阶段已结束。", started,
                sqliteErrorCode: errorCode);
        }
    }

    private static long StartPhase(IProgress<ItemPurgeProgress>? progress, string phase, string message)
    {
        ReportSafely(progress, new ItemPurgeProgress(phase, message));
        return Stopwatch.GetTimestamp();
    }

    private async Task FinishPhaseAsync(IProgress<ItemPurgeProgress>? progress, string phase, string message,
        long started, bool? ftsCacheRebuilt = null, int? sqliteErrorCode = null)
    {
        double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        ReportSafely(progress,
            new ItemPurgeProgress(phase, message, true, milliseconds, ftsCacheRebuilt, sqliteErrorCode));
        if (_logger is null)
        {
            return;
        }

        try
        {
            await _logger.LogAsync("item-purge-phase", string.Create(CultureInfo.InvariantCulture,
                $"phase={phase}; elapsed_ms={milliseconds:F3}; fts_rebuilt={ftsCacheRebuilt}; sqlite_error={sqliteErrorCode}"));
        }
        catch (Exception exception) when (exception is OperationCanceledException ||
                                          UnexpectedExceptionReporter.ReportCatch(exception, "item-purge-diagnostics",
                                              phase))
        {
            // Diagnostic cancellation/failure must not replace the transaction's outcome.
            return;
        }
    }

    private static void ReportSafely(IProgress<ItemPurgeProgress>? progress, ItemPurgeProgress update)
    {
        try
        {
            progress?.Report(update);
        }
        catch (Exception exception) when (exception is OperationCanceledException ||
                                          UnexpectedExceptionReporter.ReportCatch(exception, "item-purge-progress",
                                              update.Phase))
        {
            // Reporting must not turn a committed deletion into a business failure.
            return;
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
                cancellationToken.ThrowIfCancellationRequested();
                string shardPath = Path.Combine(syncRoot, shard.FileName);
                if (!SnapshotPublisher.IsPathInside(shardPath, syncRoot) || !File.Exists(shardPath))
                {
                    continue;
                }

                await using SqliteConnection shardConnection =
                    SqliteConnectionFactory.CreateUnpooledReadConnection(shardPath);
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
