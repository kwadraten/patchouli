using System.Data.Common;
using System.Text.Json;
using Dapper;
using Patchouli.Core;
using Patchouli.Core.Ids;
using Patchouli.Core.Operations;
using Patchouli.Core.Results;
using Patchouli.Core.Settings;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Hashing;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Search;
using Microsoft.Data.Sqlite;

namespace Patchouli.Infrastructure.Snapshots;

public sealed class SnapshotPublisher : ISnapshotPublisher
{
    private const long DefaultTargetShardSizeBytes = 512L * 1024L * 1024L;

    private static readonly string[] DataTables =
    [
        "library_metadata",
        "collections",
        "items",
        "item_collections",
        "item_identifiers",
        "item_creators",
        "item_dates",
        "file_assets",
        "file_search_root_definitions",
        "known_file_locations",
        "document_instances",
        "pages",
        "document_tree_revisions",
        "document_boxes",
        "ocr_presets",
        "ocr_preset_versions",
        "ocr_runs",
        "ocr_page_results",
        "ocr_candidate_adoptions",
        "search_units",
        "search_index_status",
        "document_commits",
        "document_commit_pages",
        "search_profiles",
        "search_rewrite_rules",
        "search_settings",
        "library_setting_records"
    ];

    private static readonly HashSet<string> LocalSnapshotTables =
    [
        "search_units_fts",
        "fts_row_map",
        "fts_cache_state",
        "item_tag_memberships",
        "file_asset_payload_refs",
        "local_maintenance_state"
    ];

    private static readonly string[] SnapshotSchemaTables = ["schema_migrations"];

    private static readonly string[][] DataShardTableGroups =
    [
        [
            "library_metadata", "collections", "items", "item_collections", "item_identifiers", "item_creators",
            "item_dates", "file_assets",
            "file_search_root_definitions", "known_file_locations", "document_instances"
        ],
        ["pages", "document_tree_revisions", "document_boxes", "document_commits", "document_commit_pages"],
        ["ocr_presets", "ocr_preset_versions", "ocr_runs", "ocr_page_results", "ocr_candidate_adoptions"],
        ["search_units", "search_index_status"],
        ["search_profiles", "search_rewrite_rules", "search_settings", "library_setting_records"]
    ];

    private readonly IClock _clock;

    public SnapshotPublisher(IClock clock)
    {
        _clock = clock;
    }

    public async Task<Result<SnapshotPublishResult>> PublishSnapshotAsync(SnapshotPublishRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(request.RuntimeDatabasePath))
        {
            return Result<SnapshotPublishResult>.Failure(AppErrorCodes.NotFound, "Runtime database was not found.");
        }

        if (string.IsNullOrWhiteSpace(request.SyncRoot) || string.IsNullOrWhiteSpace(request.DeviceId))
        {
            return Result<SnapshotPublishResult>.Failure(AppErrorCodes.ValidationFailed,
                "Sync root and device id are required.");
        }

        string runtimePath = Path.GetFullPath(request.RuntimeDatabasePath);
        string syncRoot = Path.GetFullPath(request.SyncRoot);
        if (IsPathInside(runtimePath, syncRoot))
        {
            return Result<SnapshotPublishResult>.Failure(AppErrorCodes.ValidationFailed,
                "Runtime database must not be inside the sync root.");
        }

        Directory.CreateDirectory(Path.Combine(syncRoot, "manifests"));
        Directory.CreateDirectory(Path.Combine(syncRoot, "shards"));

        string currentPath = Path.Combine(syncRoot, "current.json");
        SnapshotCurrentPointer? current = await ReadJsonAsync<SnapshotCurrentPointer>(currentPath, cancellationToken);
        string? sourceSnapshotPath = null;

        try
        {
            await CheckpointAsync(runtimePath);
            sourceSnapshotPath = Path.Combine(Path.GetTempPath(), $"patchouli-snapshot-{Guid.NewGuid():N}.sqlite");
            await BackupDatabaseAsync(runtimePath, sourceSnapshotPath);
            await ValidateDatabaseSchemaAsync(sourceSnapshotPath);
            string libraryId = await ReadLibraryIdAsync(sourceSnapshotPath);
            long generation = current is null ? 1 : current.LogicalGeneration + 1;
            string snapshotId = Guid.NewGuid().ToString("D");

            IReadOnlyList<SnapshotShard> shards = await CreateDataShardsAsync(sourceSnapshotPath, syncRoot, snapshotId,
                NormalizeTargetShardSize(request.TargetShardSizeBytes),
                LibrarySettingCatalog.NormalizeSnapshotKeys(request.EnabledSettingKeys));
            foreach (SnapshotShard shard in shards)
            {
                if (!await VerifyShardAsync(syncRoot, shard))
                {
                    return Result<SnapshotPublishResult>.Failure(AppErrorCodes.DatabaseError,
                        "Shard hash verification failed after publish.");
                }
            }

            SnapshotManifest manifest = new(1, libraryId, request.DeviceId, snapshotId, request.ParentSnapshotId,
                AppSchemaVersion.Current, generation, _clock.UtcNow.ToUniversalTime(), shards,
                Array.Empty<SnapshotShard>(),
                await Blake3FileAsync(sourceSnapshotPath), request.Notes);
            string manifestPath = Path.Combine(syncRoot, "manifests", $"{snapshotId}.json");
            await WriteJsonAtomicAsync(manifestPath, manifest, cancellationToken);
            SnapshotCurrentPointer pointer = new(snapshotId, Path.Combine("manifests", $"{snapshotId}.json"), libraryId,
                generation, _clock.UtcNow.ToUniversalTime(), request.SyncRootId);
            await WriteJsonAtomicAsync(currentPath, pointer, cancellationToken);

            return Result<SnapshotPublishResult>.Success(new SnapshotPublishResult(snapshotId, manifestPath,
                currentPath, shards, generation,
                shards.Count > 1
                    ? "Runtime database exceeded the snapshot shard target; data was split into multiple immutable shards. FTS rows are cleared in data shards; persisted search_units remain canonical."
                    : "FTS rows are cleared in the snapshot shard; persisted search_units remain canonical."));
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.snapshot-services"))
        {
            return Result<SnapshotPublishResult>.Failure(AppErrorCodes.DatabaseError,
                $"Snapshot publish failed: {ex.Message}");
        }
        finally
        {
            if (sourceSnapshotPath is not null)
            {
                DeleteDatabaseFiles(sourceSnapshotPath);
            }
        }
    }

    public static async Task CheckpointAsync(string databasePath)
    {
        await using SqliteConnection connection =
            new(BuildConnectionString(databasePath, SqliteOpenMode.ReadWriteCreate));
        await connection.OpenAsync();
        await connection.ExecuteAsync("pragma wal_checkpoint(full);");
    }

    public static async Task BackupDatabaseAsync(string sourcePath, string targetPath)
    {
        if (File.Exists(targetPath))
        {
            File.Delete(targetPath);
        }

        await using SqliteConnection source = new(BuildConnectionString(sourcePath, SqliteOpenMode.ReadOnly));
        await using SqliteConnection target = new(BuildConnectionString(targetPath, SqliteOpenMode.ReadWriteCreate));
        await source.OpenAsync();
        await target.OpenAsync();
        source.BackupDatabase(target);
    }

    private static async Task CreateShardDatabaseAsync(
        string sourceSnapshotPath,
        string targetPath,
        IReadOnlyCollection<string>? includedTables,
        IReadOnlyDictionary<string, RowIdRange>? rowRanges)
    {
        if (File.Exists(targetPath))
        {
            File.Delete(targetPath);
        }

        await using SqliteConnection
            connection = new(BuildConnectionString(targetPath, SqliteOpenMode.ReadWriteCreate));
        await connection.OpenAsync();
        await connection.ExecuteAsync("pragma foreign_keys = off;");
        await connection.ExecuteAsync(
            "attach database @SourcePath as snapshot_source;",
            new { SourcePath = sourceSnapshotPath });

        await using DbTransaction transaction = await connection.BeginTransactionAsync();
        int applicationId = await connection.ExecuteScalarAsync<int>("pragma snapshot_source.application_id;");
        int userVersion = await connection.ExecuteScalarAsync<int>("pragma snapshot_source.user_version;");
        HashSet<string> shadowTableNames = (await connection.QueryAsync<string>(
                new CommandDefinition(
                    "select name from pragma_table_list where schema = 'snapshot_source' and type = 'shadow';",
                    transaction: transaction)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        SchemaObjectRow[] schemaObjects = (await connection.QueryAsync<SchemaObjectRow>(
            new CommandDefinition(
                """
                select type as Type, name as Name, sql as Sql
                from snapshot_source.sqlite_schema
                where sql is not null
                  and name not like 'sqlite_%'
                order by case type
                    when 'table' then 0
                    when 'index' then 1
                    when 'trigger' then 2
                    when 'view' then 3
                    else 4
                end, name;
                """,
                transaction: transaction))).ToArray();

        foreach (SchemaObjectRow schemaObject in schemaObjects.Where(item => item.Type == "table"))
        {
            // Creating an FTS5 virtual table creates its shadow tables. They appear in
            // sqlite_schema as ordinary tables, so skip their DDL to avoid collisions.
            if (shadowTableNames.Contains(schemaObject.Name))
            {
                continue;
            }

            await connection.ExecuteAsync(schemaObject.Sql, transaction: transaction);
        }

        IEnumerable<string> tablesToCopy = DataTables
            .Where(table => includedTables is null || table == "library_metadata" || includedTables.Contains(table))
            .Concat(SnapshotSchemaTables)
            .Where(table => !LocalSnapshotTables.Contains(table))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string table in tablesToCopy)
        {
            if (!schemaObjects.Any(item => item.Type == "table" &&
                                           string.Equals(item.Name, table, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            string quotedTable = QuoteIdentifier(table);
            string tableLiteral = table.Replace("'", "''", StringComparison.Ordinal);
            ShardTableColumn[] columns = (await connection.QueryAsync<ShardTableColumn>(
                    new CommandDefinition(
                        $"pragma snapshot_source.table_xinfo('{tableLiteral}');",
                        transaction: transaction)))
                .Where(column => column.Hidden == 0)
                .ToArray();
            if (columns.Length == 0)
            {
                continue;
            }

            string columnList = string.Join(", ", columns.Select(column => QuoteIdentifier(column.Name)));
            RowIdRange? requestedRange = null;
            if (rowRanges is not null && rowRanges.TryGetValue(table, out RowIdRange range))
            {
                requestedRange = range;
            }

            string rangeClause = requestedRange is null
                ? string.Empty
                : "where source_rows.rowid >= @MinRowId and source_rows.rowid <= @MaxRowId order by source_rows.rowid";
            object? parameters = requestedRange is { } selectedRange
                ? new { selectedRange.MinRowId, selectedRange.MaxRowId }
                : null;
            string sourceRows = rangeClause.Length == 0
                ? $"select {columnList} from snapshot_source.{quotedTable};"
                : $"select {columnList} from snapshot_source.{quotedTable} as source_rows {rangeClause};";
            await connection.ExecuteAsync(new CommandDefinition(
                $"insert into main.{quotedTable} ({columnList}) {sourceRows}",
                parameters,
                transaction,
                cancellationToken: default));
        }

        foreach (SchemaObjectRow schemaObject in schemaObjects.Where(item => item.Type != "table"))
        {
            await connection.ExecuteAsync(schemaObject.Sql, transaction: transaction);
        }

        await connection.ExecuteAsync($"pragma main.application_id = {applicationId};", transaction: transaction);
        await connection.ExecuteAsync($"pragma main.user_version = {userVersion};", transaction: transaction);
        await transaction.CommitAsync();
        await connection.ExecuteAsync("detach database snapshot_source;");
        await connection.ExecuteAsync("pragma foreign_keys = on;");
    }

    private static string QuoteIdentifier(string identifier)
    {
        return $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static void DeleteDatabaseFiles(string databasePath)
    {
        foreach (string path in new[]
                 {
                     databasePath,
                     $"{databasePath}-wal",
                     $"{databasePath}-shm",
                     $"{databasePath}-journal"
                 })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    public static async Task ClearLocalFtsCacheAsync(string shardPath)
    {
        await using SqliteConnection connection = new(BuildConnectionString(shardPath, SqliteOpenMode.ReadWriteCreate));
        await connection.OpenAsync();
        int exists =
            await connection.ExecuteScalarAsync<int>(
                "select count(1) from sqlite_master where name = 'search_units_fts';");
        if (exists > 0)
        {
            await connection.ExecuteAsync("delete from search_units_fts;");
        }
    }

    public static async Task RedactLocalFileLocationsAsync(string shardPath)
    {
        await using SqliteConnection connection = new(BuildConnectionString(shardPath, SqliteOpenMode.ReadWriteCreate));
        await connection.OpenAsync();
        if (await connection.ExecuteScalarAsync<int>("select count(1) from sqlite_master where name = 'file_assets';") >
            0)
        {
            await connection.ExecuteAsync("update file_assets set original_path = '[redacted]';");
        }

        if (await connection.ExecuteScalarAsync<int>(
                "select count(1) from sqlite_master where name = 'known_file_locations';") > 0)
        {
            await connection.ExecuteAsync("delete from known_file_locations;");
        }

        if (await connection.ExecuteScalarAsync<int>(
                "select count(1) from sqlite_master where name = 'file_search_root_bindings';") > 0)
        {
            await connection.ExecuteAsync("delete from file_search_root_bindings;");
        }

        if (await connection.ExecuteScalarAsync<int>(
                "select count(1) from sqlite_master where name = 'ocr_preset_versions';") > 0)
        {
            await connection.ExecuteAsync(
                "update ocr_preset_versions set model_path = '[redacted]' where model_path is not null;");
        }

        await connection.ExecuteAsync("vacuum;");
    }

    private static long NormalizeTargetShardSize(long targetShardSizeBytes)
    {
        return targetShardSizeBytes <= 0 ? DefaultTargetShardSizeBytes : targetShardSizeBytes;
    }

    private static async Task<IReadOnlyList<SnapshotShard>> CreateDataShardsAsync(
        string sourceSnapshotPath,
        string syncRoot,
        string snapshotId,
        long targetShardSizeBytes,
        IReadOnlyCollection<string> enabledSettingKeys)
    {
        if (new FileInfo(sourceSnapshotPath).Length <= targetShardSizeBytes)
        {
            string shardId = Guid.NewGuid().ToString("D");
            SnapshotShard shard = await CreatePreparedDataShardAsync(
                sourceSnapshotPath,
                syncRoot,
                shardId,
                "data",
                null,
                null,
                enabledSettingKeys);
            return [await ReuseExistingImmutableShardAsync(syncRoot, shard)];
        }

        List<SnapshotShard> shards = new();
        for (int i = 0; i < DataShardTableGroups.Length; i++)
        {
            string[] tableGroup = DataShardTableGroups[i];
            string shardId = $"{snapshotId}_data_{i + 1:D2}";
            SnapshotShard shard = await CreatePreparedDataShardAsync(
                sourceSnapshotPath,
                syncRoot,
                shardId,
                $"data:{i + 1:D2}",
                tableGroup,
                null,
                enabledSettingKeys);
            string shardPath = Path.Combine(syncRoot, shard.FileName);
            if (await HasAnyRowsAsync(shardPath, tableGroup) || i == 0)
            {
                if (shard.SizeBytes <= targetShardSizeBytes)
                {
                    shards.Add(await ReuseExistingImmutableShardAsync(syncRoot, shard));
                }
                else
                {
                    File.Delete(shardPath);
                    await AddTableSplitShardsAsync(shards, sourceSnapshotPath, syncRoot, snapshotId, i + 1, tableGroup,
                        targetShardSizeBytes, i == 0, enabledSettingKeys);
                }
            }
            else if (File.Exists(shardPath))
            {
                File.Delete(shardPath);
            }
        }

        return shards;
    }

    private static async Task AddTableSplitShardsAsync(List<SnapshotShard> shards, string sourceSnapshotPath,
        string syncRoot,
        string snapshotId, int groupOrdinal, IReadOnlyList<string> tableGroup, long targetShardSizeBytes,
        bool forceFirst, IReadOnlyCollection<string> enabledSettingKeys)
    {
        bool addedAny = false;
        for (int tableIndex = 0; tableIndex < tableGroup.Count; tableIndex++)
        {
            string table = tableGroup[tableIndex];
            long rowCount = await CountRowsAsync(sourceSnapshotPath, table, null);
            if (rowCount == 0 && !(forceFirst && !addedAny))
            {
                continue;
            }

            string tableShardId = $"{snapshotId}_data_{groupOrdinal:D2}_{tableIndex + 1:D2}";
            SnapshotShard tableShard = await CreatePreparedDataShardAsync(sourceSnapshotPath, syncRoot, tableShardId,
                $"data:{groupOrdinal:D2}:{tableIndex + 1:D2}", [table], null, enabledSettingKeys);
            if (tableShard.SizeBytes <= targetShardSizeBytes || rowCount <= 1)
            {
                shards.Add(await ReuseExistingImmutableShardAsync(syncRoot, tableShard));
                addedAny = true;
                continue;
            }

            File.Delete(Path.Combine(syncRoot, tableShard.FileName));
            await AddRowSplitShardsAsync(shards, sourceSnapshotPath, syncRoot, snapshotId, groupOrdinal, tableIndex + 1,
                table,
                rowCount, tableShard.SizeBytes, targetShardSizeBytes, enabledSettingKeys);
            addedAny = true;
        }
    }

    private static async Task AddRowSplitShardsAsync(List<SnapshotShard> shards, string sourceSnapshotPath,
        string syncRoot,
        string snapshotId, int groupOrdinal, int tableOrdinal, string table, long rowCount, long tableShardSizeBytes,
        long targetShardSizeBytes, IReadOnlyCollection<string> enabledSettingKeys)
    {
        long chunkCount = Math.Min(rowCount,
            Math.Max(2, (long)Math.Ceiling(tableShardSizeBytes / (double)targetShardSizeBytes)));
        while (true)
        {
            IReadOnlyList<RowIdRange> ranges = await CreateRowIdRangesAsync(sourceSnapshotPath, table, chunkCount,
                rowCount);
            List<SnapshotShard> created = new();
            bool needsMoreSplitting = false;
            for (int i = 0; i < ranges.Count; i++)
            {
                string shardId = $"{snapshotId}_data_{groupOrdinal:D2}_{tableOrdinal:D2}_{i + 1:D4}";
                SnapshotShard shard = await CreatePreparedDataShardAsync(sourceSnapshotPath, syncRoot, shardId,
                    $"data:{groupOrdinal:D2}:{tableOrdinal:D2}:{i + 1:D4}", [table],
                    new Dictionary<string, RowIdRange> { [table] = ranges[i] }, enabledSettingKeys);
                created.Add(shard);
                long chunkRows = await CountRowsAsync(sourceSnapshotPath, table, ranges[i]);
                if (shard.SizeBytes > targetShardSizeBytes && chunkRows > 1 && chunkCount < rowCount)
                {
                    needsMoreSplitting = true;
                }
            }

            if (!needsMoreSplitting)
            {
                foreach (SnapshotShard shard in created)
                {
                    shards.Add(await ReuseExistingImmutableShardAsync(syncRoot, shard));
                }

                return;
            }

            foreach (SnapshotShard shard in created)
            {
                File.Delete(Path.Combine(syncRoot, shard.FileName));
            }

            long nextChunkCount = Math.Min(rowCount, chunkCount * 2);
            if (nextChunkCount == chunkCount)
            {
                foreach (SnapshotShard shard in created)
                {
                    shards.Add(await ReuseExistingImmutableShardAsync(syncRoot, shard));
                }

                return;
            }

            chunkCount = nextChunkCount;
        }
    }

    private static async Task<SnapshotShard> CreatePreparedDataShardAsync(string sourceSnapshotPath, string syncRoot,
        string shardId, string kind, IReadOnlyCollection<string>? includedTables,
        IReadOnlyDictionary<string, RowIdRange>? rowRanges, IReadOnlyCollection<string> enabledSettingKeys)
    {
        string shardFile = $"{shardId}.sqlite";
        string shardPath = Path.Combine(syncRoot, "shards", shardFile);
        await CreateShardDatabaseAsync(sourceSnapshotPath, shardPath, includedTables, rowRanges);
        await PrepareDataShardAsync(shardPath, includedTables, enabledSettingKeys);
        return new SnapshotShard(shardId, Path.Combine("shards", shardFile), new FileInfo(shardPath).Length,
            await Blake3FileAsync(shardPath), kind, true);
    }

    private static async Task<SnapshotShard> ReuseExistingImmutableShardAsync(string syncRoot, SnapshotShard candidate)
    {
        string candidatePath = Path.GetFullPath(Path.Combine(syncRoot, candidate.FileName));
        string shardDirectory = Path.Combine(syncRoot, "shards");
        foreach (string existingPath in Directory
                     .EnumerateFiles(shardDirectory, "*.sqlite", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            string fullExistingPath = Path.GetFullPath(existingPath);
            if (Path.GetFileNameWithoutExtension(fullExistingPath)
                .StartsWith("credentials_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(fullExistingPath, candidatePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            FileInfo existing = new(fullExistingPath);
            if (existing.Length != candidate.SizeBytes)
            {
                continue;
            }

            string existingHash = await Blake3FileAsync(fullExistingPath);
            if (!string.Equals(existingHash, candidate.Blake3, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            File.Delete(candidatePath);
            string fileName = Path.Combine("shards", Path.GetFileName(fullExistingPath));
            return candidate with { ShardId = Path.GetFileNameWithoutExtension(fullExistingPath), FileName = fileName };
        }

        return candidate;
    }

    private static async Task PrepareDataShardAsync(
        string shardPath,
        IReadOnlyCollection<string>? includedTables,
        IReadOnlyCollection<string>? enabledSettingKeys = null)
    {
        await using (SqliteConnection
                     connection = new(BuildConnectionString(shardPath, SqliteOpenMode.ReadWriteCreate)))
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync("pragma foreign_keys = off;");
            foreach (string table in DataTables)
            {
                // Keep the epoch marker in every shard so each independently verified shard is self-describing.
                if (table == "library_metadata" || includedTables is null || includedTables.Contains(table))
                {
                    continue;
                }

                if (await TableExistsAsync(connection, table))
                {
                    await connection.ExecuteAsync($"delete from {table};");
                }
            }

            if (await TableExistsAsync(connection, "search_units_fts"))
            {
                await connection.ExecuteAsync("delete from search_units_fts;");
            }

            // Snapshot shards must only contain committed revisions. Working revisions and legacy
            // status rows ('staging', 'draft', 'discarded') are local state and are not synced.
            if (await TableExistsAsync(connection, "document_tree_revisions"))
            {
                await connection.ExecuteAsync("delete from document_tree_revisions where status != 'committed';");
            }

            // Only cascade to boxes when the shard actually carries revision rows. In per-table shards
            // the document_tree_revisions table is emptied by the table-group filter above, so we must
            // not treat its empty state as "delete all boxes".
            if (await TableExistsAsync(connection, "document_boxes") &&
                (includedTables is null || includedTables.Contains("document_tree_revisions")))
            {
                await connection.ExecuteAsync(
                    "delete from document_boxes where tree_revision_id not in (select tree_revision_id from document_tree_revisions);");
            }

            await connection.ExecuteAsync("pragma foreign_keys = on;");
        }

        await RedactLocalFileLocationsAsync(shardPath);
        await FilterLibrarySettingRecordsAsync(shardPath, enabledSettingKeys ?? []);
    }

    private static async Task FilterLibrarySettingRecordsAsync(
        string shardPath,
        IReadOnlyCollection<string> enabledSettingKeys)
    {
        await using SqliteConnection connection =
            new(BuildConnectionString(shardPath, SqliteOpenMode.ReadWriteCreate));
        await connection.OpenAsync();
        if (!await TableExistsAsync(connection, "library_setting_records"))
        {
            return;
        }

        string[] allowed = LibrarySettingCatalog.NormalizeSnapshotKeys(enabledSettingKeys).ToArray();
        if (allowed.Length == 0)
        {
            await connection.ExecuteAsync("delete from library_setting_records;");
            return;
        }

        await connection.ExecuteAsync(
            "delete from library_setting_records where setting_key not in @Allowed;",
            new { Allowed = allowed });

        SettingRecordRow[] records = (await connection.QueryAsync<SettingRecordRow>(
            """
            select setting_key as SettingKey, schema_version as SchemaVersion, value_json as Value,
                   revision as Revision, updated_at as UpdatedAt, updated_by_device_id as UpdatedByDeviceId,
                   merge_policy as MergePolicy
            from library_setting_records
            order by setting_key;
            """)).ToArray();
        foreach (SettingRecordRow row in records)
        {
            Result validation = LibrarySettingCatalog.ValidateRecord(row.ToRecord());
            if (validation.IsFailure)
            {
                throw new InvalidDataException(
                    $"Library setting '{row.SettingKey}' cannot be published: {validation.ErrorMessage}");
            }
        }
    }

    private static async Task<bool> HasAnyRowsAsync(string shardPath, IReadOnlyList<string> tables)
    {
        await using SqliteConnection connection = new(BuildConnectionString(shardPath, SqliteOpenMode.ReadOnly));
        await connection.OpenAsync();
        foreach (string table in tables)
        {
            if (await TableExistsAsync(connection, table) &&
                await connection.ExecuteScalarAsync<int>($"select count(1) from {table} limit 1;") > 0)
            {
                return true;
            }
        }

        return false;
    }

    public static async Task<bool> TableExistsAsync(SqliteConnection connection, string table)
    {
        return await connection.ExecuteScalarAsync<int>(
            "select count(1) from sqlite_master where type in ('table','view') and name = @Table;",
            new { Table = table }) > 0;
    }

    private static async Task<long> CountRowsAsync(string databasePath, string table, RowIdRange? range)
    {
        await using SqliteConnection connection = new(BuildConnectionString(databasePath, SqliteOpenMode.ReadOnly));
        await connection.OpenAsync();
        if (!await TableExistsAsync(connection, table))
        {
            return 0;
        }

        if (range is null)
        {
            return await connection.ExecuteScalarAsync<long>($"select count(1) from {table};");
        }

        return await connection.ExecuteScalarAsync<long>(
            $"select count(1) from {table} where rowid >= @MinRowId and rowid <= @MaxRowId;",
            new { range.Value.MinRowId, range.Value.MaxRowId });
    }

    private static async Task<IReadOnlyList<RowIdRange>> CreateRowIdRangesAsync(
        string databasePath,
        string table,
        long chunkCount,
        long rowCount)
    {
        await using SqliteConnection connection = new(BuildConnectionString(databasePath, SqliteOpenMode.ReadOnly));
        await connection.OpenAsync();
        if (rowCount == 0)
        {
            return [];
        }

        chunkCount = Math.Clamp(chunkCount, 1, rowCount);
        long rowsPerChunk = (long)Math.Ceiling(rowCount / (double)chunkCount);
        List<RowIdRange> ranges = new();
        long? lastRowId = null;
        long remainingRows = rowCount;
        while (remainingRows > 0)
        {
            long take = Math.Min(rowsPerChunk, remainingRows);
            string rangeClause = lastRowId.HasValue ? "where rowid > @LastRowId" : string.Empty;
            long[] rowIds = (await connection.QueryAsync<long>(
                    $"select rowid from {QuoteIdentifier(table)} {rangeClause} order by rowid limit @Take;",
                    new { LastRowId = lastRowId, Take = take }))
                .ToArray();
            if (rowIds.Length == 0)
            {
                break;
            }

            ranges.Add(new RowIdRange(rowIds[0], rowIds[^1]));
            lastRowId = rowIds[^1];
            remainingRows -= rowIds.Length;
        }

        return ranges;
    }

    private readonly record struct RowIdRange(long MinRowId, long MaxRowId);

    private sealed class SchemaObjectRow
    {
        public string Type { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Sql { get; set; } = string.Empty;
    }

    private sealed class ShardTableColumn
    {
        public string Name { get; set; } = string.Empty;
        public int Hidden { get; set; }
    }

    private sealed class SettingRecordRow
    {
        public string SettingKey { get; set; } = "";
        public int SchemaVersion { get; set; }
        public string Value { get; set; } = "";
        public long Revision { get; set; }
        public string UpdatedAt { get; set; } = "";
        public string UpdatedByDeviceId { get; set; } = "";
        public string MergePolicy { get; set; } = "";

        public SettingRecord ToRecord()
        {
            return new SettingRecord(SettingKey, SchemaVersion, Value, Revision, DateTimeOffset.Parse(UpdatedAt),
                UpdatedByDeviceId, MergePolicy);
        }
    }

    public static async Task<string> ReadLibraryIdAsync(string databasePath)
    {
        await using SqliteConnection connection = new(BuildConnectionString(databasePath, SqliteOpenMode.ReadOnly));
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<string>("select library_id from library_metadata limit 1;")
               ?? throw new InvalidOperationException("Runtime database has no library metadata.");
    }

    public static string BuildConnectionString(string databasePath, SqliteOpenMode mode)
    {
        return new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = mode,
            ForeignKeys = true,
            Pooling = false
        }.ToString();
    }

    public static bool IsPathInside(string childPath, string parentPath)
    {
        string child = Path.GetFullPath(childPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string parent = Path.GetFullPath(parentPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return child.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<bool> VerifyShardAsync(string syncRoot, SnapshotShard shard)
    {
        string path = Path.Combine(syncRoot, shard.FileName);
        return File.Exists(path) && new FileInfo(path).Length == shard.SizeBytes &&
               string.Equals(await Blake3FileAsync(path), shard.Blake3, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task ValidateDatabaseSchemaAsync(string path, bool checkForeignKeys = true)
    {
        await using SqliteConnection connection = new(BuildConnectionString(path, SqliteOpenMode.ReadOnly));
        await connection.OpenAsync();
        IReadOnlyList<string> errors = await SchemaInspector.InspectAsync(connection, checkForeignKeys);
        if (errors.Count > 0)
        {
            throw new InvalidDataException(string.Join(" ", errors));
        }
    }

    public static Task<string> Blake3FileAsync(string path)
    {
        return Blake3Hash.ComputeFileAsync(path);
    }

    public static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        await using FileStream stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: cancellationToken);
    }

    public static async Task WriteJsonAtomicAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string candidate = path + ".candidate";
        await using (FileStream stream = File.Create(candidate))
        {
            await JsonSerializer.SerializeAsync(stream, value, new JsonSerializerOptions { WriteIndented = true },
                cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        if (File.Exists(path))
        {
            File.Replace(candidate, path, null);
        }
        else
        {
            File.Move(candidate, path);
        }
    }
}

public sealed class SnapshotImporter : ISnapshotImporter
{
    private readonly IBlockingOperationService? _blockingOperations;

    public SnapshotImporter(IBlockingOperationService? blockingOperations = null)
    {
        _blockingOperations = blockingOperations;
    }

    public async Task<Result<SnapshotValidationResult>> ValidateSnapshotAsync(string manifestPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            SnapshotManifest? manifest =
                await SnapshotPublisher.ReadJsonAsync<SnapshotManifest>(manifestPath, cancellationToken);
            List<string> errors = new();
            if (manifest is null)
            {
                return Result<SnapshotValidationResult>.Success(
                    new SnapshotValidationResult(false, null, new[] { "Manifest could not be read." }));
            }

            if (manifest.ManifestVersion != 1)
            {
                errors.Add("Unsupported manifest version.");
            }

            if (manifest.SchemaVersion != AppSchemaVersion.Current)
            {
                errors.Add("Unsupported snapshot schema version.");
            }

            if (string.IsNullOrWhiteSpace(manifest.LibraryId))
            {
                errors.Add("Manifest library_id is required.");
            }

            if (string.IsNullOrWhiteSpace(manifest.SnapshotId))
            {
                errors.Add("Manifest snapshot_id is required.");
            }

            if (manifest.Shards.Count == 0)
            {
                errors.Add("Manifest must contain at least one data shard.");
            }

            if (!TryResolveSnapshotRoot(manifestPath, out string syncRoot))
            {
                errors.Add("Manifest must be located directly inside a manifests directory.");
                return Result<SnapshotValidationResult>.Success(new SnapshotValidationResult(false, manifest, errors));
            }

            foreach (SnapshotShard shard in manifest.Shards.Concat(manifest.SensitiveMutableShards))
            {
                if (!TryResolveShardPath(syncRoot, shard.FileName, out string path))
                {
                    errors.Add($"Shard path escapes snapshot root: {shard.FileName}");
                    continue;
                }

                if (!File.Exists(path))
                {
                    errors.Add($"Shard missing: {shard.FileName}");
                }
                else
                {
                    if (new FileInfo(path).Length != shard.SizeBytes)
                    {
                        errors.Add($"Shard size mismatch: {shard.FileName}");
                    }

                    if (!string.Equals(await SnapshotPublisher.Blake3FileAsync(path), shard.Blake3,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add($"Shard hash mismatch: {shard.FileName}");
                    }

                    try
                    {
                        await SnapshotPublisher.ValidateDatabaseSchemaAsync(path, false);
                        IReadOnlyList<string> settingErrors =
                            await ValidateSnapshotSettingRecordsAsync(path, cancellationToken);
                        errors.AddRange(settingErrors.Select(error =>
                            $"Shard setting record invalid: {shard.FileName}: {error}"));
                    }
                    catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                                          "infrastructure.snapshot-services"))
                    {
                        errors.Add($"Shard schema invalid: {shard.FileName}: {exception.Message}");
                    }
                }
            }

            return Result<SnapshotValidationResult>.Success(new SnapshotValidationResult(errors.Count == 0, manifest,
                errors));
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.snapshot-services"))
        {
            return Result<SnapshotValidationResult>.Success(new SnapshotValidationResult(false, null,
                new[] { $"Manifest validation failed: {ex.Message}" }));
        }
    }

    private static async Task<IReadOnlyList<string>> ValidateSnapshotSettingRecordsAsync(
        string shardPath,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection =
            new(SnapshotPublisher.BuildConnectionString(shardPath, SqliteOpenMode.ReadOnly));
        await connection.OpenAsync(cancellationToken);
        if (!await SnapshotPublisher.TableExistsAsync(connection, "library_setting_records"))
        {
            return [];
        }

        SettingRecordRow[] records = (await connection.QueryAsync<SettingRecordRow>(
            """
            select setting_key as SettingKey, schema_version as SchemaVersion, value_json as Value,
                   revision as Revision, updated_at as UpdatedAt, updated_by_device_id as UpdatedByDeviceId,
                   merge_policy as MergePolicy
            from library_setting_records
            order by setting_key;
            """)).ToArray();
        List<string> errors = new();
        foreach (SettingRecordRow row in records)
        {
            Result validation = LibrarySettingCatalog.ValidateRecord(row.ToRecord());
            if (validation.IsFailure)
            {
                errors.Add($"{row.SettingKey}: {validation.ErrorMessage}");
            }
        }

        return errors;
    }

    private sealed class SettingRecordRow
    {
        public string SettingKey { get; set; } = "";
        public int SchemaVersion { get; set; }
        public string Value { get; set; } = "";
        public long Revision { get; set; }
        public string UpdatedAt { get; set; } = "";
        public string UpdatedByDeviceId { get; set; } = "";
        public string MergePolicy { get; set; } = "";

        public SettingRecord ToRecord()
        {
            return new SettingRecord(SettingKey, SchemaVersion, Value, Revision, DateTimeOffset.Parse(UpdatedAt),
                UpdatedByDeviceId, MergePolicy);
        }
    }

    public async Task<Result<SnapshotImportResult>> ImportSnapshotToStagingAsync(SnapshotImportRequest request,
        CancellationToken cancellationToken = default)
    {
        BlockingOperationId? validationOperationId =
            await TryStartValidationOperationAsync(request.ManifestPath, cancellationToken);
        try
        {
            Result<SnapshotValidationResult> validation =
                await ValidateSnapshotAsync(request.ManifestPath, cancellationToken);
            if (validation.IsFailure)
            {
                await TryFailValidationOperationAsync(
                    validationOperationId,
                    validation.ErrorCode ?? AppErrorCodes.ValidationFailed,
                    validation.ErrorMessage ?? "Snapshot validation failed.",
                    "Snapshot import validation failed.",
                    ["Review snapshot manifest", "Retry import after fixing snapshot files"],
                    cancellationToken);
                return Result<SnapshotImportResult>.Failure(validation.ErrorCode!, validation.ErrorMessage!);
            }

            if (!validation.Value.IsValid || validation.Value.Manifest is null)
            {
                await TryFailValidationOperationAsync(
                    validationOperationId,
                    AppErrorCodes.ValidationFailed,
                    string.Join(" ", validation.Value.Errors),
                    "Snapshot import validation failed.",
                    ["Review snapshot manifest", "Retry import after fixing snapshot files"],
                    cancellationToken);
                return Result<SnapshotImportResult>.Success(new SnapshotImportResult("", default, null, false, false,
                    false, validation.Value.Errors));
            }

            SnapshotManifest? manifest = validation.Value.Manifest;
            LibraryId libraryId = LibraryId.Parse(manifest.LibraryId);
            List<string> warnings = new();
            bool matches = request.ExpectedLibraryId is null || request.ExpectedLibraryId.Value == libraryId;
            if (!matches)
            {
                const string message = "Manifest library does not match expected library.";
                warnings.Add(message);
                await TryFailValidationOperationAsync(
                    validationOperationId,
                    AppErrorCodes.LibraryMismatch,
                    message,
                    "Snapshot import blocked by library mismatch.",
                    ["Choose a snapshot from the current library", "Retry import after verifying library identity"],
                    cancellationToken);
                return Result<SnapshotImportResult>.Success(new SnapshotImportResult(manifest.SnapshotId, libraryId,
                    null, false, true, false, warnings));
            }

            if (request.CurrentRuntimeDatabasePath is not null && File.Exists(request.CurrentRuntimeDatabasePath))
            {
                string localLibrary = await SnapshotPublisher.ReadLibraryIdAsync(request.CurrentRuntimeDatabasePath);
                if (!string.Equals(localLibrary, manifest.LibraryId, StringComparison.OrdinalIgnoreCase))
                {
                    const string message = "Manifest library does not match current runtime database.";
                    warnings.Add(message);
                    await TryFailValidationOperationAsync(
                        validationOperationId,
                        AppErrorCodes.LibraryMismatch,
                        message,
                        "Snapshot import blocked by library mismatch.",
                        ["Choose a snapshot from the current library", "Retry import after verifying library identity"],
                        cancellationToken);
                    return Result<SnapshotImportResult>.Success(new SnapshotImportResult(manifest.SnapshotId, libraryId,
                        null, false, true, false, warnings));
                }
            }

            Directory.CreateDirectory(request.StagingRoot);
            if (!TryResolveSnapshotRoot(request.ManifestPath, out string syncRoot))
            {
                return Result<SnapshotImportResult>.Failure(AppErrorCodes.ValidationFailed,
                    "Manifest must be located directly inside a manifests directory.");
            }

            SnapshotShard firstShard = manifest.Shards.First();
            string stagingPath = Path.Combine(request.StagingRoot, $"{manifest.SnapshotId}.staging.sqlite");
            if (!TryResolveShardPath(syncRoot, firstShard.FileName, out string firstShardPath))
            {
                return Result<SnapshotImportResult>.Failure(AppErrorCodes.ValidationFailed,
                    "Snapshot contains an unsafe shard path.");
            }

            new SqliteConnectionFactory(stagingPath).DeleteDatabaseFiles();
            await CopyFileAsync(firstShardPath, stagingPath, cancellationToken);
            foreach (SnapshotShard shard in manifest.Shards.Skip(1))
            {
                if (!TryResolveShardPath(syncRoot, shard.FileName, out string shardPath))
                {
                    return Result<SnapshotImportResult>.Failure(AppErrorCodes.ValidationFailed,
                        "Snapshot contains an unsafe shard path.");
                }

                await MergeDataShardIntoStagingAsync(stagingPath, shardPath, cancellationToken);
            }

            await RebuildLocalSnapshotProjectionsAsync(stagingPath, cancellationToken);
            await SnapshotPublisher.ValidateDatabaseSchemaAsync(stagingPath);
            SqliteConnectionFactory stagingFactory = new(stagingPath);
            DocumentTreeService trees = new(stagingFactory, new SystemClock(), new MarkdigMarkdownEngine());
            Result treeValidation = await trees.ValidateStoredTreesAsync(cancellationToken);
            if (treeValidation.IsFailure)
            {
                stagingFactory.DeleteDatabaseFiles();
                return Result<SnapshotImportResult>.Failure(AppErrorCodes.ValidationFailed,
                    $"Imported Document Box Tree is invalid: {treeValidation.ErrorMessage}",
                    treeValidation.Conflicts);
            }

            if (manifest.SensitiveMutableShards.Count > 0)
            {
                warnings.Add(
                    "Device-level sensitive settings were found in this legacy snapshot and were not imported.");
            }

            await TryCompleteValidationOperationAsync(
                validationOperationId,
                $"Snapshot import validation passed for '{manifest.SnapshotId}'.",
                cancellationToken);
            return Result<SnapshotImportResult>.Success(new SnapshotImportResult(manifest.SnapshotId, libraryId,
                stagingPath, true, true, false, warnings));
        }
        catch (OperationCanceledException)
        {
            if (_blockingOperations is not null && validationOperationId is not null)
            {
                try
                {
                    await _blockingOperations.CancelAsync(
                        validationOperationId.Value,
                        "Snapshot import was cancelled.",
                        ["Retry import when ready"],
                        CancellationToken.None);
                }
                catch (Exception exception) // Reported below; the original cancellation remains authoritative.
                {
                    UnexpectedExceptionReporter.Report(exception, "infrastructure.snapshot-services",
                        "cancel-snapshot-validation-operation");
                }
            }

            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.snapshot-services"))
        {
            await TryFailValidationOperationAsync(
                validationOperationId,
                AppErrorCodes.DatabaseError,
                $"Snapshot import failed: {exception.Message}",
                "Snapshot import validation failed.",
                ["Retry import after fixing snapshot files"],
                cancellationToken);
            return Result<SnapshotImportResult>.Failure(AppErrorCodes.DatabaseError,
                $"Snapshot import failed: {exception.Message}");
        }
    }

    private static async Task RebuildLocalSnapshotProjectionsAsync(
        string stagingPath,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new(
            SnapshotPublisher.BuildConnectionString(stagingPath, SqliteOpenMode.ReadWriteCreate));
        await connection.OpenAsync(cancellationToken);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

        if (await SnapshotPublisher.TableExistsAsync(connection, "search_units_fts"))
        {
            await SearchFtsCacheWriter.InvalidateAsync(connection, transaction, cancellationToken);
        }

        if (await SnapshotPublisher.TableExistsAsync(connection, "local_maintenance_state"))
        {
            await connection.ExecuteAsync("delete from local_maintenance_state;", transaction: transaction);
        }

        if (await SnapshotPublisher.TableExistsAsync(connection, "item_tag_memberships"))
        {
            await connection.ExecuteAsync("delete from item_tag_memberships;", transaction: transaction);
            await connection.ExecuteAsync(
                """
                insert into item_tag_memberships (item_id, ordinal, tag, library_id, is_active)
                select i.item_id, cast(t.key as integer), t.value, i.library_id,
                       i.deleted_at is null and i.merged_into_item_id is null
                from items i,
                     json_each(case when json_valid(i.tags_json) then i.tags_json else '[]' end) t
                where json_type(case when json_valid(i.tags_json) then i.tags_json else '[]' end) = 'array'
                  and t.type = 'text';
                """,
                transaction: transaction);
        }

        if (await SnapshotPublisher.TableExistsAsync(connection, "file_asset_payload_refs"))
        {
            await connection.ExecuteAsync("delete from file_asset_payload_refs;", transaction: transaction);
            await connection.ExecuteAsync(
                """
                insert or ignore into file_asset_payload_refs (tree_revision_id, box_id, file_asset_id)
                select
                    b.tree_revision_id,
                    b.box_id,
                    case
                        when json_valid(b.payload_json) then lower(coalesce(
                            json_extract(b.payload_json, '$.assetId'),
                            json_extract(b.payload_json, '$.AssetId')))
                    end
                from document_boxes b
                join document_tree_revisions r on r.tree_revision_id = b.tree_revision_id
                where r.status in ('working', 'committed')
                  and case
                      when json_valid(b.payload_json) then coalesce(
                          json_type(b.payload_json, '$.assetId'),
                          json_type(b.payload_json, '$.AssetId'))
                  end = 'text'
                  and case
                      when json_valid(b.payload_json) then coalesce(
                          json_extract(b.payload_json, '$.assetId'),
                          json_extract(b.payload_json, '$.AssetId'))
                  end <> '';
                """,
                transaction: transaction);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task MergeDataShardIntoStagingAsync(string stagingPath, string shardPath,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
            {
                DataSource = stagingPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, ForeignKeys = false
            }
            .ToString());
        await connection.OpenAsync(cancellationToken);
        await connection.ExecuteAsync("attach database @Path as shard;", new { Path = shardPath });
        await connection.ExecuteAsync("pragma foreign_keys = off;");
        foreach (string table in MergeTables)
        {
            if (!await TableExistsAsync(connection, table, "main") ||
                !await TableExistsAsync(connection, table, "shard"))
            {
                continue;
            }

            await connection.ExecuteAsync($"insert or ignore into main.{table} select * from shard.{table};");
        }

        await connection.ExecuteAsync("detach database shard;");
        await connection.ExecuteAsync("pragma foreign_keys = on;");
    }

    private static async Task CopyFileAsync(string sourcePath, string destinationPath,
        CancellationToken cancellationToken)
    {
        const int bufferSize = 1024 * 128;
        await using FileStream source = new(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize,
            true);
        await using FileStream destination = new(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize,
            true);
        await source.CopyToAsync(destination, bufferSize, cancellationToken);
        await destination.FlushAsync(cancellationToken);
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string table, string schema)
    {
        return await connection.ExecuteScalarAsync<int>(
            $"select count(1) from {schema}.sqlite_master where type in ('table','view') and name = @Table;",
            new { Table = table }) > 0;
    }

    private static bool TryResolveSnapshotRoot(string manifestPath, out string snapshotRoot)
    {
        snapshotRoot = string.Empty;
        string fullManifestPath = Path.GetFullPath(manifestPath);
        DirectoryInfo? manifestsDirectory = Directory.GetParent(fullManifestPath);
        if (manifestsDirectory is null ||
            !string.Equals(manifestsDirectory.Name, "manifests", StringComparison.OrdinalIgnoreCase) ||
            manifestsDirectory.Parent is null)
        {
            return false;
        }

        snapshotRoot = manifestsDirectory.Parent.FullName;
        return true;
    }

    private static bool TryResolveShardPath(string snapshotRoot, string shardFileName, out string shardPath)
    {
        shardPath = string.Empty;
        if (string.IsNullOrWhiteSpace(shardFileName) || Path.IsPathRooted(shardFileName))
        {
            return false;
        }

        string candidate = Path.GetFullPath(Path.Combine(snapshotRoot, shardFileName));
        if (!SnapshotPublisher.IsPathInside(candidate, snapshotRoot))
        {
            return false;
        }

        shardPath = candidate;
        return true;
    }

    private static readonly string[] MergeTables =
    [
        "library_metadata",
        "collections",
        "items",
        "item_collections",
        "item_identifiers",
        "item_creators",
        "item_dates",
        "file_assets",
        "file_search_root_definitions",
        "known_file_locations",
        "document_instances",
        "pages",
        "document_tree_revisions",
        "document_boxes",
        "ocr_presets",
        "ocr_preset_versions",
        "ocr_runs",
        "ocr_page_results",
        "ocr_candidate_adoptions",
        "search_units",
        "search_index_status",
        "document_commits",
        "document_commit_pages",
        "search_profiles",
        "search_rewrite_rules",
        "search_settings",
        "library_setting_records"
    ];

    private async Task<BlockingOperationId?> TryStartValidationOperationAsync(string manifestPath,
        CancellationToken cancellationToken)
    {
        if (_blockingOperations is null)
        {
            return null;
        }

        try
        {
            Result<BlockingOperation> started = await _blockingOperations.StartAsync(
                BlockingOperationTypes.SnapshotImportValidation,
                BlockingOperationScopeTypes.SnapshotImport,
                Path.GetFileName(manifestPath),
                true,
                "Validating snapshot import.",
                nextActions: ["Review snapshot manifest", "Retry import after fixing snapshot files"],
                cancellationToken: cancellationToken);
            return started.IsSuccess ? started.Value.OperationId : null;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.snapshot-services",
                                              "complete-snapshot-validation-operation"))
        {
            return null;
        }
    }

    private async Task TryCompleteValidationOperationAsync(
        BlockingOperationId? operationId,
        string progressLabel,
        CancellationToken cancellationToken)
    {
        if (_blockingOperations is null || operationId is null)
        {
            return;
        }

        try
        {
            await _blockingOperations.CompleteAsync(
                operationId.Value,
                progressLabel,
                Array.Empty<string>(),
                cancellationToken);
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.snapshot-services",
                                              "fail-snapshot-validation-operation"))
        {
        }
    }

    private async Task TryFailValidationOperationAsync(
        BlockingOperationId? operationId,
        string errorCode,
        string errorMessage,
        string progressLabel,
        IReadOnlyList<string> nextActions,
        CancellationToken cancellationToken)
    {
        if (_blockingOperations is null || operationId is null)
        {
            return;
        }

        try
        {
            await _blockingOperations.FailAsync(
                operationId.Value,
                errorCode,
                errorMessage,
                progressLabel,
                nextActions,
                cancellationToken);
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.snapshot-services",
                                              "fail-snapshot-validation-operation"))
        {
            _ = exception;
        }
    }
}
