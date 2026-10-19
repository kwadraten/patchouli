using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Ocr;

/// <summary>
/// Execution engine used by the OCR queue task executor. External callers should use
/// <see cref="IOcrRunCoordinator"/> instead.
/// </summary>
public interface IOcrRunEngine : IOcrRunCoordinator
{
    Task<Result<IReadOnlyList<PageId>>> ListPageIdsAsync(DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default);

    Task<Result<OcrPresetVersion>> ResolvePresetVersionAsync(OcrPresetId presetId,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Loads one immutable preset version by its identity, regardless of whether it is still the
    ///     preset's current version. Engines that cannot pin versions reject the operation.
    /// </summary>
    Task<Result<OcrPresetVersion>> GetPresetVersionAsync(OcrPresetVersionId presetVersionId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result<OcrPresetVersion>.Failure(AppErrorCodes.UnsupportedOperation,
            "Pinned OCR preset versions are not supported by this engine."));
    }

    /// <summary>
    ///     Runs one selected-pages OCR using an exact, previously resolved preset version instead of
    ///     whatever version is current at execution time. Unsupported engines must reject the operation.
    /// </summary>
    Task<Result<OcrRun>> RunPresetOnPagesWithVersionAsync(
        DocumentInstanceId documentInstanceId,
        OcrPresetId presetId,
        IReadOnlyList<PageId> pageIds,
        OcrPresetVersionId presetVersionId,
        CancellationToken cancellationToken = default,
        IProgress<OcrTaskStageProgress>? progress = null)
    {
        return Task.FromResult(Result<OcrRun>.Failure(AppErrorCodes.UnsupportedOperation,
            "Pinned OCR execution is not supported by this engine."));
    }

    /// <summary>
    ///     Preflight check for one resolved preset version against a document: registered engine,
    ///     preset validity, model/credential availability, and source-file presence. Failures must be
    ///     sanitized (no local paths, credentials, or raw exception text).
    /// </summary>
    Task<Result> CheckRunReadinessAsync(DocumentInstanceId documentInstanceId, OcrPresetVersion presetVersion,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result.Failure(AppErrorCodes.UnsupportedOperation,
            "OCR run readiness checks are not supported by this engine."));
    }

    Task<Result> ReconcileInterruptedRunsAsync(CancellationToken cancellationToken = default);
}
