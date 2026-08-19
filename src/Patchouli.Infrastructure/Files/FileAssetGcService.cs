using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Snapshots;

namespace Patchouli.Infrastructure.Files;

public sealed class FileAssetGcService : IFileAssetGcService
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly ISnapshotSyncBindingStore? _snapshotBindings;
    private readonly IAppLogger? _logger;

    public FileAssetGcService(
        SqliteConnectionFactory connectionFactory,
        ISnapshotSyncBindingStore? snapshotBindings = null,
        IAppLogger? logger = null)
    {
        _connectionFactory = connectionFactory;
        _snapshotBindings = snapshotBindings;
        _logger = logger;
    }

    public async Task<IReadOnlyList<FileAssetGcCandidate>> PreviewAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);

            IReadOnlySet<string> snapshotAssetIds = await LoadSnapshotFileAssetIdsAsync(cancellationToken);

            IEnumerable<FileAssetRow> rows = await connection.QueryAsync<FileAssetRow>(
                """
                select
                    file_asset_id as FileAssetId,
                    original_path as OriginalPath,
                    status as Status,
                    size_bytes as SizeBytes
                from file_assets
                where status = @Status;
                """,
                new { Status = FileAssetStatus.Available });

            List<FileAssetGcCandidate> candidates = new();
            foreach (FileAssetRow row in rows)
            {
                if (await IsReferencedAsync(connection, row.FileAssetId, snapshotAssetIds, cancellationToken))
                {
                    continue;
                }

                candidates.Add(new FileAssetGcCandidate(
                    FileAssetId.Parse(row.FileAssetId),
                    row.OriginalPath,
                    row.Status,
                    row.SizeBytes));
            }

            return candidates;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.file-asset-gc",
                                              "preview"))
        {
            return Array.Empty<FileAssetGcCandidate>();
        }
    }

    public async Task<FileAssetGcResult> RunAsync(FileAssetGcOptions options,
        CancellationToken cancellationToken = default)
    {
        TimeSpan delay = options.Delay ?? TimeSpan.Zero;
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken);
        }

        IReadOnlyList<FileAssetGcCandidate> candidates = await PreviewAsync(cancellationToken);
        List<FileAssetId> deleted = new();
        List<FileAssetGcFailure> failed = new();

        if (candidates.Count == 0)
        {
            return new FileAssetGcResult(deleted, failed);
        }

        using IDisposable writeLease = await _connectionFactory.EnterWriteAsync(cancellationToken);
        await using SqliteConnection connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

        int maxRetries = Math.Max(0, options.MaxRetries);

        foreach (FileAssetGcCandidate candidate in candidates)
        {
            bool succeeded = false;
            Exception? lastException = null;

            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), cancellationToken);
                }

                try
                {
                    await DeleteCandidateAsync(connection, transaction, candidate.FileAssetId, cancellationToken);
                    succeeded = true;
                    break;
                }
                catch (Exception exception) when (exception is not OperationCanceledException &&
                                                  UnexpectedExceptionReporter.ReportCatch(
                                                      exception,
                                                      "infrastructure.file-asset-gc",
                                                      "delete-candidate"))
                {
                    lastException = exception;
                }
            }

            if (succeeded)
            {
                deleted.Add(candidate.FileAssetId);
                if (_logger is not null)
                {
                    await _logger.LogAsync(
                        "file-asset-gc",
                        $"Deleted {candidate.FileAssetId} ({candidate.OriginalPath}).");
                }
            }
            else
            {
                string message = lastException?.Message ?? "Unknown error while deleting file asset.";
                failed.Add(new FileAssetGcFailure(candidate.FileAssetId, message));
                if (_logger is not null)
                {
                    await _logger.LogAsync(
                        "file-asset-gc",
                        $"Failed to delete {candidate.FileAssetId}: {message}");
                }
            }
        }

        await transaction.CommitAsync(cancellationToken);

        return new FileAssetGcResult(deleted, failed);
    }

    private static async Task DeleteCandidateAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        FileAssetId fileAssetId,
        CancellationToken cancellationToken)
    {
        string id = fileAssetId.ToString();

        await connection.ExecuteAsync(
            new CommandDefinition(
                "delete from known_file_locations where file_asset_id = @FileAssetId;",
                new { FileAssetId = id },
                transaction,
                cancellationToken: cancellationToken));

        await connection.ExecuteAsync(
            new CommandDefinition(
                "delete from file_assets where file_asset_id = @FileAssetId;",
                new { FileAssetId = id },
                transaction,
                cancellationToken: cancellationToken));
    }

    private async Task<bool> IsReferencedAsync(
        SqliteConnection connection,
        string fileAssetId,
        IReadOnlySet<string> snapshotAssetIds,
        CancellationToken cancellationToken)
    {
        if (snapshotAssetIds.Contains(fileAssetId))
        {
            return true;
        }

        int documentInstanceCount = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                """
                select count(1)
                from document_instances
                where file_asset_id = @FileAssetId;
                """,
                new { FileAssetId = fileAssetId },
                cancellationToken: cancellationToken));

        if (documentInstanceCount > 0)
        {
            return true;
        }

        int ocrRunCount = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                """
                select count(1)
                from ocr_runs o
                join document_instances d on d.document_instance_id = o.document_instance_id
                where d.file_asset_id = @FileAssetId;
                """,
                new { FileAssetId = fileAssetId },
                cancellationToken: cancellationToken));

        if (ocrRunCount > 0)
        {
            return true;
        }

        int payloadCount = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                """
                select count(1)
                from document_boxes b
                join document_tree_revisions r on r.tree_revision_id = b.tree_revision_id
                where b.payload_json like '%' || @FileAssetId || '%'
                  and r.status in (@Working, @Committed);
                """,
                new
                {
                    FileAssetId = fileAssetId,
                    Working = DocumentTreeRevisionStatus.Working,
                    Committed = DocumentTreeRevisionStatus.Committed
                },
                cancellationToken: cancellationToken));

        return payloadCount > 0;
    }

    private async Task<IReadOnlySet<string>> LoadSnapshotFileAssetIdsAsync(CancellationToken cancellationToken)
    {
        HashSet<string> ids = new();

        if (_snapshotBindings is null)
        {
            return ids;
        }

        try
        {
            Result<SnapshotSyncBinding> bindingResult = await _snapshotBindings.GetBindingAsync(cancellationToken);
            if (bindingResult.IsFailure)
            {
                return ids;
            }

            SnapshotSyncBinding binding = bindingResult.Value;
            if (string.IsNullOrWhiteSpace(binding.SyncRoot) || !Directory.Exists(binding.SyncRoot))
            {
                return ids;
            }

            string syncRoot = Path.GetFullPath(binding.SyncRoot);
            string currentPath = Path.Combine(syncRoot, "current.json");
            SnapshotCurrentPointer? current =
                await SnapshotPublisher.ReadJsonAsync<SnapshotCurrentPointer>(currentPath, cancellationToken);

            if (current is null)
            {
                return ids;
            }

            string manifestPath = Path.Combine(syncRoot, current.ManifestPath);
            if (!SnapshotPublisher.IsPathInside(manifestPath, syncRoot))
            {
                return ids;
            }

            SnapshotManifest? manifest =
                await SnapshotPublisher.ReadJsonAsync<SnapshotManifest>(manifestPath, cancellationToken);

            if (manifest is null)
            {
                return ids;
            }

            foreach (SnapshotShard shard in manifest.Shards.Concat(manifest.SensitiveMutableShards))
            {
                string shardPath = Path.Combine(syncRoot, shard.FileName);
                if (!SnapshotPublisher.IsPathInside(shardPath, syncRoot) || !File.Exists(shardPath))
                {
                    continue;
                }

                await using SqliteConnection shardConnection =
                    new(SnapshotPublisher.BuildConnectionString(shardPath, SqliteOpenMode.ReadOnly));
                await shardConnection.OpenAsync(cancellationToken);

                if (!await SnapshotPublisher.TableExistsAsync(shardConnection, "file_assets"))
                {
                    continue;
                }

                IEnumerable<string> shardIds = await shardConnection.QueryAsync<string>(
                    new CommandDefinition(
                        "select file_asset_id from file_assets;",
                        cancellationToken: cancellationToken));

                foreach (string id in shardIds)
                {
                    ids.Add(id);
                }
            }
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.file-asset-gc",
                                              "load-snapshot-refs"))
        {
            // Treat a broken snapshot as if it contained no references so a bad remote pointer
            // does not block local cleanup.
        }

        return ids;
    }

    private sealed class FileAssetRow
    {
        public string FileAssetId { get; set; } = string.Empty;
        public string OriginalPath { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
    }
}
