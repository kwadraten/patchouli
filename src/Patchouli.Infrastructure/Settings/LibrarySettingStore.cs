using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Results;
using Patchouli.Core.Settings;
using Patchouli.Infrastructure.Database;

namespace Patchouli.Infrastructure.Settings;

/// <summary>Owns the durable, snapshot-eligible non-secret settings table.</summary>
public sealed class LibrarySettingStore : ILibrarySettingStore
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public LibrarySettingStore(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<Result<SettingRecord?>> GetAsync(string settingKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settingKey))
        {
            return Result<SettingRecord?>.Failure(AppErrorCodes.ValidationFailed, "Setting key is required.");
        }

        if (!LibrarySettingCatalog.TryGet(settingKey.Trim(), out SettingCatalogEntry? entry) ||
            !entry.IsSnapshotEligible)
        {
            return Result<SettingRecord?>.Failure(AppErrorCodes.UnsupportedOperation,
                "Setting is not eligible for library snapshot storage.");
        }

        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            Row? row = await connection.QuerySingleOrDefaultAsync<Row>(
                """
                select setting_key as SettingKey, schema_version as SchemaVersion, value_json as Value,
                       revision as Revision, updated_at as UpdatedAt, updated_by_device_id as UpdatedByDeviceId,
                       merge_policy as MergePolicy
                from library_setting_records
                where setting_key = @SettingKey;
                """, new { SettingKey = settingKey.Trim() });
            return Result<SettingRecord?>.Success(row?.ToRecord());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.library-setting-store"))
        {
            return Result<SettingRecord?>.Failure(AppErrorCodes.DatabaseError,
                $"Library setting lookup failed: {exception.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<SettingRecord>>> ListAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            string[] allowed = LibrarySettingCatalog.All.Where(entry => entry.IsSnapshotEligible)
                .Select(entry => entry.SettingKey)
                .ToArray();
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            Row[] records = (await connection.QueryAsync<Row>(
                """
                select setting_key as SettingKey, schema_version as SchemaVersion, value_json as Value,
                       revision as Revision, updated_at as UpdatedAt, updated_by_device_id as UpdatedByDeviceId,
                       merge_policy as MergePolicy
                from library_setting_records
                where setting_key in @Allowed
                order by setting_key;
                """, new { Allowed = allowed })).ToArray();
            return Result<IReadOnlyList<SettingRecord>>.Success(records.Select(row => row.ToRecord()).ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.library-setting-store"))
        {
            return Result<IReadOnlyList<SettingRecord>>.Failure(AppErrorCodes.DatabaseError,
                $"Library setting list failed: {exception.Message}");
        }
    }

    public async Task<Result> SaveAsync(SettingRecord record, CancellationToken cancellationToken = default)
    {
        Result validation = Validate(record);
        if (validation.IsFailure)
        {
            return validation;
        }

        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await connection.ExecuteAsync(
                """
                insert into library_setting_records (
                    setting_key, schema_version, value_json, revision, updated_at, updated_by_device_id, merge_policy)
                values (@SettingKey, @SchemaVersion, @Value, @Revision, @UpdatedAt, @UpdatedByDeviceId, @MergePolicy)
                on conflict(setting_key) do update set
                    schema_version = excluded.schema_version,
                    value_json = excluded.value_json,
                    revision = excluded.revision,
                    updated_at = excluded.updated_at,
                    updated_by_device_id = excluded.updated_by_device_id,
                    merge_policy = excluded.merge_policy;
                """, new
                {
                    SettingKey = record.SettingKey.Trim(),
                    record.SchemaVersion,
                    record.Value,
                    record.Revision,
                    UpdatedAt = record.UpdatedAt.ToUniversalTime().ToString("O"),
                    record.UpdatedByDeviceId,
                    record.MergePolicy
                });
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.library-setting-store"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Library setting save failed: {exception.Message}");
        }
    }

    public async Task<Result> DeleteAsync(string settingKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settingKey))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "Setting key is required.");
        }

        if (!LibrarySettingCatalog.TryGet(settingKey.Trim(), out SettingCatalogEntry? entry) ||
            !entry.IsSnapshotEligible)
        {
            return Result.Failure(AppErrorCodes.UnsupportedOperation,
                "Setting is not eligible for library snapshot storage.");
        }

        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await connection.ExecuteAsync("delete from library_setting_records where setting_key = @SettingKey;",
                new { SettingKey = settingKey.Trim() });
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.library-setting-store"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Library setting delete failed: {exception.Message}");
        }
    }

    private static Result Validate(SettingRecord record)
    {
        return LibrarySettingCatalog.ValidateRecord(record);
    }

    private sealed class Row
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
}
