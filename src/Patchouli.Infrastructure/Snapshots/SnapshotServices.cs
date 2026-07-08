using System.Text.Json;
using Dapper;
using Patchouli.Core;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Hashing;
using Microsoft.Data.Sqlite;

namespace Patchouli.Infrastructure.Snapshots;

public sealed class SnapshotPublisher : ISnapshotPublisher
{
    private const long DefaultTargetShardSizeBytes = 512L * 1024L * 1024L;
    private static readonly string[] DataTables =
    [
        "library_metadata",
        "items",
        "item_identifiers",
        "item_creators",
        "item_dates",
        "file_assets",
        "file_search_roots",
        "known_file_locations",
        "document_instances",
        "pages",
        "layout_revisions",
        "layout_nodes",
        "ocr_presets",
        "ocr_preset_versions",
        "ocr_runs",
        "ocr_page_results",
        "ocr_candidate_adoptions",
        "search_units",
        "search_index_status",
        "evidence_ref_records",
        "evidence_successors",
        "search_profiles",
        "search_rewrite_rules",
        "search_settings"
    ];
    private static readonly string[][] DataShardTableGroups =
    [
        ["library_metadata", "items", "item_identifiers", "item_creators", "item_dates", "file_assets", "file_search_roots", "known_file_locations", "document_instances"],
        ["pages", "layout_revisions", "layout_nodes"],
        ["ocr_presets", "ocr_preset_versions", "ocr_runs", "ocr_page_results", "ocr_candidate_adoptions"],
        ["search_units", "search_index_status", "evidence_ref_records", "evidence_successors"],
        ["search_profiles", "search_rewrite_rules", "search_settings"]
    ];
    private static readonly string[] CredentialTables = ["provider_credentials", "provider_credential_bindings"];
    private static readonly string[] LocalOnlyTables = ["mcp_server_settings", "mcp_tool_overrides"];
    private readonly IClock _clock;

    public SnapshotPublisher(IClock clock)
    {
        _clock = clock;
    }

    public async Task<Result<SnapshotPublishResult>> PublishSnapshotAsync(SnapshotPublishRequest request, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(request.RuntimeDatabasePath))
        {
            return Result<SnapshotPublishResult>.Failure(AppErrorCodes.NotFound, "Runtime database was not found.");
        }
        if (string.IsNullOrWhiteSpace(request.SyncRoot) || string.IsNullOrWhiteSpace(request.DeviceId))
        {
            return Result<SnapshotPublishResult>.Failure(AppErrorCodes.ValidationFailed, "Sync root and device id are required.");
        }

        var runtimePath = Path.GetFullPath(request.RuntimeDatabasePath);
        var syncRoot = Path.GetFullPath(request.SyncRoot);
        if (IsPathInside(runtimePath, syncRoot))
        {
            return Result<SnapshotPublishResult>.Failure(AppErrorCodes.ValidationFailed, "Runtime database must not be inside the sync root.");
        }

        Directory.CreateDirectory(Path.Combine(syncRoot, "manifests"));
        Directory.CreateDirectory(Path.Combine(syncRoot, "shards"));
        Directory.CreateDirectory(Path.Combine(syncRoot, "branches"));

        var currentPath = Path.Combine(syncRoot, "current.json");
        var current = await ReadJsonAsync<SnapshotCurrentPointer>(currentPath, cancellationToken);
        if (current is not null && current.SnapshotId != request.ParentSnapshotId)
        {
            var branch = new SnapshotBranchInfo(
                Guid.NewGuid().ToString("D"),
                current.LibraryId,
                request.DeviceId,
                request.ParentSnapshotId,
                current.SnapshotId,
                _clock.UtcNow.ToUniversalTime(),
                "parent_mismatch",
                null);
            var branchPath = Path.Combine(syncRoot, "branches", $"{branch.BranchId}.json");
            await WriteJsonAtomicAsync(branchPath, branch, cancellationToken);
            return Result<SnapshotPublishResult>.Success(new SnapshotPublishResult("", "", currentPath, true, branch, Array.Empty<SnapshotShard>(), current.LogicalGeneration, "Remote current snapshot no longer matches local parent; branch metadata was written and current pointer was not overwritten."));
        }

        try
        {
            var libraryId = await ReadLibraryIdAsync(runtimePath);
            var generation = current is null ? 1 : current.LogicalGeneration + 1;
            var snapshotId = Guid.NewGuid().ToString("D");

            await CheckpointAsync(runtimePath);
            var shards = await CreateDataShardsAsync(runtimePath, syncRoot, snapshotId, NormalizeTargetShardSize(request.TargetShardSizeBytes));
            foreach (var shard in shards)
                if (!await VerifyShardAsync(syncRoot, shard))
                    return Result<SnapshotPublishResult>.Failure(AppErrorCodes.DatabaseError, "Shard hash verification failed after publish.");

            var credentialShard = await CreateCredentialShardAsync(runtimePath, syncRoot, snapshotId);
            var manifest = new SnapshotManifest(1, libraryId, request.DeviceId, snapshotId, request.ParentSnapshotId, AppSchemaVersion.Current, generation, _clock.UtcNow.ToUniversalTime(), shards, credentialShard is null ? Array.Empty<SnapshotShard>() : new[] { credentialShard }, await Blake3FileAsync(runtimePath), request.Notes);
            var manifestPath = Path.Combine(syncRoot, "manifests", $"{snapshotId}.json");
            await WriteJsonAtomicAsync(manifestPath, manifest, cancellationToken);
            var pointer = new SnapshotCurrentPointer(snapshotId, Path.Combine("manifests", $"{snapshotId}.json"), libraryId, generation, _clock.UtcNow.ToUniversalTime());
            await WriteJsonAtomicAsync(currentPath, pointer, cancellationToken);

            return Result<SnapshotPublishResult>.Success(new SnapshotPublishResult(snapshotId, manifestPath, currentPath, false, null, shards, generation, shards.Count > 1 ? "Runtime database exceeded the snapshot shard target; data was split into multiple immutable shards. FTS rows are cleared in data shards; persisted search_units remain canonical." : "FTS rows are cleared in the snapshot shard; persisted search_units remain canonical."));
        }
        catch (Exception ex)
        {
            return Result<SnapshotPublishResult>.Failure(AppErrorCodes.DatabaseError, $"Snapshot publish failed: {ex.Message}");
        }
    }

    public static async Task CheckpointAsync(string databasePath)
    {
        await using var connection = new SqliteConnection(BuildConnectionString(databasePath, SqliteOpenMode.ReadWriteCreate));
        await connection.OpenAsync();
        await connection.ExecuteAsync("pragma wal_checkpoint(full);");
    }

    public static async Task BackupDatabaseAsync(string sourcePath, string targetPath)
    {
        if (File.Exists(targetPath)) File.Delete(targetPath);
        await using var source = new SqliteConnection(BuildConnectionString(sourcePath, SqliteOpenMode.ReadOnly));
        await using var target = new SqliteConnection(BuildConnectionString(targetPath, SqliteOpenMode.ReadWriteCreate));
        await source.OpenAsync();
        await target.OpenAsync();
        source.BackupDatabase(target);
    }

    public static async Task ClearLocalFtsCacheAsync(string shardPath)
    {
        await using var connection = new SqliteConnection(BuildConnectionString(shardPath, SqliteOpenMode.ReadWriteCreate));
        await connection.OpenAsync();
        var exists = await connection.ExecuteScalarAsync<int>("select count(1) from sqlite_master where name = 'search_units_fts';");
        if (exists > 0)
        {
            await connection.ExecuteAsync("delete from search_units_fts;");
        }
    }

    public static async Task RedactCredentialsAsync(string shardPath)
    {
        await using var connection = new SqliteConnection(BuildConnectionString(shardPath, SqliteOpenMode.ReadWriteCreate));
        await connection.OpenAsync();
        var exists = await connection.ExecuteScalarAsync<int>("select count(1) from sqlite_master where name = 'provider_credentials';");
        if (exists > 0) await connection.ExecuteAsync("update provider_credentials set secret_value = '[redacted]';");
    }

    public static async Task RedactLocalFileLocationsAsync(string shardPath)
    {
        await using var connection = new SqliteConnection(BuildConnectionString(shardPath, SqliteOpenMode.ReadWriteCreate));
        await connection.OpenAsync();
        if (await connection.ExecuteScalarAsync<int>("select count(1) from sqlite_master where name = 'file_assets';") > 0)
            await connection.ExecuteAsync("update file_assets set original_path = '[redacted]';");
        if (await connection.ExecuteScalarAsync<int>("select count(1) from sqlite_master where name = 'known_file_locations';") > 0)
            await connection.ExecuteAsync("delete from known_file_locations;");
        if (await connection.ExecuteScalarAsync<int>("select count(1) from sqlite_master where name = 'file_search_roots';") > 0)
            await connection.ExecuteAsync("delete from file_search_roots;");
        if (await connection.ExecuteScalarAsync<int>("select count(1) from sqlite_master where name = 'ocr_preset_versions';") > 0)
            await connection.ExecuteAsync("update ocr_preset_versions set model_path = '[redacted]' where model_path is not null;");
        await connection.ExecuteAsync("vacuum;");
    }

    private static long NormalizeTargetShardSize(long targetShardSizeBytes) => targetShardSizeBytes <= 0 ? DefaultTargetShardSizeBytes : targetShardSizeBytes;

    private static async Task<IReadOnlyList<SnapshotShard>> CreateDataShardsAsync(string runtimePath, string syncRoot, string snapshotId, long targetShardSizeBytes)
    {
        if (new FileInfo(runtimePath).Length <= targetShardSizeBytes)
        {
            var shardId = Guid.NewGuid().ToString("D");
            var shardFile = $"{shardId}.sqlite";
            var shardPath = Path.Combine(syncRoot, "shards", shardFile);
            await BackupDatabaseAsync(runtimePath, shardPath);
            await PrepareDataShardAsync(shardPath, null);
            var shard = new SnapshotShard(shardId, Path.Combine("shards", shardFile), new FileInfo(shardPath).Length, await Blake3FileAsync(shardPath), "data", true);
            return [await ReuseExistingImmutableShardAsync(syncRoot, shard)];
        }

        var shards = new List<SnapshotShard>();
        for (var i = 0; i < DataShardTableGroups.Length; i++)
        {
            var tableGroup = DataShardTableGroups[i];
            var shardId = $"{snapshotId}_data_{i + 1:D2}";
            var shardFile = $"{shardId}.sqlite";
            var shardPath = Path.Combine(syncRoot, "shards", shardFile);
            await BackupDatabaseAsync(runtimePath, shardPath);
            await PrepareDataShardAsync(shardPath, tableGroup);
            if (await HasAnyRowsAsync(shardPath, tableGroup) || i == 0)
            {
                var shard = new SnapshotShard(shardId, Path.Combine("shards", shardFile), new FileInfo(shardPath).Length, await Blake3FileAsync(shardPath), $"data:{i + 1:D2}", true);
                if (shard.SizeBytes <= targetShardSizeBytes)
                {
                    shards.Add(await ReuseExistingImmutableShardAsync(syncRoot, shard));
                }
                else
                {
                    File.Delete(shardPath);
                    await AddTableSplitShardsAsync(shards, runtimePath, syncRoot, snapshotId, i + 1, tableGroup, targetShardSizeBytes, i == 0);
                }
            }
            else if (File.Exists(shardPath))
            {
                File.Delete(shardPath);
            }
        }

        return shards;
    }

    private static async Task AddTableSplitShardsAsync(List<SnapshotShard> shards, string runtimePath, string syncRoot, string snapshotId, int groupOrdinal, IReadOnlyList<string> tableGroup, long targetShardSizeBytes, bool forceFirst)
    {
        var addedAny = false;
        for (var tableIndex = 0; tableIndex < tableGroup.Count; tableIndex++)
        {
            var table = tableGroup[tableIndex];
            var rowCount = await CountRowsAsync(runtimePath, table, null);
            if (rowCount == 0 && !(forceFirst && !addedAny))
                continue;

            var tableShardId = $"{snapshotId}_data_{groupOrdinal:D2}_{tableIndex + 1:D2}";
            var tableShard = await CreatePreparedDataShardAsync(runtimePath, syncRoot, tableShardId, $"data:{groupOrdinal:D2}:{tableIndex + 1:D2}", [table], null);
            if (tableShard.SizeBytes <= targetShardSizeBytes || rowCount <= 1)
            {
                shards.Add(await ReuseExistingImmutableShardAsync(syncRoot, tableShard));
                addedAny = true;
                continue;
            }

            File.Delete(Path.Combine(syncRoot, tableShard.FileName));
            await AddRowSplitShardsAsync(shards, runtimePath, syncRoot, snapshotId, groupOrdinal, tableIndex + 1, table, rowCount, tableShard.SizeBytes, targetShardSizeBytes);
            addedAny = true;
        }
    }

    private static async Task AddRowSplitShardsAsync(List<SnapshotShard> shards, string runtimePath, string syncRoot, string snapshotId, int groupOrdinal, int tableOrdinal, string table, long rowCount, long tableShardSizeBytes, long targetShardSizeBytes)
    {
        var chunkCount = Math.Min(rowCount, Math.Max(2, (long)Math.Ceiling(tableShardSizeBytes / (double)targetShardSizeBytes)));
        while (true)
        {
            var ranges = await CreateRowIdRangesAsync(runtimePath, table, chunkCount);
            var created = new List<SnapshotShard>();
            var needsMoreSplitting = false;
            for (var i = 0; i < ranges.Count; i++)
            {
                var shardId = $"{snapshotId}_data_{groupOrdinal:D2}_{tableOrdinal:D2}_{i + 1:D4}";
                var shard = await CreatePreparedDataShardAsync(runtimePath, syncRoot, shardId, $"data:{groupOrdinal:D2}:{tableOrdinal:D2}:{i + 1:D4}", [table], new Dictionary<string, RowIdRange> { [table] = ranges[i] });
                created.Add(shard);
                var chunkRows = await CountRowsAsync(runtimePath, table, ranges[i]);
                if (shard.SizeBytes > targetShardSizeBytes && chunkRows > 1 && chunkCount < rowCount)
                    needsMoreSplitting = true;
            }

            if (!needsMoreSplitting)
            {
                foreach (var shard in created)
                    shards.Add(await ReuseExistingImmutableShardAsync(syncRoot, shard));
                return;
            }

            foreach (var shard in created)
                File.Delete(Path.Combine(syncRoot, shard.FileName));
            var nextChunkCount = Math.Min(rowCount, chunkCount * 2);
            if (nextChunkCount == chunkCount)
            {
                foreach (var shard in created)
                    shards.Add(await ReuseExistingImmutableShardAsync(syncRoot, shard));
                return;
            }
            chunkCount = nextChunkCount;
        }
    }

    private static async Task<SnapshotShard> CreatePreparedDataShardAsync(string runtimePath, string syncRoot, string shardId, string kind, IReadOnlyCollection<string> includedTables, IReadOnlyDictionary<string, RowIdRange>? rowRanges)
    {
        var shardFile = $"{shardId}.sqlite";
        var shardPath = Path.Combine(syncRoot, "shards", shardFile);
        await BackupDatabaseAsync(runtimePath, shardPath);
        await PrepareDataShardAsync(shardPath, includedTables, rowRanges);
        return new SnapshotShard(shardId, Path.Combine("shards", shardFile), new FileInfo(shardPath).Length, await Blake3FileAsync(shardPath), kind, true);
    }

    private static async Task<SnapshotShard> ReuseExistingImmutableShardAsync(string syncRoot, SnapshotShard candidate)
    {
        var candidatePath = Path.GetFullPath(Path.Combine(syncRoot, candidate.FileName));
        var shardDirectory = Path.Combine(syncRoot, "shards");
        foreach (var existingPath in Directory.EnumerateFiles(shardDirectory, "*.sqlite", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var fullExistingPath = Path.GetFullPath(existingPath);
            if (Path.GetFileNameWithoutExtension(fullExistingPath).StartsWith("credentials_", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(fullExistingPath, candidatePath, StringComparison.OrdinalIgnoreCase))
                continue;
            var existing = new FileInfo(fullExistingPath);
            if (existing.Length != candidate.SizeBytes)
                continue;
            var existingHash = await Blake3FileAsync(fullExistingPath);
            if (!string.Equals(existingHash, candidate.Blake3, StringComparison.OrdinalIgnoreCase))
                continue;

            File.Delete(candidatePath);
            var fileName = Path.Combine("shards", Path.GetFileName(fullExistingPath));
            return candidate with { ShardId = Path.GetFileNameWithoutExtension(fullExistingPath), FileName = fileName };
        }

        return candidate;
    }

    private static async Task PrepareDataShardAsync(string shardPath, IReadOnlyCollection<string>? includedTables, IReadOnlyDictionary<string, RowIdRange>? rowRanges = null)
    {
        await using (var connection = new SqliteConnection(BuildConnectionString(shardPath, SqliteOpenMode.ReadWriteCreate)))
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync("pragma foreign_keys = off;");
            foreach (var table in DataTables)
            {
                if (includedTables is null || includedTables.Contains(table)) continue;
                if (await TableExistsAsync(connection, table))
                    await connection.ExecuteAsync($"delete from {table};");
            }
            if (rowRanges is not null)
            {
                foreach (var range in rowRanges)
                    if (await TableExistsAsync(connection, range.Key))
                        await connection.ExecuteAsync($"delete from {range.Key} where rowid < @MinRowId or rowid > @MaxRowId;", new { range.Value.MinRowId, range.Value.MaxRowId });
            }
            foreach (var table in CredentialTables)
            {
                if (await TableExistsAsync(connection, table))
                    await connection.ExecuteAsync($"delete from {table};");
            }
            foreach (var table in LocalOnlyTables)
            {
                if (await TableExistsAsync(connection, table))
                    await connection.ExecuteAsync($"delete from {table};");
            }
            if (await TableExistsAsync(connection, "search_units_fts"))
                await connection.ExecuteAsync("delete from search_units_fts;");
            await connection.ExecuteAsync("pragma foreign_keys = on;");
        }

        await RedactLocalFileLocationsAsync(shardPath);
    }

    private static async Task<bool> HasAnyRowsAsync(string shardPath, IReadOnlyList<string> tables)
    {
        await using var connection = new SqliteConnection(BuildConnectionString(shardPath, SqliteOpenMode.ReadOnly));
        await connection.OpenAsync();
        foreach (var table in tables)
        {
            if (await TableExistsAsync(connection, table) && await connection.ExecuteScalarAsync<int>($"select count(1) from {table} limit 1;") > 0)
                return true;
        }
        return false;
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string table)
        => await connection.ExecuteScalarAsync<int>("select count(1) from sqlite_master where type in ('table','view') and name = @Table;", new { Table = table }) > 0;

    private static async Task<long> CountRowsAsync(string databasePath, string table, RowIdRange? range)
    {
        await using var connection = new SqliteConnection(BuildConnectionString(databasePath, SqliteOpenMode.ReadOnly));
        await connection.OpenAsync();
        if (!await TableExistsAsync(connection, table))
            return 0;
        if (range is null)
            return await connection.ExecuteScalarAsync<long>($"select count(1) from {table};");
        return await connection.ExecuteScalarAsync<long>($"select count(1) from {table} where rowid >= @MinRowId and rowid <= @MaxRowId;", new { range.Value.MinRowId, range.Value.MaxRowId });
    }

    private static async Task<IReadOnlyList<RowIdRange>> CreateRowIdRangesAsync(string databasePath, string table, long chunkCount)
    {
        await using var connection = new SqliteConnection(BuildConnectionString(databasePath, SqliteOpenMode.ReadOnly));
        await connection.OpenAsync();
        var rowCount = await connection.ExecuteScalarAsync<long>($"select count(1) from {table};");
        if (rowCount == 0)
            return [];

        chunkCount = Math.Clamp(chunkCount, 1, rowCount);
        var rowsPerChunk = (long)Math.Ceiling(rowCount / (double)chunkCount);
        var starts = new List<long>();
        for (var offset = 0L; offset < rowCount; offset += rowsPerChunk)
        {
            var rowId = await connection.ExecuteScalarAsync<long>($"select rowid from {table} order by rowid limit 1 offset @Offset;", new { Offset = offset });
            starts.Add(rowId);
        }

        var ranges = new List<RowIdRange>();
        for (var i = 0; i < starts.Count; i++)
        {
            var max = i + 1 < starts.Count ? starts[i + 1] - 1 : long.MaxValue;
            ranges.Add(new RowIdRange(starts[i], max));
        }
        return ranges;
    }

    private readonly record struct RowIdRange(long MinRowId, long MaxRowId);

    private static async Task<SnapshotShard?> CreateCredentialShardAsync(string runtimePath, string syncRoot, string snapshotId)
    {
        await using var source = new SqliteConnection(BuildConnectionString(runtimePath, SqliteOpenMode.ReadOnly));
        await source.OpenAsync();
        var exists = await source.ExecuteScalarAsync<int>("select count(1) from sqlite_master where name = 'provider_credentials';");
        if (exists == 0) return null;
        var shardId = $"credentials_{snapshotId}"; var name = $"{shardId}.sqlite"; var path = Path.Combine(syncRoot, "shards", name);
        if (File.Exists(path)) File.Delete(path);
        await using (var target = new SqliteConnection(BuildConnectionString(path, SqliteOpenMode.ReadWriteCreate)))
        {
            await target.OpenAsync();
            await target.ExecuteAsync("create table provider_credentials (credential_id text primary key, library_id text, provider_id text, display_name text, secret_value text, status text, created_at text, updated_at text); create table provider_credential_bindings (binding_id text primary key, credential_id text, preset_id text null, provider_id text, status text, created_at text, updated_at text);");
            var creds = await source.QueryAsync<CredentialShardRow>("select credential_id as CredentialId,library_id as LibraryId,provider_id as ProviderId,display_name as DisplayName,secret_value as SecretValue,status as Status,created_at as CreatedAt,updated_at as UpdatedAt from provider_credentials;");
            foreach (var row in creds) await target.ExecuteAsync("insert into provider_credentials values (@CredentialId,@LibraryId,@ProviderId,@DisplayName,@SecretValue,@Status,@CreatedAt,@UpdatedAt);", row);
            var bindings = await source.QueryAsync<BindingShardRow>("select binding_id as BindingId,credential_id as CredentialId,preset_id as PresetId,provider_id as ProviderId,status as Status,created_at as CreatedAt,updated_at as UpdatedAt from provider_credential_bindings;");
            foreach (var row in bindings) await target.ExecuteAsync("insert into provider_credential_bindings values (@BindingId,@CredentialId,@PresetId,@ProviderId,@Status,@CreatedAt,@UpdatedAt);", row);
        }
        return new SnapshotShard(shardId, Path.Combine("shards", name), new FileInfo(path).Length, await Blake3FileAsync(path), "sensitive_mutable", false);
    }

    public static async Task<string> ReadLibraryIdAsync(string databasePath)
    {
        await using var connection = new SqliteConnection(BuildConnectionString(databasePath, SqliteOpenMode.ReadOnly));
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<string>("select library_id from library_metadata limit 1;")
               ?? throw new InvalidOperationException("Runtime database has no library metadata.");
    }

    private static string BuildConnectionString(string databasePath, SqliteOpenMode mode) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = mode,
            ForeignKeys = true,
            Pooling = false
        }.ToString();

    public static bool IsPathInside(string childPath, string parentPath)
    {
        var child = Path.GetFullPath(childPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var parent = Path.GetFullPath(parentPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return child.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<bool> VerifyShardAsync(string syncRoot, SnapshotShard shard)
    {
        var path = Path.Combine(syncRoot, shard.FileName);
        return File.Exists(path) && new FileInfo(path).Length == shard.SizeBytes && string.Equals(await Blake3FileAsync(path), shard.Blake3, StringComparison.OrdinalIgnoreCase);
    }

    public static Task<string> Blake3FileAsync(string path)
    {
        return Blake3Hash.ComputeFileAsync(path);
    }

    public static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return default;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: cancellationToken);
    }

    public static async Task WriteJsonAtomicAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var candidate = path + ".candidate";
        await using (var stream = File.Create(candidate))
        {
            await JsonSerializer.SerializeAsync(stream, value, new JsonSerializerOptions { WriteIndented = true }, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        if (File.Exists(path)) File.Replace(candidate, path, null);
        else File.Move(candidate, path);
    }
}

file sealed class CredentialShardRow { public string CredentialId { get; set; } = ""; public string LibraryId { get; set; } = ""; public string ProviderId { get; set; } = ""; public string DisplayName { get; set; } = ""; public string SecretValue { get; set; } = ""; public string Status { get; set; } = ""; public string CreatedAt { get; set; } = ""; public string UpdatedAt { get; set; } = ""; }
file sealed class BindingShardRow { public string BindingId { get; set; } = ""; public string CredentialId { get; set; } = ""; public string? PresetId { get; set; } public string ProviderId { get; set; } = ""; public string Status { get; set; } = ""; public string CreatedAt { get; set; } = ""; public string UpdatedAt { get; set; } = ""; }

public sealed class SnapshotImporter : ISnapshotImporter
{
    public async Task<Result<SnapshotValidationResult>> ValidateSnapshotAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        try
        {
            var manifest = await SnapshotPublisher.ReadJsonAsync<SnapshotManifest>(manifestPath, cancellationToken);
            var errors = new List<string>();
            if (manifest is null)
            {
                return Result<SnapshotValidationResult>.Success(new SnapshotValidationResult(false, null, new[] { "Manifest could not be read." }));
            }
            if (manifest.ManifestVersion != 1) errors.Add("Unsupported manifest version.");
            if (string.IsNullOrWhiteSpace(manifest.LibraryId)) errors.Add("Manifest library_id is required.");
            var syncRoot = Directory.GetParent(Directory.GetParent(Path.GetFullPath(manifestPath))!.FullName)!.FullName;
            foreach (var shard in manifest.Shards.Concat(manifest.SensitiveMutableShards))
            {
                var path = Path.Combine(syncRoot, shard.FileName);
                if (!File.Exists(path)) errors.Add($"Shard missing: {shard.FileName}");
                else
                {
                    if (new FileInfo(path).Length != shard.SizeBytes) errors.Add($"Shard size mismatch: {shard.FileName}");
                    if (!string.Equals(await SnapshotPublisher.Blake3FileAsync(path), shard.Blake3, StringComparison.OrdinalIgnoreCase)) errors.Add($"Shard hash mismatch: {shard.FileName}");
                }
            }
            return Result<SnapshotValidationResult>.Success(new SnapshotValidationResult(errors.Count == 0, manifest, errors));
        }
        catch (Exception ex)
        {
            return Result<SnapshotValidationResult>.Success(new SnapshotValidationResult(false, null, new[] { $"Manifest validation failed: {ex.Message}" }));
        }
    }

    public async Task<Result<SnapshotImportResult>> ImportSnapshotToStagingAsync(SnapshotImportRequest request, CancellationToken cancellationToken = default)
    {
        var validation = await ValidateSnapshotAsync(request.ManifestPath, cancellationToken);
        if (validation.IsFailure) return Result<SnapshotImportResult>.Failure(validation.ErrorCode!, validation.ErrorMessage!);
        if (!validation.Value.IsValid || validation.Value.Manifest is null)
        {
            return Result<SnapshotImportResult>.Success(new SnapshotImportResult("", default, null, false, false, false, validation.Value.Errors));
        }

        var manifest = validation.Value.Manifest;
        var libraryId = LibraryId.Parse(manifest.LibraryId);
        var warnings = new List<string>();
        var matches = request.ExpectedLibraryId is null || request.ExpectedLibraryId.Value == libraryId;
        if (!matches)
        {
            warnings.Add("Manifest library does not match expected library.");
            return Result<SnapshotImportResult>.Success(new SnapshotImportResult(manifest.SnapshotId, libraryId, null, false, true, false, warnings));
        }
        if (request.CurrentRuntimeDatabasePath is not null && File.Exists(request.CurrentRuntimeDatabasePath))
        {
            var localLibrary = await SnapshotPublisher.ReadLibraryIdAsync(request.CurrentRuntimeDatabasePath);
            if (!string.Equals(localLibrary, manifest.LibraryId, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add("Manifest library does not match current runtime database.");
                return Result<SnapshotImportResult>.Success(new SnapshotImportResult(manifest.SnapshotId, libraryId, null, false, true, false, warnings));
            }
        }

        Directory.CreateDirectory(request.StagingRoot);
        var syncRoot = Directory.GetParent(Directory.GetParent(Path.GetFullPath(request.ManifestPath))!.FullName)!.FullName;
        var firstShard = manifest.Shards.First();
        var stagingPath = Path.Combine(request.StagingRoot, $"{manifest.SnapshotId}.staging.sqlite");
        File.Copy(Path.Combine(syncRoot, firstShard.FileName), stagingPath, overwrite: true);
        foreach (var shard in manifest.Shards.Skip(1))
        {
            await MergeDataShardIntoStagingAsync(stagingPath, Path.Combine(syncRoot, shard.FileName), cancellationToken);
        }
        foreach (var shard in manifest.SensitiveMutableShards)
        {
            File.Copy(Path.Combine(syncRoot, shard.FileName), Path.Combine(request.StagingRoot, Path.GetFileName(shard.FileName)), overwrite: true);
        }
        return Result<SnapshotImportResult>.Success(new SnapshotImportResult(manifest.SnapshotId, libraryId, stagingPath, true, true, false, warnings));
    }

    public async Task<Result<SnapshotBranchDetectionResult>> DetectBranchAsync(string syncRoot, string localParentSnapshotId, CancellationToken cancellationToken = default)
    {
        var currentPath = Path.Combine(syncRoot, "current.json");
        var current = await SnapshotPublisher.ReadJsonAsync<SnapshotCurrentPointer>(currentPath, cancellationToken);
        if (current is null)
        {
            return Result<SnapshotBranchDetectionResult>.Success(new SnapshotBranchDetectionResult(false, null, localParentSnapshotId));
        }
        return Result<SnapshotBranchDetectionResult>.Success(new SnapshotBranchDetectionResult(current.SnapshotId != localParentSnapshotId, current.SnapshotId, localParentSnapshotId));
    }

    private static async Task MergeDataShardIntoStagingAsync(string stagingPath, string shardPath, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = stagingPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, ForeignKeys = false }.ToString());
        await connection.OpenAsync(cancellationToken);
        await connection.ExecuteAsync("attach database @Path as shard;", new { Path = shardPath });
        await connection.ExecuteAsync("pragma foreign_keys = off;");
        foreach (var table in MergeTables)
        {
            if (!await TableExistsAsync(connection, table, "main") || !await TableExistsAsync(connection, table, "shard"))
            {
                continue;
            }
            await connection.ExecuteAsync($"insert or ignore into main.{table} select * from shard.{table};");
        }
        await connection.ExecuteAsync("detach database shard;");
        await connection.ExecuteAsync("pragma foreign_keys = on;");
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string table, string schema)
        => await connection.ExecuteScalarAsync<int>($"select count(1) from {schema}.sqlite_master where type in ('table','view') and name = @Table;", new { Table = table }) > 0;

    private static readonly string[] MergeTables =
    [
        "library_metadata",
        "items",
        "item_identifiers",
        "item_creators",
        "item_dates",
        "file_assets",
        "file_search_roots",
        "known_file_locations",
        "document_instances",
        "pages",
        "layout_revisions",
        "layout_nodes",
        "ocr_presets",
        "ocr_preset_versions",
        "ocr_runs",
        "ocr_page_results",
        "ocr_candidate_adoptions",
        "search_units",
        "search_index_status",
        "evidence_ref_records",
        "evidence_successors",
        "search_profiles",
        "search_rewrite_rules",
        "search_settings"
    ];
}
