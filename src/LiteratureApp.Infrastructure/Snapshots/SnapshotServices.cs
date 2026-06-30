using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using LiteratureApp.Core;
using LiteratureApp.Core.Ids;
using LiteratureApp.Core.Results;
using LiteratureApp.Core.Time;
using LiteratureApp.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace LiteratureApp.Infrastructure.Snapshots;

public sealed class SnapshotPublisher : ISnapshotPublisher
{
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
            var shardId = Guid.NewGuid().ToString("D");
            var shardFile = $"{shardId}.sqlite";
            var shardPath = Path.Combine(syncRoot, "shards", shardFile);

            await CheckpointAsync(runtimePath);
            await BackupDatabaseAsync(runtimePath, shardPath);
            await ClearLocalFtsCacheAsync(shardPath);
            await RedactCredentialsAsync(shardPath);
            await RedactLocalFileLocationsAsync(shardPath);

            var shard = new SnapshotShard(shardId, Path.Combine("shards", shardFile), new FileInfo(shardPath).Length, await Sha256FileAsync(shardPath), "data", true);
            if (!await VerifyShardAsync(syncRoot, shard))
            {
                return Result<SnapshotPublishResult>.Failure(AppErrorCodes.DatabaseError, "Shard hash verification failed after publish.");
            }

            var credentialShard = await CreateCredentialShardAsync(runtimePath, syncRoot, snapshotId);
            var manifest = new SnapshotManifest(1, libraryId, request.DeviceId, snapshotId, request.ParentSnapshotId, AppSchemaVersion.Current, generation, _clock.UtcNow.ToUniversalTime(), new[] { shard }, credentialShard is null ? Array.Empty<SnapshotShard>() : new[] { credentialShard }, await Sha256FileAsync(runtimePath), request.Notes);
            var manifestPath = Path.Combine(syncRoot, "manifests", $"{snapshotId}.json");
            await WriteJsonAtomicAsync(manifestPath, manifest, cancellationToken);
            var pointer = new SnapshotCurrentPointer(snapshotId, Path.Combine("manifests", $"{snapshotId}.json"), libraryId, generation, _clock.UtcNow.ToUniversalTime());
            await WriteJsonAtomicAsync(currentPath, pointer, cancellationToken);

            return Result<SnapshotPublishResult>.Success(new SnapshotPublishResult(snapshotId, manifestPath, currentPath, false, null, new[] { shard }, generation, "FTS rows are cleared in the snapshot shard; persisted search_units remain canonical."));
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
        return new SnapshotShard(shardId, Path.Combine("shards", name), new FileInfo(path).Length, await Sha256FileAsync(path), "sensitive_mutable", false);
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
        return File.Exists(path) && new FileInfo(path).Length == shard.SizeBytes && string.Equals(await Sha256FileAsync(path), shard.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<string> Sha256FileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
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
                    if (!string.Equals(await SnapshotPublisher.Sha256FileAsync(path), shard.Sha256, StringComparison.OrdinalIgnoreCase)) errors.Add($"Shard hash mismatch: {shard.FileName}");
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
}
