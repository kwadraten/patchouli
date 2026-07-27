using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Files;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Operations;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Conflicts;
using Patchouli.Infrastructure.Database;

namespace Patchouli.Infrastructure.Files;

public sealed class FileResolutionService : IFileResolutionService
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly ILibraryIdentityService _libraryIdentityService;
    private readonly IClock _clock;
    private readonly IFileFingerprintService _fingerprintService;
    private readonly IBlockingOperationService? _blockingOperations;
    private readonly IFileSearchRootAccess _rootAccess;

    public FileResolutionService(
        SqliteConnectionFactory connectionFactory,
        ILibraryIdentityService libraryIdentityService,
        IClock clock,
        IFileFingerprintService? fingerprintService = null,
        IBlockingOperationService? blockingOperations = null,
        IFileSearchRootAccess? rootAccess = null)
    {
        _connectionFactory = connectionFactory;
        _libraryIdentityService = libraryIdentityService;
        _clock = clock;
        _fingerprintService = fingerprintService ?? new FileFingerprintService();
        _blockingOperations = blockingOperations;
        _rootAccess = rootAccess ?? new FileSearchRootAccess();
    }

    public async Task<Result<FileSearchRoot>> AddSearchRootAsync(
        SelectedFileSearchRoot selectedRoot,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(selectedRoot.DisplayPath))
        {
            return Result<FileSearchRoot>.Failure(AppErrorCodes.ValidationFailed, "Search root path is required.");
        }

        if (string.IsNullOrWhiteSpace(selectedRoot.ProviderIdentity) ||
            string.IsNullOrWhiteSpace(selectedRoot.AuthorizationKind))
        {
            return Result<FileSearchRoot>.Failure(AppErrorCodes.ValidationFailed,
                "Search root picker provenance and authorization kind are required.");
        }

        Result<LibraryMetadata> libraryResult = await _libraryIdentityService.GetCurrentLibraryAsync(cancellationToken);
        if (libraryResult.IsFailure)
        {
            return Result<FileSearchRoot>.Failure(libraryResult.ErrorCode!, libraryResult.ErrorMessage!);
        }

        try
        {
            DateTimeOffset now = _clock.UtcNow.ToUniversalTime();
            string normalizedRootPath = Path.GetFullPath(selectedRoot.DisplayPath);
            bool isRootAvailable = Directory.Exists(normalizedRootPath);
            FileSearchRoot root = new(
                FileSearchRootId.New(),
                libraryResult.Value.LibraryId,
                normalizedRootPath,
                isRootAvailable,
                now,
                now,
                selectedRoot.AuthorizationKind,
                selectedRoot.AuthorizationPayload,
                selectedRoot.AuthorizationPayloadVersion,
                selectedRoot.SelectedAt.ToUniversalTime());
            BlockingOperationId? scanOperationId = await TryStartRootScanAsync(root.RootPath, cancellationToken);

            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            int duplicate = await connection.ExecuteScalarAsync<int>(
                "select count(1) from file_search_roots where library_id = @LibraryId and root_path = @RootPath;",
                new { LibraryId = root.LibraryId.ToString(), root.RootPath },
                transaction);

            if (duplicate > 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                await TryFailRootScanAsync(
                    scanOperationId,
                    AppErrorCodes.InvalidState,
                    "This search root is already registered for the current library.",
                    "Search root scan blocked by duplicate registration.",
                    ["Choose a different search root"],
                    cancellationToken);
                return Result<FileSearchRoot>.Failure(
                    AppErrorCodes.InvalidState,
                    "This search root is already registered for the current library.");
            }

            await connection.ExecuteAsync(
                """
                insert into file_search_roots (root_id, library_id, root_path, is_available, created_at, updated_at,
                    authorization_kind, authorization_payload, authorization_payload_version, authorization_updated_at)
                values (@RootId, @LibraryId, @RootPath, @IsAvailable, @CreatedAt, @UpdatedAt,
                    @AuthorizationKind, @AuthorizationPayload, @AuthorizationPayloadVersion, @AuthorizationUpdatedAt);
                """,
                ToParameters(root),
                transaction);

            Result<ResolvedFileSearchRoot> reopened = await _rootAccess.ReopenAsync(root, cancellationToken);
            FileSearchRootTraversalResult scanSummary = reopened.IsSuccess
                ? await _rootAccess.TraverseAsync(reopened.Value, cancellationToken)
                : new FileSearchRootTraversalResult([], [], [], [], FileSearchRootStatuses.AuthorizationRequired,
                    FileSearchRootScanStatuses.Failed);
            if (reopened.IsSuccess)
            {
                reopened.Value.AccessLease?.Dispose();
            }

            await TryLogExclusionsAsync(scanOperationId, scanSummary.ExcludedEntries, cancellationToken);

            bool scanComplete = isRootAvailable && scanSummary.ScanStatus == FileSearchRootScanStatuses.Complete;
            if (root.IsAvailable != scanComplete)
            {
                root = root with { IsAvailable = scanComplete, UpdatedAt = _clock.UtcNow.ToUniversalTime() };
                await connection.ExecuteAsync(
                    "update file_search_roots set is_available = @IsAvailable, updated_at = @UpdatedAt where root_id = @RootId;",
                    new { IsAvailable = scanComplete, UpdatedAt = root.UpdatedAt, RootId = root.RootId.ToString() },
                    transaction);
            }

            await transaction.CommitAsync(cancellationToken);

            if (scanSummary.ScanStatus == FileSearchRootScanStatuses.Cancelled)
            {
                if (_blockingOperations is not null && scanOperationId is not null)
                {
                    await _blockingOperations.CancelAsync(scanOperationId.Value, "Search root scan was cancelled.",
                        ["Retry the root scan"], CancellationToken.None);
                }
            }
            else if (scanSummary.ScanStatus != FileSearchRootScanStatuses.Complete)
            {
                await TryFailRootScanAsync(
                    scanOperationId,
                    scanSummary.ScanStatus == FileSearchRootScanStatuses.Partial
                        ? "scan_partial"
                        : RootFailureCode(scanSummary.RootStatus),
                    scanSummary.ScanStatus == FileSearchRootScanStatuses.Partial
                        ? "Search root scan was incomplete."
                        : "Search root scan failed.",
                    $"Search root scan ended with status {scanSummary.ScanStatus}.",
                    ["Reconnect the search root", "Retry the root scan after the directory is available"],
                    CancellationToken.None);
            }
            else
            {
                await TryCompleteRootScanAsync(
                    scanOperationId,
                    $"Scanned {scanSummary.Files.Count} file(s) while registering the search root ({scanSummary.ScanStatus}).",
                    cancellationToken);
            }

            return Result<FileSearchRoot>.Success(root);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.file-resolution"))
        {
            return DatabaseFailure<FileSearchRoot>(exception);
        }
    }

    private static string RootFailureCode(string rootStatus)
    {
        return rootStatus switch
        {
            FileSearchRootStatuses.AccessDenied => "access_denied",
            FileSearchRootStatuses.AuthorizationRequired => "authorization_required",
            FileSearchRootStatuses.Offline => AppErrorCodes.NotFound,
            _ => "scan_failed"
        };
    }

    private async Task TryLogExclusionsAsync(BlockingOperationId? operationId,
        IReadOnlyList<FileSearchRootExcludedEntry> excludedEntries, CancellationToken cancellationToken)
    {
        if (_blockingOperations is null || operationId is null)
        {
            return;
        }

        foreach (IGrouping<string, FileSearchRootExcludedEntry> group in excludedEntries.GroupBy(entry => entry.Rule,
                     StringComparer.Ordinal))
        {
            await _blockingOperations.AddLogEntryAsync(operationId.Value, "info",
                $"Excluded {group.Count()} path(s) by scan rule.", group.Key,
                BlockingOperationScopeTypes.FileSearchRoot, null, cancellationToken);
        }
    }

    public async Task<Result<IReadOnlyList<FileSearchRoot>>> ListSearchRootsAsync(
        CancellationToken cancellationToken = default)
    {
        Result<LibraryMetadata> libraryResult = await _libraryIdentityService.GetCurrentLibraryAsync(cancellationToken);
        if (libraryResult.IsFailure)
        {
            return Result<IReadOnlyList<FileSearchRoot>>.Failure(libraryResult.ErrorCode!, libraryResult.ErrorMessage!);
        }

        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            IEnumerable<SearchRootRow> rows = await connection.QueryAsync<SearchRootRow>(
                """
                select root_id as RootId, library_id as LibraryId, root_path as RootPath,
                       is_available as IsAvailable, created_at as CreatedAt, updated_at as UpdatedAt,
                       authorization_kind as AuthorizationKind, authorization_payload as AuthorizationPayload,
                       authorization_payload_version as AuthorizationPayloadVersion, authorization_updated_at as AuthorizationUpdatedAt
                from file_search_roots
                where library_id = @LibraryId
                order by root_path;
                """,
                new { LibraryId = libraryResult.Value.LibraryId.ToString() });

            return Result<IReadOnlyList<FileSearchRoot>>.Success(rows.Select(row => row.ToSearchRoot()).ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.file-resolution"))
        {
            return DatabaseFailure<IReadOnlyList<FileSearchRoot>>(exception);
        }
    }

    public async Task<Result> DeleteSearchRootAsync(
        FileSearchRootId rootId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            int affected = await connection.ExecuteAsync(
                "delete from file_search_roots where root_id = @RootId;",
                new { RootId = rootId.ToString() });

            return affected == 0
                ? Result.Failure(AppErrorCodes.NotFound, "Search root was not found.")
                : Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.file-resolution"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result> SetSearchRootAvailabilityAsync(
        FileSearchRootId rootId,
        bool isAvailable,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            int affected = await connection.ExecuteAsync(
                """
                update file_search_roots
                set is_available = @IsAvailable, updated_at = @UpdatedAt
                where root_id = @RootId;
                """,
                new
                {
                    RootId = rootId.ToString(),
                    IsAvailable = isAvailable ? 1 : 0,
                    UpdatedAt = FormatUtc(_clock.UtcNow)
                });

            return affected == 0
                ? Result.Failure(AppErrorCodes.NotFound, "Search root was not found.")
                : Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.file-resolution"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<KnownFileLocation>>> ListKnownLocationsAsync(
        FileAssetId fileAssetId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            int assetExists = await connection.ExecuteScalarAsync<int>(
                "select count(1) from file_assets where file_asset_id = @FileAssetId;",
                new { FileAssetId = fileAssetId.ToString() });
            if (assetExists == 0)
            {
                return Result<IReadOnlyList<KnownFileLocation>>.Failure(
                    AppErrorCodes.NotFound,
                    "File asset was not found.");
            }

            IEnumerable<KnownLocationRow> rows = await connection.QueryAsync<KnownLocationRow>(
                """
                select location_id as LocationId, file_asset_id as FileAssetId, path as Path,
                       last_seen_at as LastSeenAt, status as Status
                from known_file_locations
                where file_asset_id = @FileAssetId
                order by last_seen_at desc, path;
                """,
                new { FileAssetId = fileAssetId.ToString() });

            return Result<IReadOnlyList<KnownFileLocation>>.Success(rows.Select(row => row.ToKnownLocation())
                .ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.file-resolution"))
        {
            return DatabaseFailure<IReadOnlyList<KnownFileLocation>>(exception);
        }
    }

    public async Task<Result<FileResolutionResult>> ResolveFileAsync(
        FileAssetId fileAssetId,
        ResolveFilePurpose purpose,
        CancellationToken cancellationToken = default)
    {
        _ = purpose;

        Result<LibraryMetadata> libraryResult = await _libraryIdentityService.GetCurrentLibraryAsync(cancellationToken);
        if (libraryResult.IsFailure)
        {
            return Result<FileResolutionResult>.Failure(libraryResult.ErrorCode!, libraryResult.ErrorMessage!);
        }

        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            FileAssetRow? asset = await GetFileAssetRowAsync(connection, fileAssetId);
            if (asset is null)
            {
                return Result<FileResolutionResult>.Failure(AppErrorCodes.NotFound, "File asset was not found.");
            }

            if (asset.LibraryId != libraryResult.Value.LibraryId.ToString())
            {
                return Result<FileResolutionResult>.Failure(
                    AppErrorCodes.LibraryMismatch,
                    "File asset belongs to a different library.");
            }

            PathEvaluation? original =
                await EvaluatePathAsync(asset, asset.OriginalPath, "original_path", cancellationToken);
            if (original?.Status == FileAssetStatus.Available)
            {
                return SuccessResolution(asset.FileAssetId, FileAssetStatus.Available, original.Path, [original]);
            }

            if (original?.Status == FileAssetStatus.Changed)
            {
                return ChangedResolution(
                    fileAssetId,
                    asset.OriginalPath,
                    [original],
                    "Original path exists, but size or quick_hash changed.");
            }

            KnownLocationRow[] knownLocations = (await connection.QueryAsync<KnownLocationRow>(
                """
                select location_id as LocationId, file_asset_id as FileAssetId, path as Path,
                       last_seen_at as LastSeenAt, status as Status
                from known_file_locations
                where file_asset_id = @FileAssetId
                order by last_seen_at desc;
                """,
                new { FileAssetId = fileAssetId.ToString() })).ToArray();

            List<PathEvaluation> knownCandidates = new();
            foreach (KnownLocationRow known in knownLocations)
            {
                if (Path.GetFullPath(known.Path) == Path.GetFullPath(asset.OriginalPath))
                {
                    continue;
                }

                PathEvaluation? candidate =
                    await EvaluatePathAsync(asset, known.Path, "known_location", cancellationToken);
                if (candidate is null)
                {
                    continue;
                }

                knownCandidates.Add(candidate);
                if (candidate.Status == FileAssetStatus.Available)
                {
                    return Result<FileResolutionResult>.Success(new FileResolutionResult(
                        fileAssetId,
                        FileAssetStatus.MovedCandidate,
                        candidate.Path,
                        [candidate],
                        FileResolutionConfidence.Exact,
                        FileResolutionRequiredAction.None,
                        "File was found at a known location that differs from original_path."));
                }
            }

            if (knownCandidates.Any(candidate => candidate.Status == FileAssetStatus.Changed))
            {
                return ChangedResolution(
                    fileAssetId,
                    asset.OriginalPath,
                    knownCandidates,
                    "Known location exists, but size or quick_hash changed.");
            }

            FileSearchRoot[] roots = (await connection.QueryAsync<SearchRootRow>(
                """
                select root_id as RootId, library_id as LibraryId, root_path as RootPath,
                       is_available as IsAvailable, created_at as CreatedAt, updated_at as UpdatedAt,
                       authorization_kind as AuthorizationKind, authorization_payload as AuthorizationPayload,
                       authorization_payload_version as AuthorizationPayloadVersion, authorization_updated_at as AuthorizationUpdatedAt
                from file_search_roots
                where library_id = @LibraryId;
                """,
                new { asset.LibraryId })).Select(row => row.ToSearchRoot()).ToArray();

            if (roots.Length > 0 && roots.All(root => !root.IsAvailable))
            {
                return Result<FileResolutionResult>.Success(new FileResolutionResult(
                    fileAssetId,
                    FileAssetStatus.OfflineRoot,
                    null,
                    [],
                    FileResolutionConfidence.None,
                    FileResolutionRequiredAction.ReconnectOfflineRoot,
                    "All configured file search roots are offline."));
            }

            List<FileResolutionCandidate> scannedCandidates =
                await ScanSearchRootsAsync(asset, roots.Where(root => root.IsAvailable), cancellationToken);
            if (scannedCandidates.Count == 1)
            {
                return Result<FileResolutionResult>.Success(new FileResolutionResult(
                    fileAssetId,
                    FileAssetStatus.MovedCandidate,
                    scannedCandidates[0].Path,
                    scannedCandidates,
                    FileResolutionConfidence.Exact,
                    FileResolutionRequiredAction.None,
                    "File was found under a search root."));
            }

            if (scannedCandidates.Count > 1)
            {
                return Result<FileResolutionResult>.Success(new FileResolutionResult(
                    fileAssetId,
                    FileAssetStatus.Conflict,
                    null,
                    scannedCandidates,
                    FileResolutionConfidence.High,
                    FileResolutionRequiredAction.ChooseCandidate,
                    "Multiple matching candidates were found.")
                {
                    Conflicts =
                    [
                        ConflictDescriptorMapper.FileRelocationMultipleCandidates(
                            fileAssetId,
                            asset.OriginalPath,
                            scannedCandidates)
                    ]
                });
            }

            return Result<FileResolutionResult>.Success(new FileResolutionResult(
                fileAssetId,
                FileAssetStatus.Missing,
                null,
                [],
                FileResolutionConfidence.None,
                FileResolutionRequiredAction.LocateManually,
                "No matching file was found."));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.file-resolution"))
        {
            return DatabaseFailure<FileResolutionResult>(exception);
        }
    }

    public Task<Result<FileAsset>> ConfirmMovedCandidateAsync(
        FileAssetId fileAssetId,
        string selectedPath,
        CancellationToken cancellationToken = default)
    {
        return ConfirmFileAsync(fileAssetId, selectedPath, false, false, cancellationToken);
    }

    public Task<Result<FileAsset>> RebindSourceAsync(
        FileAssetId fileAssetId,
        string selectedPath,
        CancellationToken cancellationToken = default)
    {
        return ConfirmFileAsync(fileAssetId, selectedPath, true, false, cancellationToken);
    }

    public Task<Result<FileAsset>> ConfirmChangedFileAsync(
        FileAssetId fileAssetId,
        string selectedPath,
        CancellationToken cancellationToken = default)
    {
        return ConfirmFileAsync(fileAssetId, selectedPath, false, true, cancellationToken);
    }

    private async Task<Result<FileAsset>> ConfirmFileAsync(
        FileAssetId fileAssetId,
        string selectedPath,
        bool requireOriginalFullFingerprint,
        bool markSourceBasisStale,
        CancellationToken cancellationToken,
        Func<SqliteConnection, DbTransaction, FileFingerprint, CancellationToken, Task<Result>>? afterFileAssetUpdated =
            null)
    {
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return Result<FileAsset>.Failure(AppErrorCodes.ValidationFailed, "Selected path is required.");
        }

        Result<FileFingerprint> fingerprint =
            await _fingerprintService.GetFileMetadataAsync(selectedPath, cancellationToken);
        if (fingerprint.IsFailure)
        {
            return Result<FileAsset>.Failure(fingerprint.ErrorCode!, fingerprint.ErrorMessage!);
        }

        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            FileAssetRow? asset = await GetFileAssetRowAsync(connection, fileAssetId, transaction);
            if (asset is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<FileAsset>.Failure(AppErrorCodes.NotFound, "File asset was not found.");
            }

            if (requireOriginalFullFingerprint &&
                (string.IsNullOrWhiteSpace(asset.FullBlake3) ||
                 string.IsNullOrWhiteSpace(fingerprint.Value.FullBlake3) ||
                 !string.Equals(asset.FullBlake3, fingerprint.Value.FullBlake3, StringComparison.Ordinal)))
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<FileAsset>.Failure(AppErrorCodes.ValidationFailed,
                    "Rebinding requires a path with the original complete file fingerprint.");
            }

            DateTimeOffset now = _clock.UtcNow.ToUniversalTime();
            await connection.ExecuteAsync(
                """
                update file_assets
                set original_path = @OriginalPath,
                    file_name = @FileName,
                    size_bytes = @SizeBytes,
                    mtime_utc = @MtimeUtc,
                    quick_hash = @QuickHash,
                    full_blake3 = @FullBlake3,
                    status = @Status,
                    updated_at = @UpdatedAt
                where file_asset_id = @FileAssetId;
                """,
                new
                {
                    FileAssetId = fileAssetId.ToString(),
                    OriginalPath = fingerprint.Value.Path,
                    fingerprint.Value.FileName,
                    fingerprint.Value.SizeBytes,
                    MtimeUtc = FormatUtc(fingerprint.Value.MtimeUtc),
                    fingerprint.Value.QuickHash,
                    fingerprint.Value.FullBlake3,
                    Status = FileAssetStatus.Available,
                    UpdatedAt = FormatUtc(now)
                },
                transaction);

            await FileAssetService.UpsertKnownLocationAsync(
                connection,
                transaction,
                fileAssetId,
                fingerprint.Value.Path,
                FileAssetStatus.Available,
                now);

            if (markSourceBasisStale)
            {
                await connection.ExecuteAsync(
                    """
                    insert into search_index_status (
                        scope_type, scope_id, status, pending_document_count, pending_unit_count,
                        progress_percent, affected_scopes_summary, reason, updated_at)
                    select 'document_instance', document_instance_id, 'stale', 1, 0,
                           null, 'Source file fingerprint changed', 'source_file_changed', @UpdatedAt
                    from document_instances
                    where file_asset_id = @FileAssetId
                    on conflict(scope_type, scope_id) do update set
                        status = 'stale',
                        pending_document_count = 1,
                        affected_scopes_summary = excluded.affected_scopes_summary,
                        reason = excluded.reason,
                        updated_at = excluded.updated_at;
                    """,
                    new
                    {
                        FileAssetId = fileAssetId.ToString(),
                        UpdatedAt = FormatUtc(now)
                    },
                    transaction);
            }

            if (afterFileAssetUpdated is not null)
            {
                Result followUp = await afterFileAssetUpdated(connection, transaction, fingerprint.Value,
                    cancellationToken);
                if (followUp.IsFailure)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Result<FileAsset>.Failure(followUp.ErrorCode!, followUp.ErrorMessage!, followUp.Conflicts);
                }
            }

            await transaction.CommitAsync(cancellationToken);

            return Result<FileAsset>.Success(new FileAsset(
                fileAssetId,
                LibraryId.Parse(asset.LibraryId),
                fingerprint.Value.Path,
                fingerprint.Value.FileName,
                fingerprint.Value.SizeBytes,
                fingerprint.Value.MtimeUtc,
                fingerprint.Value.QuickHash,
                fingerprint.Value.FullBlake3,
                asset.PageCount,
                asset.PdfTrailerId,
                FileAssetStatus.Available,
                DateTimeOffset.Parse(asset.CreatedAt),
                now));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.file-resolution"))
        {
            return DatabaseFailure<FileAsset>(exception);
        }
    }

    public async Task<Result> ReuseRevisionForNewFingerprintAsync(
        FileAssetId fileAssetId,
        string selectedPath,
        CancellationToken cancellationToken = default)
    {
        Result<FileAsset> confirmed = await ConfirmFileAsync(
            fileAssetId,
            selectedPath,
            true,
            true,
            cancellationToken);
        return confirmed.IsSuccess
            ? Result.Success()
            : Result.Failure(confirmed.ErrorCode!, confirmed.ErrorMessage!, confirmed.Conflicts);
    }

    public async Task<Result> KeepOldEvidenceAsync(
        FileAssetId fileAssetId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            int exists = await connection.ExecuteScalarAsync<int>(
                "select count(1) from file_assets where file_asset_id = @FileAssetId;",
                new { FileAssetId = fileAssetId.ToString() });
            return exists == 0
                ? Result.Failure(AppErrorCodes.NotFound, "File asset was not found.")
                : Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.file-resolution"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result> MarkFileMissingAsync(
        FileAssetId fileAssetId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            int affected = await connection.ExecuteAsync(
                """
                update file_assets
                set status = @Status, updated_at = @UpdatedAt
                where file_asset_id = @FileAssetId;
                """,
                new
                {
                    FileAssetId = fileAssetId.ToString(),
                    Status = FileAssetStatus.Missing,
                    UpdatedAt = FormatUtc(_clock.UtcNow)
                });

            return affected == 0
                ? Result.Failure(AppErrorCodes.NotFound, "File asset was not found.")
                : Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.file-resolution"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    private async Task<List<FileResolutionCandidate>> ScanSearchRootsAsync(
        FileAssetRow asset,
        IEnumerable<FileSearchRoot> roots,
        CancellationToken cancellationToken)
    {
        List<FileResolutionCandidate> candidates = new();
        foreach (FileSearchRoot root in roots)
        {
            Result<ResolvedFileSearchRoot> reopened = await _rootAccess.ReopenAsync(root, cancellationToken);
            if (reopened.IsFailure)
            {
                continue;
            }

            using (reopened.Value.AccessLease)
            {
                foreach (string path in (await _rootAccess.TraverseAsync(reopened.Value, cancellationToken)).Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FileInfo fileInfo = new(path);
                    if (!string.Equals(fileInfo.Name, asset.FileName, StringComparison.OrdinalIgnoreCase) ||
                        fileInfo.Length != asset.SizeBytes)
                    {
                        continue;
                    }

                    PathEvaluation? candidate = await EvaluatePathAsync(asset, path, "search_root", cancellationToken);
                    if (candidate?.Status == FileAssetStatus.Available)
                    {
                        candidates.Add(candidate);
                    }
                }
            }
        }

        return candidates;
    }

    private async Task<PathEvaluation?> EvaluatePathAsync(
        FileAssetRow asset,
        string path,
        string reason,
        CancellationToken cancellationToken)
    {
        Result materialized = await _rootAccess.EnsureAvailableAsync(path, cancellationToken);
        if (materialized.IsFailure)
        {
            return null;
        }

        if (!File.Exists(path))
        {
            return null;
        }

        Result<FileFingerprint> fingerprint = await _fingerprintService.GetFileMetadataAsync(path, cancellationToken);
        if (fingerprint.IsFailure)
        {
            return null;
        }

        bool quickHashMatches = string.Equals(fingerprint.Value.QuickHash, asset.QuickHash, StringComparison.Ordinal);
        bool sizeMatches = fingerprint.Value.SizeBytes == asset.SizeBytes;
        string status = sizeMatches && quickHashMatches ? FileAssetStatus.Available : FileAssetStatus.Changed;
        string confidence = status == FileAssetStatus.Available
            ? FileResolutionConfidence.Exact
            : FileResolutionConfidence.Low;

        return new PathEvaluation(
            fingerprint.Value.Path,
            fingerprint.Value.SizeBytes,
            fingerprint.Value.MtimeUtc,
            fingerprint.Value.QuickHash,
            fingerprint.Value.FullBlake3,
            confidence,
            reason,
            status);
    }

    private static Result<FileResolutionResult> SuccessResolution(
        string fileAssetId,
        string status,
        string resolvedPath,
        IReadOnlyList<FileResolutionCandidate> candidates)
    {
        return Result<FileResolutionResult>.Success(new FileResolutionResult(
            FileAssetId.Parse(fileAssetId),
            status,
            resolvedPath,
            candidates,
            FileResolutionConfidence.Exact,
            FileResolutionRequiredAction.None,
            null));
    }

    private static Result<FileResolutionResult> ChangedResolution(
        FileAssetId fileAssetId,
        string? originalPath,
        IReadOnlyList<FileResolutionCandidate> candidates,
        string warning)
    {
        return Result<FileResolutionResult>.Success(new FileResolutionResult(
            fileAssetId,
            FileAssetStatus.Changed,
            null,
            candidates,
            FileResolutionConfidence.Low,
            FileResolutionRequiredAction.ConfirmChangedFile,
            warning)
        {
            Conflicts =
            [
                ConflictDescriptorMapper.SourceFileChanged(
                    fileAssetId,
                    originalPath,
                    candidates,
                    warning)
            ]
        });
    }

    private static Task<FileAssetRow?> GetFileAssetRowAsync(
        SqliteConnection connection,
        FileAssetId fileAssetId,
        DbTransaction? transaction = null)
    {
        return connection.QuerySingleOrDefaultAsync<FileAssetRow>(
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
            new { FileAssetId = fileAssetId.ToString() },
            transaction);
    }

    private static object ToParameters(FileSearchRoot root)
    {
        return new
        {
            RootId = root.RootId.ToString(),
            LibraryId = root.LibraryId.ToString(),
            root.RootPath,
            IsAvailable = root.IsAvailable ? 1 : 0,
            CreatedAt = FormatUtc(root.CreatedAt),
            UpdatedAt = FormatUtc(root.UpdatedAt), root.AuthorizationKind, root.AuthorizationPayload,
            root.AuthorizationPayloadVersion,
            AuthorizationUpdatedAt =
                root.AuthorizationUpdatedAt is null ? null : FormatUtc(root.AuthorizationUpdatedAt.Value)
        };
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O");
    }

    private static Result<T> DatabaseFailure<T>(Exception exception)
    {
        return Result<T>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
    }

    private async Task<BlockingOperationId?> TryStartRootScanAsync(
        string rootPath,
        CancellationToken cancellationToken)
    {
        if (_blockingOperations is null)
        {
            return null;
        }

        try
        {
            Result<BlockingOperation> started = await _blockingOperations.StartAsync(
                BlockingOperationTypes.FileSearchRootScan,
                BlockingOperationScopeTypes.FileSearchRoot,
                rootPath,
                true,
                "Scanning file search root.",
                nextActions: ["Reconnect the search root", "Retry registering the search root"],
                cancellationToken: cancellationToken);
            return started.IsSuccess ? started.Value.OperationId : null;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.file-resolution", "complete-root-scan-operation"))
        {
            return null;
        }
    }

    private async Task TryCompleteRootScanAsync(
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
                                              "infrastructure.file-resolution", "fail-root-scan-operation"))
        {
        }
    }

    private async Task TryFailRootScanAsync(
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
                                              "infrastructure.file-resolution", "fail-root-scan-operation"))
        {
            _ = exception;
        }
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
    }


    private sealed class SearchRootRow
    {
        public string RootId { get; set; } = string.Empty;
        public string LibraryId { get; set; } = string.Empty;
        public string RootPath { get; set; } = string.Empty;
        public int IsAvailable { get; set; }
        public string CreatedAt { get; set; } = string.Empty;
        public string UpdatedAt { get; set; } = string.Empty;
        public string? AuthorizationKind { get; set; }
        public byte[]? AuthorizationPayload { get; set; }
        public int? AuthorizationPayloadVersion { get; set; }
        public string? AuthorizationUpdatedAt { get; set; }

        public FileSearchRoot ToSearchRoot()
        {
            return new FileSearchRoot(
                FileSearchRootId.Parse(RootId),
                Patchouli.Core.Ids.LibraryId.Parse(LibraryId),
                RootPath,
                IsAvailable == 1,
                DateTimeOffset.Parse(CreatedAt),
                DateTimeOffset.Parse(UpdatedAt),
                AuthorizationKind,
                AuthorizationPayload,
                AuthorizationPayloadVersion,
                AuthorizationUpdatedAt is null ? null : DateTimeOffset.Parse(AuthorizationUpdatedAt));
        }
    }

    private sealed class KnownLocationRow
    {
        public string LocationId { get; set; } = string.Empty;
        public string FileAssetId { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string LastSeenAt { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;

        public KnownFileLocation ToKnownLocation()
        {
            return new KnownFileLocation(
                KnownFileLocationId.Parse(LocationId),
                Patchouli.Core.Ids.FileAssetId.Parse(FileAssetId),
                Path,
                DateTimeOffset.Parse(LastSeenAt),
                Status);
        }
    }

    private sealed record PathEvaluation(
        string Path,
        long SizeBytes,
        DateTimeOffset? MtimeUtc,
        string? QuickHash,
        string? FullBlake3,
        string Confidence,
        string Reason,
        string Status)
        : FileResolutionCandidate(Path, SizeBytes, MtimeUtc, QuickHash, FullBlake3, Confidence, Reason);
}
