using Patchouli.Core.Results;

namespace Patchouli.Core.Import;

/// <summary>Durable record of a local file import that needed move recovery.</summary>
public sealed record FileImportRecoveryRecord(
    string RecordId,
    DateTimeOffset CreatedAt,
    string Trigger,
    string Kind,
    string OriginalPath,
    string? MovedPath,
    string? RestoredPath,
    string Status,
    string Diagnosis);

/// <summary>Stable statuses for <see cref="FileImportRecoveryRecord"/>.</summary>
public static class FileImportRecoveryStatuses
{
    public const string MoveFailed = "move_failed";
    public const string ImportFailedRestored = "import_failed_restored";
    public const string ImportFailedRestoreFailed = "import_failed_restore_failed";
    public const string ImportFailedNoMove = "import_failed_no_move";
    public const string ImportFailedKeptAtMovedLocation = "import_failed_kept_at_moved_location";
}

/// <summary>
/// Issue #11: durable recovery records for local file imports. File moves and DB
/// commits are not one transaction; restore attempts are recorded in the runtime
/// database — a repo/runtime-agreed durable location, never temp cache.
/// </summary>
public interface IFileImportRecoveryStore
{
    Task<Result> RecordAsync(FileImportRecoveryRecord record, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<FileImportRecoveryRecord>>> ListRecordsAsync(
        int limit = 50,
        CancellationToken cancellationToken = default);
}
