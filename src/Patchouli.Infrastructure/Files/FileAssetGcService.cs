using System.Data.Common;
using System.Text.Json;
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
    private const int MaxCachedSnapshotShards = 256;

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly ISnapshotSyncBindingStore? _snapshotBindings;
    private readonly IAppLogger? _logger;
    private readonly object _snapshotShardCacheLock = new();
    private readonly Dictionary<SnapshotShardCacheKey, SnapshotShardCacheEntry> _snapshotShardCache = new();
    private readonly Queue<SnapshotShardCacheKey> _snapshotShardCacheOrder = new();

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
            return await LoadCandidatesAsync(connection, null, null, snapshotAssetIds, cancellationToken);
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
        return await RunCoreAsync(null, options, cancellationToken);
    }

    public async Task<FileAssetGcResult> RunCandidatesAsync(
        IEnumerable<FileAssetId> fileAssetIds,
        FileAssetGcOptions options,
        CancellationToken cancellationToken = default)
    {
        string[] ids = fileAssetIds.Select(fileAssetId => fileAssetId.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return ids.Length == 0
            ? new FileAssetGcResult(Array.Empty<FileAssetId>(), Array.Empty<FileAssetGcFailure>())
            : await RunCoreAsync(JsonSerializer.Serialize(ids), options, cancellationToken);
    }

    private async Task<FileAssetGcResult> RunCoreAsync(
        string? candidateIdsJson,
        FileAssetGcOptions options,
        CancellationToken cancellationToken)
    {
        TimeSpan delay = options.Delay ?? TimeSpan.Zero;
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken);
        }

        List<FileAssetId> deleted = new();
        List<FileAssetGcFailure> failed = new();

        using IDisposable writeLease = await _connectionFactory.EnterWriteAsync(cancellationToken);
        IReadOnlySet<string> snapshotAssetIds;
        try
        {
            snapshotAssetIds = await LoadSnapshotFileAssetIdsAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException &&
                                          UnexpectedExceptionReporter.ReportCatch(
                                              exception,
                                              "infrastructure.file-asset-gc",
                                              "load-snapshot-refs"))
        {
            // Do not delete local assets when a configured snapshot cannot be inspected safely.
            return new FileAssetGcResult(deleted, failed);
        }

        await using SqliteConnection connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
        IReadOnlyList<FileAssetGcCandidate> candidates = await LoadCandidatesAsync(
            connection,
            transaction,
            candidateIdsJson,
            snapshotAssetIds,
            cancellationToken);

        if (candidates.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return new FileAssetGcResult(deleted, failed);
        }

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

    private async Task<IReadOnlyList<FileAssetGcCandidate>> LoadCandidatesAsync(
        SqliteConnection connection,
        DbTransaction? transaction,
        string? candidateIdsJson,
        IReadOnlySet<string> snapshotAssetIds,
        CancellationToken cancellationToken)
    {
        string candidateFilter = candidateIdsJson is null
            ? string.Empty
            : "and a.file_asset_id in (select value from json_each(@CandidateFileAssetIdsJson))";
        IEnumerable<FileAssetRow> rows = await connection.QueryAsync<FileAssetRow>(
            new CommandDefinition(
                $"""
                 select
                     a.file_asset_id as FileAssetId,
                     a.original_path as OriginalPath,
                     a.status as Status,
                     a.size_bytes as SizeBytes
                 from file_assets a
                 where a.status = @Status
                   {candidateFilter}
                   and not exists (
                       select 1
                       from document_instances d
                       where d.file_asset_id = a.file_asset_id)
                   and not exists (
                       select 1
                       from ocr_runs o
                       join document_instances d on d.document_instance_id = o.document_instance_id
                       where d.file_asset_id = a.file_asset_id)
                   and not exists (
                       select 1
                       from file_asset_payload_refs p
                       where p.file_asset_id = lower(a.file_asset_id));
                 """,
                new
                {
                    Status = FileAssetStatus.Available,
                    CandidateFileAssetIdsJson = candidateIdsJson
                },
                transaction,
                cancellationToken: cancellationToken));

        return rows
            .Where(row => !snapshotAssetIds.Contains(row.FileAssetId))
            .Select(row => new FileAssetGcCandidate(
                FileAssetId.Parse(row.FileAssetId),
                row.OriginalPath,
                row.Status,
                row.SizeBytes))
            .ToArray();
    }

    private async Task<IReadOnlySet<string>> LoadSnapshotFileAssetIdsAsync(CancellationToken cancellationToken)
    {
        if (_snapshotBindings is null)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        Result<SnapshotSyncBinding> bindingResult = await _snapshotBindings.GetBindingAsync(cancellationToken);
        if (bindingResult.IsFailure)
        {
            throw new InvalidOperationException(bindingResult.ErrorMessage ?? "Snapshot binding could not be loaded.");
        }

        SnapshotSyncBinding binding = bindingResult.Value;
        if (string.IsNullOrWhiteSpace(binding.SyncRoot) || !Directory.Exists(binding.SyncRoot))
        {
            throw new DirectoryNotFoundException("The configured snapshot sync root is unavailable.");
        }

        string syncRoot = Path.GetFullPath(binding.SyncRoot);
        string currentPath = Path.Combine(syncRoot, "current.json");
        if (!File.Exists(currentPath))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        SnapshotCurrentPointer? current =
            await SnapshotPublisher.ReadJsonAsync<SnapshotCurrentPointer>(currentPath, cancellationToken);
        if (current is null)
        {
            throw new InvalidDataException("The configured snapshot pointer is invalid.");
        }

        string manifestPath = Path.GetFullPath(Path.Combine(syncRoot, current.ManifestPath));
        if (!SnapshotPublisher.IsPathInside(manifestPath, syncRoot))
        {
            throw new InvalidDataException("The configured snapshot manifest path is outside its sync root.");
        }

        SnapshotManifest? manifest =
            await SnapshotPublisher.ReadJsonAsync<SnapshotManifest>(manifestPath, cancellationToken);
        if (manifest is null)
        {
            throw new InvalidDataException("The configured snapshot manifest is invalid or missing.");
        }

        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (SnapshotShard shard in manifest.Shards.Concat(manifest.SensitiveMutableShards))
        {
            string shardPath = Path.GetFullPath(Path.Combine(syncRoot, shard.FileName));
            if (!SnapshotPublisher.IsPathInside(shardPath, syncRoot) || !File.Exists(shardPath))
            {
                throw new InvalidDataException("A referenced snapshot shard is missing or outside its sync root.");
            }

            foreach (string id in await LoadSnapshotShardFileAssetIdsAsync(shardPath, shard, cancellationToken))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private async Task<IReadOnlySet<string>> LoadSnapshotShardFileAssetIdsAsync(
        string shardPath,
        SnapshotShard shard,
        CancellationToken cancellationToken)
    {
        FileInfo file = new(shardPath);
        SnapshotShardCacheKey key = new(shardPath, shard.Blake3);
        lock (_snapshotShardCacheLock)
        {
            if (_snapshotShardCache.TryGetValue(key, out SnapshotShardCacheEntry? cached) &&
                cached.SizeBytes == file.Length &&
                cached.LastWriteTimeUtc == file.LastWriteTimeUtc)
            {
                return cached.FileAssetIds;
            }
        }

        string actualHash = await SnapshotPublisher.Blake3FileAsync(shardPath);
        if (!string.Equals(actualHash, shard.Blake3, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A referenced snapshot shard failed hash verification.");
        }

        await using SqliteConnection shardConnection =
            new(SnapshotPublisher.BuildConnectionString(shardPath, SqliteOpenMode.ReadOnly));
        await shardConnection.OpenAsync(cancellationToken);
        HashSet<string> ids = new(StringComparer.Ordinal);
        if (await SnapshotPublisher.TableExistsAsync(shardConnection, "file_assets"))
        {
            IEnumerable<string> shardIds = await shardConnection.QueryAsync<string>(
                new CommandDefinition(
                    "select file_asset_id from file_assets;",
                    cancellationToken: cancellationToken));
            ids.UnionWith(shardIds);
        }

        lock (_snapshotShardCacheLock)
        {
            if (!_snapshotShardCache.ContainsKey(key))
            {
                _snapshotShardCacheOrder.Enqueue(key);
            }

            _snapshotShardCache[key] = new SnapshotShardCacheEntry(file.Length, file.LastWriteTimeUtc, ids);
            while (_snapshotShardCache.Count > MaxCachedSnapshotShards &&
                   _snapshotShardCacheOrder.TryDequeue(out SnapshotShardCacheKey oldest))
            {
                _snapshotShardCache.Remove(oldest);
            }
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

    private readonly record struct SnapshotShardCacheKey(string Path, string Blake3);

    private sealed record SnapshotShardCacheEntry(
        long SizeBytes,
        DateTime LastWriteTimeUtc,
        IReadOnlySet<string> FileAssetIds);
}
