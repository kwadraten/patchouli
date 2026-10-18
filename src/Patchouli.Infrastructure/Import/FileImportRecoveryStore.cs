using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Import;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Database;

namespace Patchouli.Infrastructure.Import;

/// <summary>
/// Issue #11: SQLite-backed <see cref="IFileImportRecoveryStore"/> over the runtime
/// database (migration 049). Records are diagnostics; a write failure here never
/// masks the import failure it describes.
/// </summary>
public sealed class FileImportRecoveryStore : IFileImportRecoveryStore
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public FileImportRecoveryStore(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<Result> RecordAsync(FileImportRecoveryRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await connection.ExecuteAsync(
                """
                insert into file_import_recovery_records (
                    record_id, created_at, trigger, kind, original_path, moved_path,
                    restored_path, status, diagnosis)
                values (
                    @RecordId, @CreatedAt, @Trigger, @Kind, @OriginalPath, @MovedPath,
                    @RestoredPath, @Status, @Diagnosis);
                """,
                new
                {
                    record.RecordId,
                    CreatedAt = record.CreatedAt.ToUniversalTime().ToString("O"),
                    record.Trigger,
                    record.Kind,
                    record.OriginalPath,
                    record.MovedPath,
                    record.RestoredPath,
                    record.Status,
                    record.Diagnosis
                });
            return Result.Success();
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.file-import-recovery"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError,
                $"Recovery record write failed: {exception.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<FileImportRecoveryRecord>>> ListRecordsAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            IEnumerable<RecoveryRecordRow> rows = await connection.QueryAsync<RecoveryRecordRow>(
                """
                select record_id as RecordId, created_at as CreatedAt, trigger as Trigger,
                       kind as Kind, original_path as OriginalPath, moved_path as MovedPath,
                       restored_path as RestoredPath, status as Status, diagnosis as Diagnosis
                from file_import_recovery_records
                order by created_at desc
                limit @Limit;
                """,
                new { Limit = Math.Max(1, limit) });
            return Result<IReadOnlyList<FileImportRecoveryRecord>>.Success(rows.Select(row =>
                new FileImportRecoveryRecord(
                    row.RecordId,
                    DateTimeOffset.Parse(row.CreatedAt),
                    row.Trigger,
                    row.Kind,
                    row.OriginalPath,
                    row.MovedPath,
                    row.RestoredPath,
                    row.Status,
                    row.Diagnosis)).ToArray());
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.file-import-recovery"))
        {
            return Result<IReadOnlyList<FileImportRecoveryRecord>>.Failure(AppErrorCodes.DatabaseError,
                $"Recovery record read failed: {exception.Message}");
        }
    }

    private sealed class RecoveryRecordRow
    {
        public string RecordId { get; set; } = string.Empty;
        public string CreatedAt { get; set; } = string.Empty;
        public string Trigger { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string OriginalPath { get; set; } = string.Empty;
        public string? MovedPath { get; set; }
        public string? RestoredPath { get; set; }
        public string Status { get; set; } = string.Empty;
        public string Diagnosis { get; set; } = string.Empty;
    }
}
