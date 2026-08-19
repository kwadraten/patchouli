using System.Data.Common;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Ocr;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Snapshots;

namespace Patchouli.Infrastructure.Bibliography;

public sealed class ItemPurgeService : IItemPurgeService
{
    private const string PurgeReason = "user_purge";

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
        Result<LibraryMetadata> libraryResult = await _libraryIdentityService.GetCurrentLibraryAsync(cancellationToken);
        if (libraryResult.IsFailure)
        {
            return Result<ItemPurgeDependencyReport>.Failure(libraryResult.ErrorCode!, libraryResult.ErrorMessage!);
        }

        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);

            int itemExists = await connection.ExecuteScalarAsync<int>(
                """
                select count(1)
                from items
                where item_id = @ItemId
                  and deleted_at is not null
                  and merged_into_item_id is null;
                """,
                new { ItemId = itemId.ToString() });
            if (itemExists == 0)
            {
                return Result<ItemPurgeDependencyReport>.Failure(AppErrorCodes.NotFound,
                    "Item was not found in trash.");
            }

            string[] documentIds = (await connection.QueryAsync<string>(
                "select document_instance_id from document_instances where item_id = @ItemId;",
                new { ItemId = itemId.ToString() })).ToArray();

            bool hasActiveOcr = documentIds.Length > 0 && await connection.ExecuteScalarAsync<int>(
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
                }) > 0;

            bool hasOcrCandidates = documentIds.Length > 0 && await connection.ExecuteScalarAsync<int>(
                """
                select count(1)
                from ocr_page_results r
                join ocr_runs o on o.ocr_run_id = r.ocr_run_id
                where o.document_instance_id in @DocumentIds
                  and r.working_tree_revision_id is not null;
                """,
                new { DocumentIds = documentIds }) > 0;

            bool hasWorking = documentIds.Length > 0 && await connection.ExecuteScalarAsync<int>(
                """
                select count(1)
                from document_tree_revisions
                where document_instance_id in @DocumentIds
                  and status = @Working;
                """,
                new { DocumentIds = documentIds, Working = "working" }) > 0;

            IReadOnlyList<string> snapshotShardIds =
                await FindSnapshotShardIdsAsync(connection, itemId, cancellationToken);

            return Result<ItemPurgeDependencyReport>.Success(new ItemPurgeDependencyReport(
                itemId,
                snapshotShardIds,
                snapshotShardIds.Count,
                hasActiveOcr,
                hasOcrCandidates,
                hasWorking));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.item-purge"))
        {
            return Result<ItemPurgeDependencyReport>.Failure(
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

            int trashCount = await connection.ExecuteScalarAsync<int>(
                """
                select count(1)
                from items
                where item_id in @ItemIds
                  and deleted_at is not null
                  and merged_into_item_id is null;
                """,
                new { ItemIds = itemIdStrings });
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
                    new { ItemIds = itemIdStrings }))
                .ToDictionary(row => row.ItemId, row => row.Count, StringComparer.Ordinal);

            string[] documentIds = (await connection.QueryAsync<string>(
                "select document_instance_id from document_instances where item_id in @ItemIds;",
                new { ItemIds = itemIdStrings })).ToArray();

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
                    });
            if (activeOcrCount > 0)
            {
                return Result.Failure(
                    AppErrorCodes.InvalidState,
                    "Cannot purge items while OCR runs are pending or running.");
            }

            await connection.ExecuteAsync("pragma foreign_keys = off;");
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            if (documentIds.Length > 0)
            {
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

                await connection.ExecuteAsync(
                    "delete from search_units_fts where document_instance_id in @DocumentIds;",
                    new { DocumentIds = documentIds },
                    transaction);

                await connection.ExecuteAsync(
                    "delete from search_units where document_instance_id in @DocumentIds;",
                    new { DocumentIds = documentIds },
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
                LibraryChangeSet.Empty with { ItemIds = distinctIds },
                cancellationToken);
            if (revision.IsFailure)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result.Failure(revision.ErrorCode!, revision.ErrorMessage!);
            }

            await transaction.CommitAsync(cancellationToken);
            await connection.ExecuteAsync("pragma foreign_keys = on;");
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

    private async Task<IReadOnlyList<string>> FindSnapshotShardIdsAsync(
        SqliteConnection connection,
        ItemId itemId,
        CancellationToken cancellationToken)
    {
        if (_snapshotBindings is null)
        {
            return Array.Empty<string>();
        }

        try
        {
            Result<SnapshotSyncBinding> bindingResult = await _snapshotBindings.GetBindingAsync(cancellationToken);
            if (bindingResult.IsFailure)
            {
                return Array.Empty<string>();
            }

            SnapshotSyncBinding binding = bindingResult.Value;
            if (string.IsNullOrWhiteSpace(binding.SyncRoot) || !Directory.Exists(binding.SyncRoot))
            {
                return Array.Empty<string>();
            }

            string syncRoot = Path.GetFullPath(binding.SyncRoot);
            string currentPath = Path.Combine(syncRoot, "current.json");
            SnapshotCurrentPointer? current =
                await SnapshotPublisher.ReadJsonAsync<SnapshotCurrentPointer>(currentPath, cancellationToken);
            if (current is null)
            {
                return Array.Empty<string>();
            }

            string manifestPath = Path.Combine(syncRoot, current.ManifestPath);
            if (!SnapshotPublisher.IsPathInside(manifestPath, syncRoot))
            {
                return Array.Empty<string>();
            }

            SnapshotManifest? manifest =
                await SnapshotPublisher.ReadJsonAsync<SnapshotManifest>(manifestPath, cancellationToken);
            if (manifest is null)
            {
                return Array.Empty<string>();
            }

            List<string> shardIds = new();
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

                int count = await shardConnection.ExecuteScalarAsync<int>(
                    "select count(1) from items where item_id = @ItemId;",
                    new { ItemId = itemId.ToString() });
                if (count > 0)
                {
                    shardIds.Add(shard.ShardId);
                }
            }

            return shardIds;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.item-purge"))
        {
            return Array.Empty<string>();
        }
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

    private static string FormatUtc(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O");
    }
}
