using Patchouli.Core.Csl;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Files;
using Patchouli.Core.Import;
using Patchouli.Core.Results;
using Patchouli.Core.Settings;
using Patchouli.Core.Time;

namespace Patchouli.Infrastructure.Import;

/// <summary>
/// Issue #11: shared import core for MCP <c>add</c>, the CLI, scan import and window
/// drag-drop. Action entries pick the file handling behavior through
/// <see cref="IFileHandlingBehaviorSettingsService"/>; this service owns path validation,
/// file handling, fingerprinting, dedup and ingestion. The atomic PDF import (Item,
/// FileAsset, DocumentInstance, physical pages committed per book, per-page failure
/// tolerance) is reused through <see cref="IPdfImportWorkflow"/>; ViewModels hold no
/// second import logic.
/// </summary>
public sealed class FileImportService : IFileImportService
{
    private const int SniffByteCount = 16384;
    private const int MovePlanRetries = 3;

    private readonly IPdfImportWorkflow _pdfImport;
    private readonly ICslLocalFileInstallService _cslInstall;
    private readonly IFileHandlingBehaviorSettingsService _behaviorSettings;
    private readonly IFileResolutionService _fileResolution;
    private readonly IFileFingerprintService _fingerprints;
    private readonly IFileImportRecoveryStore _recovery;
    private readonly IClock _clock;

    public FileImportService(
        IPdfImportWorkflow pdfImport,
        ICslLocalFileInstallService cslInstall,
        IFileHandlingBehaviorSettingsService behaviorSettings,
        IFileResolutionService fileResolution,
        IFileFingerprintService fingerprints,
        IFileImportRecoveryStore recovery,
        IClock clock)
    {
        _pdfImport = pdfImport;
        _cslInstall = cslInstall;
        _behaviorSettings = behaviorSettings;
        _fileResolution = fileResolution;
        _fingerprints = fingerprints;
        _recovery = recovery;
        _clock = clock;
    }

    public async Task<Result<FileImportResult>> ImportAsync(
        FileImportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Result<ValidatedImportFile> validated = await ValidateLocalPathAsync(request, cancellationToken);
        if (validated.IsFailure)
        {
            return Result<FileImportResult>.Failure(validated.ErrorCode!, validated.ErrorMessage!);
        }

        ValidatedImportFile file = validated.Value;
        Result<LocalImportKind> kindResult = ResolveKind(request.Kind, file);
        if (kindResult.IsFailure)
        {
            return Result<FileImportResult>.Failure(kindResult.ErrorCode!, kindResult.ErrorMessage!);
        }

        LocalImportKind kind = kindResult.Value;
        Result<FileHandlingBehavior> behaviorResult =
            await ResolveBehaviorAsync(request.Trigger, kind, cancellationToken);
        if (behaviorResult.IsFailure)
        {
            return Result<FileImportResult>.Failure(behaviorResult.ErrorCode!, behaviorResult.ErrorMessage!);
        }

        // Authorization + config checks complete before any file move or ingestion. Path
        // validation and content sniffing above are read-only; mutations start below.
        if (request.AuthorizeKindAsync is not null)
        {
            Result authorized = await request.AuthorizeKindAsync(kind);
            if (authorized.IsFailure)
            {
                return Result<FileImportResult>.Failure(authorized.ErrorCode!, authorized.ErrorMessage!);
            }
        }

        string importPath = file.FullPath;
        bool wasMoved = false;
        if (kind == LocalImportKind.Pdf && behaviorResult.Value == FileHandlingBehavior.MoveToFirstSearchRoot)
        {
            Result<MoveOutcome> move = await MoveToFirstSearchRootAsync(file, request.Trigger, cancellationToken);
            if (move.IsFailure)
            {
                return Result<FileImportResult>.Failure(move.ErrorCode!, move.ErrorMessage!);
            }

            importPath = move.Value.FinalPath;
            wasMoved = move.Value.PhysicallyMoved;
        }

        // CSL follows style-install rules (read + install); it is never moved to file
        // search roots.
        Result<FileImportResult> ingested;
        try
        {
            ingested = kind == LocalImportKind.Pdf
                ? await ImportPdfCoreAsync(request.Trigger, importPath, wasMoved, cancellationToken)
                : await InstallCslCoreAsync(importPath, cancellationToken);
        }
        catch (OperationCanceledException) when (wasMoved)
        {
            // P1-4: the file was already moved but the import was cancelled (e.g. during
            // fingerprint computation). Restore with a non-cancelled token so cleanup is
            // not itself cancelled, and record the outcome durably.
            await RestoreAfterCancelAsync(file.FullPath, importPath, request.Trigger, kind,
                CancellationToken.None);
            throw;
        }

        if (ingested.IsFailure && wasMoved)
        {
            return await RecoverAfterFailedImportAsync(file.FullPath, importPath, request.Trigger, kind,
                ingested, cancellationToken);
        }

        return ingested;
    }

    /// <summary>
    /// P1-4: restores a moved file after the import was cancelled. Uses a non-cancelled
    /// token for cleanup; records whether the restore succeeded so no file is silently lost.
    /// </summary>
    private async Task RestoreAfterCancelAsync(
        string originalPath,
        string movedPath,
        FileImportTrigger trigger,
        LocalImportKind kind,
        CancellationToken cleanupToken)
    {
        Result restore = await MoveFileAsync(movedPath, originalPath, cleanupToken);
        if (restore.IsSuccess)
        {
            await RecordRecoveryAsync(trigger, kind, originalPath, movedPath, originalPath,
                FileImportRecoveryStatuses.ImportFailedRestored,
                "Import was cancelled after the file was moved; the file was restored to its original position.",
                cleanupToken);
            return;
        }

        await RecordRecoveryAsync(trigger, kind, originalPath, movedPath, null,
            FileImportRecoveryStatuses.ImportFailedRestoreFailed,
            "Import was cancelled after the file was moved; restoring the file to its original " +
            $"position failed: {restore.ErrorMessage}. The file was kept at its moved location.",
            cleanupToken);
    }

    private sealed record ValidatedImportFile(string FullPath, string FileName, byte[] LeadingBytes);

    private sealed record MoveOutcome(string FinalPath, bool PhysicallyMoved);

    private static async Task<Result<ValidatedImportFile>> ValidateLocalPathAsync(
        FileImportRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.LocalPath))
        {
            return Result<ValidatedImportFile>.Failure(AppErrorCodes.InvalidArgument, "path is required.");
        }

        if (FileContentSniffer.IsUrlLike(request.LocalPath))
        {
            return Result<ValidatedImportFile>.Failure(FileImportErrorCodes.UrlNotAllowed,
                "MCP add accepts only host-local file paths, never URLs. Download the file with your own " +
                "ability first, then call add with the local path.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(request.LocalPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              ArgumentException or NotSupportedException)
        {
            return Result<ValidatedImportFile>.Failure(AppErrorCodes.InvalidArgument,
                "The local path is not a valid file path.");
        }

        string fileName = Path.GetFileName(fullPath);
        if (Directory.Exists(fullPath))
        {
            return Result<ValidatedImportFile>.Failure(FileImportErrorCodes.NotRegularFile,
                "Directories cannot be imported; pass a single file path.");
        }

        if (!File.Exists(fullPath))
        {
            return Result<ValidatedImportFile>.Failure(FileImportErrorCodes.PathNotFound,
                $"Local file '{fileName}' was not found on the host.");
        }

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(fullPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result<ValidatedImportFile>.Failure(FileImportErrorCodes.UnreadableFile,
                $"Local file '{fileName}' could not be accessed.");
        }

        byte[] leading;
        try
        {
            await using FileStream stream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            leading = new byte[SniffByteCount];
            int read = await stream.ReadAsync(leading, cancellationToken);
            if (read < leading.Length)
            {
                Array.Resize(ref leading, read);
            }
        }
        catch (UnauthorizedAccessException)
        {
            return Result<ValidatedImportFile>.Failure(FileImportErrorCodes.UnreadableFile,
                $"Local file '{fileName}' is not readable.");
        }
        catch (IOException exception)
        {
            bool placeholder = (attributes & (FileAttributes.Offline | FileAttributes.ReparsePoint)) != 0;
            return Result<ValidatedImportFile>.Failure(
                placeholder ? FileImportErrorCodes.CloudPlaceholderUnavailable : FileImportErrorCodes.UnreadableFile,
                placeholder
                    ? $"Local file '{fileName}' is a cloud placeholder whose content is not available on the host."
                    : $"Local file '{fileName}' could not be read: {SanitizeIoMessage(exception.Message)}");
        }

        return Result<ValidatedImportFile>.Success(new ValidatedImportFile(fullPath, fileName, leading));
    }

    private static string SanitizeIoMessage(string message)
    {
        // Never leak host paths: keep only the first line and strip anything that looks
        // like an absolute path is impossible here, so truncate defensively.
        string firstLine = message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return firstLine.Length > 200 ? firstLine[..200] : firstLine;
    }

    private static Result<LocalImportKind> ResolveKind(string? explicitKind, ValidatedImportFile file)
    {
        if (!FileContentSniffer.TryParseKind(explicitKind, out LocalImportKind parsed))
        {
            return Result<LocalImportKind>.Failure(AppErrorCodes.InvalidArgument,
                "kind must be \"pdf\" or \"csl\".");
        }

        LocalImportKind sniffed = FileContentSniffer.SniffKind(file.LeadingBytes);
        if (parsed != LocalImportKind.Unknown && sniffed != LocalImportKind.Unknown && parsed != sniffed)
        {
            return Result<LocalImportKind>.Failure(FileImportErrorCodes.KindMismatch,
                $"The explicit kind '{explicitKind}' does not match the actual file content.");
        }

        LocalImportKind kind = parsed != LocalImportKind.Unknown ? parsed : sniffed;
        if (kind == LocalImportKind.Unknown)
        {
            return Result<LocalImportKind>.Failure(FileImportErrorCodes.UnsupportedFormat,
                $"Local file '{file.FileName}' is not a supported import format (PDF or CSL).");
        }

        return Result<LocalImportKind>.Success(kind);
    }

    private async Task<Result<FileHandlingBehavior>> ResolveBehaviorAsync(
        FileImportTrigger trigger,
        LocalImportKind kind,
        CancellationToken cancellationToken)
    {
        // CSL follows style-install rules (read + install); it is never moved to file
        // search roots regardless of the PDF behavior settings.
        if (kind == LocalImportKind.Csl)
        {
            return Result<FileHandlingBehavior>.Success(FileHandlingBehavior.KeepInPlace);
        }

        if (trigger == FileImportTrigger.Scan)
        {
            return Result<FileHandlingBehavior>.Success(FileHandlingBehavior.KeepInPlace);
        }

        Result<FileHandlingBehaviorSettings> settings =
            await _behaviorSettings.GetSettingsAsync(cancellationToken);
        if (settings.IsFailure)
        {
            return Result<FileHandlingBehavior>.Failure(settings.ErrorCode!, settings.ErrorMessage!);
        }

        return Result<FileHandlingBehavior>.Success(trigger switch
        {
            FileImportTrigger.McpAdd => settings.Value.McpAddPdfBehavior,
            FileImportTrigger.DragDrop => settings.Value.PdfDragDropBehavior,
            _ => FileHandlingBehavior.KeepInPlace
        });
    }

    private async Task<Result<MoveOutcome>> MoveToFirstSearchRootAsync(
        ValidatedImportFile file,
        FileImportTrigger trigger,
        CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<FileSearchRoot>> roots = await _fileResolution.ListSearchRootsAsync(cancellationToken);
        if (roots.IsFailure)
        {
            return Result<MoveOutcome>.Failure(roots.ErrorCode!, roots.ErrorMessage!);
        }

        if (roots.Value.Count == 0)
        {
            return Result<MoveOutcome>.Failure(FileImportErrorCodes.NoSearchRoot,
                "Move behavior is enabled but no file search root is configured.");
        }

        // The roots arrive in persisted sort order; the first root is the move target.
        // Never silently use another root or fall back to in-place.
        FileSearchRoot first = roots.Value[0];
        Result<string> rootPath = await VerifyMoveTargetRootAsync(first, cancellationToken);
        if (rootPath.IsFailure)
        {
            return Result<MoveOutcome>.Failure(rootPath.ErrorCode!, rootPath.ErrorMessage!);
        }

        Result<MoveCandidateIdentity> identity = await ProbeIdentityAsync(file.FullPath, cancellationToken);
        if (identity.IsFailure)
        {
            return Result<MoveOutcome>.Failure(identity.ErrorCode!, identity.ErrorMessage!);
        }

        string rootFull = rootPath.Value;
        for (int attempt = 0; attempt < MovePlanRetries; attempt++)
        {
            MoveTargetPlan planned = await MoveTargetNamer.PlanAsync(
                file.FullPath,
                rootFull,
                identity.Value,
                candidate => ProbeCandidateIdentityAsync(candidate, cancellationToken),
                cancellationToken);
            if (!planned.Moved)
            {
                // Already inside the target root, or identical content already occupies
                // the name: no physical move.
                return Result<MoveOutcome>.Success(new MoveOutcome(planned.TargetPath, false));
            }

            Result move = await MoveFileAsync(file.FullPath, planned.TargetPath, cancellationToken);
            if (move.IsSuccess)
            {
                return Result<MoveOutcome>.Success(new MoveOutcome(planned.TargetPath, true));
            }

            if (move.ErrorCode == FileImportErrorCodes.MovePartialFailure)
            {
                // Cross-volume copy succeeded but source deletion failed. The target
                // exists because WE created it — not a concurrent conflict. Do not
                // retry; record the partial failure durably.
                await RecordRecoveryAsync(trigger, LocalImportKind.Pdf, file.FullPath, planned.TargetPath, null,
                    FileImportRecoveryStatuses.MoveFailed,
                    $"Move to the first file search root partially failed: {move.ErrorMessage}",
                    cancellationToken);
                return Result<MoveOutcome>.Failure(move.ErrorCode!, move.ErrorMessage!);
            }

            if (move.ErrorCode == FileImportErrorCodes.MoveFailed && File.Exists(planned.TargetPath))
            {
                // A concurrent import won the planned name; replan deterministically.
                continue;
            }

            await RecordRecoveryAsync(trigger, LocalImportKind.Pdf, file.FullPath, planned.TargetPath, null,
                FileImportRecoveryStatuses.MoveFailed,
                $"Move to the first file search root failed: {move.ErrorMessage}",
                cancellationToken);
            return Result<MoveOutcome>.Failure(move.ErrorCode!, move.ErrorMessage!);
        }

        return Result<MoveOutcome>.Failure(FileImportErrorCodes.MoveFailed,
            "Could not plan a deterministic move target after concurrent imports; retry the import.");
    }

    private async Task<MoveCandidateIdentity?> ProbeCandidateIdentityAsync(
        string candidate,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(candidate))
            {
                return null;
            }

            long size = new FileInfo(candidate).Length;
            Result<string> quick = await _fingerprints.ComputeQuickHashAsync(candidate, cancellationToken);
            return quick.IsFailure ? null : new MoveCandidateIdentity(size, quick.Value);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<Result<MoveCandidateIdentity>> ProbeIdentityAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            long size = new FileInfo(path).Length;
            Result<string> quick = await _fingerprints.ComputeQuickHashAsync(path, cancellationToken);
            return quick.IsFailure
                ? Result<MoveCandidateIdentity>.Failure(quick.ErrorCode!, quick.ErrorMessage!)
                : Result<MoveCandidateIdentity>.Success(new MoveCandidateIdentity(size, quick.Value));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result<MoveCandidateIdentity>.Failure(FileImportErrorCodes.UnreadableFile,
                "The local file could not be read for move planning.");
        }
    }

    private static async Task<Result<string>> VerifyMoveTargetRootAsync(
        FileSearchRoot root,
        CancellationToken cancellationToken)
    {
        string rootFull;
        try
        {
            rootFull = Path.GetFullPath(root.RootPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              ArgumentException or NotSupportedException)
        {
            return Result<string>.Failure(FileImportErrorCodes.SearchRootUnavailable,
                "The first file search root has an invalid path.");
        }

        if (!root.IsAvailable || !Directory.Exists(rootFull))
        {
            return Result<string>.Failure(FileImportErrorCodes.SearchRootUnavailable,
                "The first file search root is not available.");
        }

        // Explicit writability probe: a read-only mount must fail here, never silently.
        string probe = Path.Combine(rootFull, $".patchouli-write-test-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllBytesAsync(probe, [1, 2, 3], cancellationToken);
            File.Delete(probe);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result<string>.Failure(FileImportErrorCodes.SearchRootUnavailable,
                "The first file search root is not writable.");
        }

        return Result<string>.Success(rootFull);
    }

    /// <summary>
    /// Moves the source to the target. Same-volume moves are atomic renames; cross-volume
    /// moves copy, verify, then delete the source — the source is never deleted before
    /// the copy is verified, so a failure never loses the file.
    /// </summary>
    private static async Task<Result> MoveFileAsync(
        string source,
        string target,
        CancellationToken cancellationToken)
    {
        try
        {
            string? directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string sourceRoot = Path.GetPathRoot(Path.GetFullPath(source)) ?? string.Empty;
            string targetRoot = Path.GetPathRoot(Path.GetFullPath(target)) ?? string.Empty;
            bool sameVolume = OperatingSystem.IsWindows()
                ? string.Equals(sourceRoot, targetRoot, StringComparison.OrdinalIgnoreCase)
                : string.Equals(sourceRoot, targetRoot, StringComparison.Ordinal);

            if (sameVolume)
            {
                File.Move(source, target, overwrite: false);
                return Result.Success();
            }

            long sourceLength = new FileInfo(source).Length;
            await Task.Run(() => File.Copy(source, target, overwrite: false), cancellationToken);
            long targetLength = new FileInfo(target).Length;
            if (targetLength != sourceLength)
            {
                TryDelete(target);
                return Result.Failure(FileImportErrorCodes.MoveFailed,
                    "Cross-volume copy verification failed; the source file was kept in place.");
            }

            try
            {
                File.Delete(source);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The copy is complete and verified; the source could not be removed.
                // Never claim success, never lose track of either copy. This is a
                // partial failure, NOT a concurrent naming conflict — the target was
                // created by this move, so the caller must not retry as if someone
                // else won the name.
                return Result.Failure(FileImportErrorCodes.MovePartialFailure,
                    "The file was copied to the search root but the source could not be removed; " +
                    "the file now exists at both locations.");
            }

            return Result.Success();
        }
        catch (IOException exception)
        {
            return Result.Failure(FileImportErrorCodes.MoveFailed,
                $"The file could not be moved: {SanitizeIoMessage(exception.Message)}");
        }
        catch (UnauthorizedAccessException)
        {
            return Result.Failure(FileImportErrorCodes.MoveFailed,
                "The file could not be moved: access was denied.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _ = exception;
        }
    }

    private async Task<Result<FileImportResult>> ImportPdfCoreAsync(
        FileImportTrigger trigger,
        string path,
        bool wasMoved,
        CancellationToken cancellationToken)
    {
        PdfImportResult imported =
            await _pdfImport.ImportPdfAsync(new PdfImportRequest(path, null, null, null), cancellationToken);
        if (!imported.Success)
        {
            if (!wasMoved)
            {
                await RecordRecoveryAsync(trigger, LocalImportKind.Pdf, path, null, path,
                    FileImportRecoveryStatuses.ImportFailedNoMove,
                    $"Import failed ({FileImportErrorCodes.ImportFailed}); the file was not moved and remains at its original position.",
                    cancellationToken);
            }

            return Result<FileImportResult>.Failure(FileImportErrorCodes.ImportFailed,
                imported.ErrorMessage ?? "PDF import failed.");
        }

        List<FileImportIssue> issues = [];
        if (wasMoved)
        {
            issues.Add(new FileImportIssue("moved_to_search_root",
                "The source file was moved to the first file search root before import."));
        }

        foreach (PdfImportPageFailure failure in imported.PageFailures ?? [])
        {
            issues.Add(new FileImportIssue("page_failure",
                $"Page {failure.PageIndex + 1}: {failure.ErrorMessage}"));
        }

        string? itemUri = imported.CreatedItemId is null ? null : $"patchouli://items/{imported.CreatedItemId}.yml";
        string? textUri = imported.CreatedDocumentInstanceId is null
            ? null
            : $"patchouli://texts/{imported.CreatedDocumentInstanceId}/";
        return Result<FileImportResult>.Success(new FileImportResult(
            true,
            LocalImportKind.Pdf,
            imported.IsDuplicate,
            wasMoved,
            itemUri,
            textUri,
            null,
            imported.PageCount,
            imported.Status,
            // Importing a PDF is never misreported as OCR or index complete.
            "ingested",
            issues,
            null));
    }

    private async Task<Result<FileImportResult>> InstallCslCoreAsync(
        string path,
        CancellationToken cancellationToken)
    {
        Result<CslLocalInstallResult> installed = await _cslInstall.InstallFromFileAsync(path, cancellationToken);
        if (installed.IsFailure)
        {
            return Result<FileImportResult>.Failure(
                installed.ErrorCode ?? FileImportErrorCodes.StyleInvalid,
                installed.ErrorMessage ?? "CSL style install failed.");
        }

        CslLocalInstallResult result = installed.Value;
        List<FileImportIssue> issues = [];
        if (result.MissingParent is not null)
        {
            issues.Add(new FileImportIssue("missing_parent_style",
                result.ErrorMessage ?? $"Parent style '{result.MissingParent}' is not installed."));
        }

        return Result<FileImportResult>.Success(new FileImportResult(
            true,
            LocalImportKind.Csl,
            result.Reused,
            false,
            null,
            null,
            result.StyleUri,
            0,
            result.Reused ? "style_reused" : "installed",
            "body_readable",
            issues,
            null));
    }

    /// <summary>
    /// Recovery strategy: file moves and DB commits are not one transaction. When the
    /// import fails after a move, restore the original position; when the restore fails,
    /// keep the file where it is and return a recoverable diagnosis — never "file lost",
    /// never a silent move. Every attempt is recorded durably.
    /// </summary>
    private async Task<Result<FileImportResult>> RecoverAfterFailedImportAsync(
        string originalPath,
        string movedPath,
        FileImportTrigger trigger,
        LocalImportKind kind,
        Result<FileImportResult> ingestFailure,
        CancellationToken cancellationToken)
    {
        string fileName = Path.GetFileName(originalPath);
        Result restore = await MoveFileAsync(movedPath, originalPath, cancellationToken);
        if (restore.IsSuccess)
        {
            await RecordRecoveryAsync(trigger, kind, originalPath, movedPath, originalPath,
                FileImportRecoveryStatuses.ImportFailedRestored,
                $"Import failed ({ingestFailure.ErrorCode}); the file was restored to its original position.",
                cancellationToken);
            return Result<FileImportResult>.Failure(
                ingestFailure.ErrorCode ?? FileImportErrorCodes.ImportFailed,
                $"{ingestFailure.ErrorMessage} The source file was restored to its original position.");
        }

        await RecordRecoveryAsync(trigger, kind, originalPath, movedPath, null,
            FileImportRecoveryStatuses.ImportFailedRestoreFailed,
            $"Import failed ({ingestFailure.ErrorCode}); restoring the file to its original position " +
            $"failed: {restore.ErrorMessage}. The file was kept at its moved location.",
            cancellationToken);
        return Result<FileImportResult>.Failure(FileImportErrorCodes.RecoveryFailed,
            $"{ingestFailure.ErrorMessage} The file could not be restored to its original position and " +
            $"was kept as '{fileName}' in the first file search root; see the import recovery records " +
            "for the durable diagnosis.");
    }

    private async Task RecordRecoveryAsync(
        FileImportTrigger trigger,
        LocalImportKind kind,
        string originalPath,
        string? movedPath,
        string? restoredPath,
        string status,
        string diagnosis,
        CancellationToken cancellationToken)
    {
        try
        {
            await _recovery.RecordAsync(
                new FileImportRecoveryRecord(
                    Guid.NewGuid().ToString("N"),
                    _clock.UtcNow.ToUniversalTime(),
                    trigger.ToString(),
                    kind.ToString(),
                    originalPath,
                    movedPath,
                    restoredPath,
                    status,
                    diagnosis),
                cancellationToken);
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.file-import", "record-recovery"))
        {
            // A failed recovery record must never mask the import failure it describes.
        }
    }
}
