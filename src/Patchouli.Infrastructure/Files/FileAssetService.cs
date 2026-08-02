using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Database;

namespace Patchouli.Infrastructure.Files;

public sealed class FileAssetService : IFileAssetService
{
    private const int SampleSize = 4096;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly ILibraryIdentityService _libraryIdentityService;
    private readonly IClock _clock;
    private readonly IFileFingerprintService _fingerprintService;
    private readonly ILibraryRevisionService? _revisions;

    public FileAssetService(
        SqliteConnectionFactory connectionFactory,
        ILibraryIdentityService libraryIdentityService,
        IClock clock,
        IFileFingerprintService? fingerprintService = null,
        ILibraryRevisionService? revisions = null)
    {
        _connectionFactory = connectionFactory;
        _libraryIdentityService = libraryIdentityService;
        _clock = clock;
        _fingerprintService = fingerprintService ?? new FileFingerprintService();
        _revisions = revisions;
    }

    public async Task<Result<FileAsset>> RegisterFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Result<FileAsset>.Failure(AppErrorCodes.ValidationFailed, "File path is required.");
        }

        Result<LibraryMetadata> libraryResult = await _libraryIdentityService.GetCurrentLibraryAsync(cancellationToken);
        if (libraryResult.IsFailure)
        {
            return Result<FileAsset>.Failure(libraryResult.ErrorCode!, libraryResult.ErrorMessage!);
        }

        try
        {
            string normalizedPath = Path.GetFullPath(path);
            FileInfo fileInfo = new(normalizedPath);
            bool exists = fileInfo.Exists;
            DateTimeOffset now = _clock.UtcNow.ToUniversalTime();
            Result<FileFingerprint>? fingerprint = exists
                ? await _fingerprintService.GetFileMetadataAsync(normalizedPath, cancellationToken)
                : null;

            if (fingerprint?.IsFailure == true)
            {
                return Result<FileAsset>.Failure(fingerprint.ErrorCode!, fingerprint.ErrorMessage!);
            }

            FileAsset asset = new(
                exists ? CreateFileAssetId(fingerprint!.Value.FullBlake3) : FileAssetId.New(),
                libraryResult.Value.LibraryId,
                normalizedPath,
                fileInfo.Name,
                exists ? fingerprint!.Value.SizeBytes : 0,
                exists ? fingerprint!.Value.MtimeUtc : null,
                exists ? fingerprint!.Value.QuickHash : null,
                exists ? fingerprint!.Value.FullBlake3 : null,
                null,
                null,
                exists ? FileAssetStatus.Available : FileAssetStatus.Missing,
                now,
                now);

            using IDisposable writeLease = await _connectionFactory.EnterWriteAsync(cancellationToken);
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            if (exists)
            {
                FileAssetRow? existing = await connection.QuerySingleOrDefaultAsync<FileAssetRow>(
                    """
                    select
                        file_asset_id as FileAssetId,
                        library_id as LibraryId,
                        original_path as OriginalPath,
                        file_name as FileName,
                        size_bytes as SizeBytes,
                        mtime_utc as MtimeUtc,
                        quick_hash as QuickHash,
                        full_blake3 as FullBlake3,
                        page_count as PageCount,
                        pdf_trailer_id as PdfTrailerId,
                        status as Status,
                        created_at as CreatedAt,
                        updated_at as UpdatedAt
                    from file_assets
                    where file_asset_id = @FileAssetId;
                    """,
                    new { FileAssetId = asset.FileAssetId.ToString() },
                    transaction);

                if (existing is not null)
                {
                    FileAsset existingAsset = existing.ToFileAsset();
                    if (existingAsset.LibraryId != libraryResult.Value.LibraryId)
                    {
                        return Result<FileAsset>.Failure(
                            AppErrorCodes.LibraryMismatch,
                            "File content already exists in a different library.");
                    }

                    FileAsset updatedAsset = existingAsset with
                    {
                        SizeBytes = asset.SizeBytes,
                        MtimeUtc = asset.MtimeUtc,
                        QuickHash = asset.QuickHash,
                        FullBlake3 = asset.FullBlake3,
                        Status = FileAssetStatus.Available,
                        UpdatedAt = now
                    };

                    await connection.ExecuteAsync(
                        """
                        update file_assets
                        set size_bytes = @SizeBytes,
                            mtime_utc = @MtimeUtc,
                            quick_hash = @QuickHash,
                            full_blake3 = @FullBlake3,
                            status = @Status,
                            updated_at = @UpdatedAt
                        where file_asset_id = @FileAssetId;
                        """,
                        new
                        {
                            FileAssetId = updatedAsset.FileAssetId.ToString(),
                            updatedAsset.SizeBytes,
                            MtimeUtc = updatedAsset.MtimeUtc?.ToUniversalTime().ToString("O"),
                            updatedAsset.QuickHash,
                            updatedAsset.FullBlake3,
                            updatedAsset.Status,
                            UpdatedAt = updatedAsset.UpdatedAt.ToUniversalTime().ToString("O")
                        },
                        transaction);

                    await UpsertKnownLocationAsync(
                        connection,
                        transaction,
                        updatedAsset.FileAssetId,
                        normalizedPath,
                        FileAssetStatus.Available,
                        now);

                    Result<LibraryChangeSet?> existingRevision = await IncrementRevisionAsync(
                        connection, transaction, updatedAsset.FileAssetId, cancellationToken);
                    if (existingRevision.IsFailure)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        return Result<FileAsset>.Failure(existingRevision.ErrorCode!, existingRevision.ErrorMessage!);
                    }

                    await transaction.CommitAsync(cancellationToken);
                    PublishRevision(existingRevision.Value);
                    return Result<FileAsset>.Success(updatedAsset);
                }
            }

            await connection.ExecuteAsync(
                """
                insert into file_assets (
                    file_asset_id, library_id, original_path, file_name, size_bytes,
                    mtime_utc, quick_hash, full_blake3, page_count, pdf_trailer_id,
                    status, created_at, updated_at
                )
                values (
                    @FileAssetId, @LibraryId, @OriginalPath, @FileName, @SizeBytes,
                    @MtimeUtc, @QuickHash, @FullBlake3, @PageCount, @PdfTrailerId,
                    @Status, @CreatedAt, @UpdatedAt
                );
                """,
                ToParameters(asset),
                transaction);

            if (exists)
            {
                await UpsertKnownLocationAsync(
                    connection,
                    transaction,
                    asset.FileAssetId,
                    asset.OriginalPath,
                    FileAssetStatus.Available,
                    now);
            }

            Result<LibraryChangeSet?> revision = await IncrementRevisionAsync(
                connection, transaction, asset.FileAssetId, cancellationToken);
            if (revision.IsFailure)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<FileAsset>.Failure(revision.ErrorCode!, revision.ErrorMessage!);
            }

            await transaction.CommitAsync(cancellationToken);
            PublishRevision(revision.Value);
            return Result<FileAsset>.Success(asset);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.file-asset"))
        {
            return DatabaseFailure<FileAsset>(exception);
        }
    }

    public async Task<Result<FileAsset>> GetFileAssetAsync(
        FileAssetId fileAssetId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            FileAssetRow? row = await connection.QuerySingleOrDefaultAsync<FileAssetRow>(
                """
                select
                    file_asset_id as FileAssetId,
                    library_id as LibraryId,
                    original_path as OriginalPath,
                    file_name as FileName,
                    size_bytes as SizeBytes,
                    mtime_utc as MtimeUtc,
                    quick_hash as QuickHash,
                    full_blake3 as FullBlake3,
                    page_count as PageCount,
                    pdf_trailer_id as PdfTrailerId,
                    status as Status,
                    created_at as CreatedAt,
                    updated_at as UpdatedAt
                from file_assets
                where file_asset_id = @FileAssetId;
                """,
                new { FileAssetId = fileAssetId.ToString() });

            return row is null
                ? Result<FileAsset>.Failure(AppErrorCodes.NotFound, "File asset was not found.")
                : Result<FileAsset>.Success(row.ToFileAsset());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.file-asset"))
        {
            return DatabaseFailure<FileAsset>(exception);
        }
    }

    public async Task<Result<IReadOnlyList<string>>> ListOriginalPathsAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);
            IEnumerable<string> paths = await connection.QueryAsync<string>("select original_path from file_assets;");
            return Result<IReadOnlyList<string>>.Success(paths.ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.file-asset"))
        {
            return DatabaseFailure<IReadOnlyList<string>>(exception);
        }
    }

    private async Task<Result<LibraryChangeSet?>> IncrementRevisionAsync(SqliteConnection connection,
        DbTransaction transaction, FileAssetId fileAssetId, CancellationToken cancellationToken)
    {
        if (_revisions is null)
        {
            return Result<LibraryChangeSet?>.Success(null);
        }

        FileAssetImpactRow[] impacts = (await connection.QueryAsync<FileAssetImpactRow>(
            """
            select di.document_instance_id as DocumentInstanceId, di.item_id as ItemId
            from document_instances di
            where di.file_asset_id = @FileAssetId;
            """,
            new { FileAssetId = fileAssetId.ToString() }, transaction)).ToArray();
        Result<LibraryChangeSet> revision = await _revisions.IncrementInTransactionAsync(connection, transaction,
            LibraryChangeSet.Empty with
            {
                ItemIds = impacts.Select(row => ItemId.Parse(row.ItemId)).Distinct().ToArray(),
                DocumentInstanceIds = impacts.Select(row => DocumentInstanceId.Parse(row.DocumentInstanceId)).Distinct()
                    .ToArray()
            }, cancellationToken);
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

    private sealed class FileAssetImpactRow
    {
        public string DocumentInstanceId { get; init; } = string.Empty;
        public string ItemId { get; init; } = string.Empty;
    }

    private static object ToParameters(FileAsset asset)
    {
        return new
        {
            FileAssetId = asset.FileAssetId.ToString(),
            LibraryId = asset.LibraryId.ToString(),
            asset.OriginalPath,
            asset.FileName,
            asset.SizeBytes,
            MtimeUtc = asset.MtimeUtc?.ToUniversalTime().ToString("O"),
            asset.QuickHash,
            asset.FullBlake3,
            asset.PageCount,
            asset.PdfTrailerId,
            asset.Status,
            CreatedAt = asset.CreatedAt.ToUniversalTime().ToString("O"),
            UpdatedAt = asset.UpdatedAt.ToUniversalTime().ToString("O")
        };
    }

    private static FileAssetId CreateFileAssetId(string? fullBlake3)
    {
        if (string.IsNullOrWhiteSpace(fullBlake3) || fullBlake3.Length < 32)
        {
            return FileAssetId.New();
        }

        return FileAssetId.Parse(
            $"{fullBlake3[..8]}-{fullBlake3[8..12]}-{fullBlake3[12..16]}-{fullBlake3[16..20]}-{fullBlake3[20..32]}");
    }

    private static Result<T> DatabaseFailure<T>(Exception exception)
    {
        return Result<T>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
    }

    internal static Task UpsertKnownLocationAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        FileAssetId fileAssetId,
        string path,
        string status,
        DateTimeOffset lastSeenAt)
    {
        return connection.ExecuteAsync(
            """
            insert into known_file_locations (location_id, file_asset_id, path, last_seen_at, status)
            values (@LocationId, @FileAssetId, @Path, @LastSeenAt, @Status)
            on conflict(file_asset_id, path) do update set
                last_seen_at = excluded.last_seen_at,
                status = excluded.status;
            """,
            new
            {
                LocationId = KnownFileLocationId.New().ToString(),
                FileAssetId = fileAssetId.ToString(),
                Path = Path.GetFullPath(path),
                LastSeenAt = lastSeenAt.ToUniversalTime().ToString("O"),
                Status = status
            },
            transaction);
    }

    private sealed class FileAssetRow
    {
        public string FileAssetId { get; set; } = string.Empty;
        public string LibraryId { get; set; } = string.Empty;
        public string OriginalPath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public string? MtimeUtc { get; set; }
        public string? QuickHash { get; set; }
        public string? FullBlake3 { get; set; }
        public int? PageCount { get; set; }
        public string? PdfTrailerId { get; set; }
        public string Status { get; set; } = string.Empty;
        public string CreatedAt { get; set; } = string.Empty;
        public string UpdatedAt { get; set; } = string.Empty;

        public FileAsset ToFileAsset()
        {
            return new FileAsset(
                Patchouli.Core.Ids.FileAssetId.Parse(FileAssetId),
                Patchouli.Core.Ids.LibraryId.Parse(LibraryId),
                OriginalPath,
                FileName,
                SizeBytes,
                MtimeUtc is null ? null : DateTimeOffset.Parse(MtimeUtc),
                QuickHash,
                FullBlake3,
                PageCount,
                PdfTrailerId,
                Status,
                DateTimeOffset.Parse(CreatedAt),
                DateTimeOffset.Parse(UpdatedAt));
        }
    }
}
