using Dapper;
using Patchouli.Core.Files;
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
    private const int MaxScannedFiles = 5000;

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly ILibraryIdentityService _libraryIdentityService;
    private readonly IClock _clock;
    private readonly IFileFingerprintService _fingerprintService;
    private readonly IBlockingOperationService? _blockingOperations;

    public FileResolutionService(
        SqliteConnectionFactory connectionFactory,
        ILibraryIdentityService libraryIdentityService,
        IClock clock,
        IFileFingerprintService? fingerprintService = null,
        IBlockingOperationService? blockingOperations = null)
    {
        _connectionFactory = connectionFactory;
        _libraryIdentityService = libraryIdentityService;
        _clock = clock;
        _fingerprintService = fingerprintService ?? new FileFingerprintService();
        _blockingOperations = blockingOperations;
    }

    public async Task<Result<FileSearchRoot>> AddSearchRootAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return Result<FileSearchRoot>.Failure(AppErrorCodes.ValidationFailed, "Search root path is required.");
        }

        var libraryResult = await _libraryIdentityService.GetCurrentLibraryAsync(cancellationToken);
        if (libraryResult.IsFailure)
        {
            return Result<FileSearchRoot>.Failure(libraryResult.ErrorCode!, libraryResult.ErrorMessage!);
        }

        try
        {
            var now = _clock.UtcNow.ToUniversalTime();
            var normalizedRootPath = Path.GetFullPath(rootPath);
            var isRootAvailable = Directory.Exists(normalizedRootPath);
            var root = new FileSearchRoot(
                FileSearchRootId.New(),
                libraryResult.Value.LibraryId,
                normalizedRootPath,
                isRootAvailable,
                now,
                now);
            var scanOperationId = await TryStartRootScanAsync(root.RootPath, cancellationToken);

            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var duplicate = await connection.ExecuteScalarAsync<int>(
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
                insert into file_search_roots (root_id, library_id, root_path, is_available, created_at, updated_at)
                values (@RootId, @LibraryId, @RootPath, @IsAvailable, @CreatedAt, @UpdatedAt);
                """,
                ToParameters(root),
                transaction);

            var scanSummary = ScanRoot(root.RootPath, cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            if (!root.IsAvailable)
            {
                await TryFailRootScanAsync(
                    scanOperationId,
                    AppErrorCodes.NotFound,
                    "Search root directory is not currently available.",
                    "Search root scan did not run because the directory is unavailable.",
                    ["Reconnect the search root", "Retry the root scan after the directory is available"],
                    cancellationToken);
            }
            else
            {
                await TryCompleteRootScanAsync(
                    scanOperationId,
                    scanSummary.HitScanLimit
                        ? $"Scanned {scanSummary.ScannedFileCount} file(s) while registering the search root before hitting the scan cap."
                        : $"Scanned {scanSummary.ScannedFileCount} file(s) while registering the search root.",
                    cancellationToken);
            }

            return Result<FileSearchRoot>.Success(root);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return DatabaseFailure<FileSearchRoot>(exception);
        }
    }

    public async Task<Result<IReadOnlyList<FileSearchRoot>>> ListSearchRootsAsync(
        CancellationToken cancellationToken = default)
    {
        var libraryResult = await _libraryIdentityService.GetCurrentLibraryAsync(cancellationToken);
        if (libraryResult.IsFailure)
        {
            return Result<IReadOnlyList<FileSearchRoot>>.Failure(libraryResult.ErrorCode!, libraryResult.ErrorMessage!);
        }

        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            var rows = await connection.QueryAsync<SearchRootRow>(
                """
                select root_id as RootId, library_id as LibraryId, root_path as RootPath,
                       is_available as IsAvailable, created_at as CreatedAt, updated_at as UpdatedAt
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
        catch (Exception exception)
        {
            return DatabaseFailure<IReadOnlyList<FileSearchRoot>>(exception);
        }
    }

    public async Task<Result> SetSearchRootAvailabilityAsync(
        FileSearchRootId rootId,
        bool isAvailable,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            var affected = await connection.ExecuteAsync(
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
        catch (Exception exception)
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
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            var assetExists = await connection.ExecuteScalarAsync<int>(
                "select count(1) from file_assets where file_asset_id = @FileAssetId;",
                new { FileAssetId = fileAssetId.ToString() });
            if (assetExists == 0)
            {
                return Result<IReadOnlyList<KnownFileLocation>>.Failure(
                    AppErrorCodes.NotFound,
                    "File asset was not found.");
            }

            var rows = await connection.QueryAsync<KnownLocationRow>(
                """
                select location_id as LocationId, file_asset_id as FileAssetId, path as Path,
                       last_seen_at as LastSeenAt, status as Status
                from known_file_locations
                where file_asset_id = @FileAssetId
                order by last_seen_at desc, path;
                """,
                new { FileAssetId = fileAssetId.ToString() });

            return Result<IReadOnlyList<KnownFileLocation>>.Success(rows.Select(row => row.ToKnownLocation()).ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
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

        var libraryResult = await _libraryIdentityService.GetCurrentLibraryAsync(cancellationToken);
        if (libraryResult.IsFailure)
        {
            return Result<FileResolutionResult>.Failure(libraryResult.ErrorCode!, libraryResult.ErrorMessage!);
        }

        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            var asset = await GetFileAssetRowAsync(connection, fileAssetId);
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

            var original = await EvaluatePathAsync(asset, asset.OriginalPath, "original_path", cancellationToken);
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

            var knownLocations = (await connection.QueryAsync<KnownLocationRow>(
                """
                select location_id as LocationId, file_asset_id as FileAssetId, path as Path,
                       last_seen_at as LastSeenAt, status as Status
                from known_file_locations
                where file_asset_id = @FileAssetId
                order by last_seen_at desc;
                """,
                new { FileAssetId = fileAssetId.ToString() })).ToArray();

            var knownCandidates = new List<PathEvaluation>();
            foreach (var known in knownLocations)
            {
                if (Path.GetFullPath(known.Path) == Path.GetFullPath(asset.OriginalPath))
                {
                    continue;
                }

                var candidate = await EvaluatePathAsync(asset, known.Path, "known_location", cancellationToken);
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

            var roots = (await connection.QueryAsync<SearchRootRow>(
                """
                select root_id as RootId, library_id as LibraryId, root_path as RootPath,
                       is_available as IsAvailable, created_at as CreatedAt, updated_at as UpdatedAt
                from file_search_roots
                where library_id = @LibraryId;
                """,
                new { asset.LibraryId })).Select(row => row.ToSearchRoot()).ToArray();

            if (roots.Length > 0 && roots.All(root => !root.IsAvailable))
            {
                return Result<FileResolutionResult>.Success(new FileResolutionResult(
                    fileAssetId,
                    FileAssetStatus.OfflineRoot,
                    ResolvedPath: null,
                    Candidates: [],
                    FileResolutionConfidence.None,
                    FileResolutionRequiredAction.ReconnectOfflineRoot,
                    "All configured file search roots are offline."));
            }

            var scannedCandidates = await ScanSearchRootsAsync(asset, roots.Where(root => root.IsAvailable), cancellationToken);
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
                    ResolvedPath: null,
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
                ResolvedPath: null,
                Candidates: [],
                FileResolutionConfidence.None,
                FileResolutionRequiredAction.LocateManually,
                "No matching file was found."));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return DatabaseFailure<FileResolutionResult>(exception);
        }
    }

    public async Task<Result<FileAsset>> ConfirmMovedCandidateAsync(
        FileAssetId fileAssetId,
        string selectedPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return Result<FileAsset>.Failure(AppErrorCodes.ValidationFailed, "Selected path is required.");
        }

        var fingerprint = await _fingerprintService.GetFileMetadataAsync(selectedPath, cancellationToken);
        if (fingerprint.IsFailure)
        {
            return Result<FileAsset>.Failure(fingerprint.ErrorCode!, fingerprint.ErrorMessage!);
        }

        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var asset = await GetFileAssetRowAsync(connection, fileAssetId, transaction);
            if (asset is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<FileAsset>.Failure(AppErrorCodes.NotFound, "File asset was not found.");
            }

            var now = _clock.UtcNow.ToUniversalTime();
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

            await transaction.CommitAsync(cancellationToken);

            return Result<FileAsset>.Success(new FileAsset(
                fileAssetId,
                Patchouli.Core.Ids.LibraryId.Parse(asset.LibraryId),
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
        catch (Exception exception)
        {
            return DatabaseFailure<FileAsset>(exception);
        }
    }

    public async Task<Result> MarkFileMissingAsync(
        FileAssetId fileAssetId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            var affected = await connection.ExecuteAsync(
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
        catch (Exception exception)
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    private async Task<List<FileResolutionCandidate>> ScanSearchRootsAsync(
        FileAssetRow asset,
        IEnumerable<FileSearchRoot> roots,
        CancellationToken cancellationToken)
    {
        var candidates = new List<FileResolutionCandidate>();
        var scanned = 0;

        foreach (var root in roots)
        {
            if (!Directory.Exists(root.RootPath))
            {
                continue;
            }

            foreach (var path in EnumerateFilesSafely(root.RootPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                scanned++;
                if (scanned > MaxScannedFiles)
                {
                    return candidates;
                }

                var fileInfo = new FileInfo(path);
                if (!string.Equals(fileInfo.Name, asset.FileName, StringComparison.OrdinalIgnoreCase) ||
                    fileInfo.Length != asset.SizeBytes)
                {
                    continue;
                }

                var candidate = await EvaluatePathAsync(asset, path, "search_root", cancellationToken);
                if (candidate?.Status == FileAssetStatus.Available)
                {
                    candidates.Add(candidate);
                }
            }
        }

        return candidates;
    }

    private RootScanSummary ScanRoot(string rootPath, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(rootPath))
        {
            return new RootScanSummary(0, false);
        }

        var scannedFileCount = 0;
        foreach (var _ in EnumerateFilesSafely(rootPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            scannedFileCount++;
            if (scannedFileCount >= MaxScannedFiles)
            {
                return new RootScanSummary(scannedFileCount, true);
            }
        }

        return new RootScanSummary(scannedFileCount, false);
    }

    private static IEnumerable<string> EnumerateFilesSafely(string rootPath)
    {
        var pending = new Stack<string>();
        pending.Push(rootPath);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            IEnumerable<string> subdirectories;
            try
            {
                subdirectories = Directory.EnumerateDirectories(directory);
            }
            catch
            {
                continue;
            }

            foreach (var subdirectory in subdirectories)
            {
                var name = Path.GetFileName(subdirectory);
                if (name.StartsWith(".", StringComparison.Ordinal))
                {
                    continue;
                }

                pending.Push(subdirectory);
            }
        }
    }

    private async Task<PathEvaluation?> EvaluatePathAsync(
        FileAssetRow asset,
        string path,
        string reason,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var fingerprint = await _fingerprintService.GetFileMetadataAsync(path, cancellationToken);
        if (fingerprint.IsFailure)
        {
            return null;
        }

        var quickHashMatches = string.Equals(fingerprint.Value.QuickHash, asset.QuickHash, StringComparison.Ordinal);
        var sizeMatches = fingerprint.Value.SizeBytes == asset.SizeBytes;
        var status = sizeMatches && quickHashMatches ? FileAssetStatus.Available : FileAssetStatus.Changed;
        var confidence = status == FileAssetStatus.Available
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
            Warning: null));
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
            ResolvedPath: null,
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
        Microsoft.Data.Sqlite.SqliteConnection connection,
        FileAssetId fileAssetId,
        System.Data.Common.DbTransaction? transaction = null)
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
            UpdatedAt = FormatUtc(root.UpdatedAt)
        };
    }

    private static string FormatUtc(DateTimeOffset value) => value.ToUniversalTime().ToString("O");

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
            var started = await _blockingOperations.StartAsync(
                BlockingOperationTypes.FileSearchRootScan,
                BlockingOperationScopeTypes.FileSearchRoot,
                rootPath,
                canCancel: true,
                progressLabel: "Scanning file search root.",
                nextActions: ["Reconnect the search root", "Retry registering the search root"],
                cancellationToken: cancellationToken);
            return started.IsSuccess ? started.Value.OperationId : null;
        }
        catch
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
        catch
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
        catch
        {
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

        public FileSearchRoot ToSearchRoot()
        {
            return new FileSearchRoot(
                FileSearchRootId.Parse(RootId),
                Patchouli.Core.Ids.LibraryId.Parse(LibraryId),
                RootPath,
                IsAvailable == 1,
                DateTimeOffset.Parse(CreatedAt),
                DateTimeOffset.Parse(UpdatedAt));
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

    private sealed record RootScanSummary(
        int ScannedFileCount,
        bool HitScanLimit);
}
