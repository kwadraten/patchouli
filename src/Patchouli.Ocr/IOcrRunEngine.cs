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
    ///     Executes one frozen OCR request accepted by the queue: <paramref name="request"/> carries
    ///     the accepted page set, the optional region input, and the pinned preset version, while
    ///     <paramref name="execution"/> identifies the invoking queue attempt and receives the
    ///     created run's binding notification before model execution or page progress begins.
    ///     Engines that do not override the unified entry cannot honor a pinned version or the
    ///     binding context, so the default fails closed for those requests; the legacy per-scope
    ///     dispatch is kept only for demonstrably unpinned, context-free calls.
    /// </summary>
    Task<Result<OcrRun>> ExecuteAsync(
        OcrRunExecutionRequest request,
        OcrRunExecutionContext? execution = null,
        CancellationToken cancellationToken = default,
        IProgress<OcrTaskStageProgress>? progress = null)
    {
        if (request.PresetVersionId is not null || execution is not null)
        {
            return Task.FromResult(Result<OcrRun>.Failure(AppErrorCodes.UnsupportedOperation,
                "This OCR engine cannot execute pinned preset versions or report run bindings."));
        }

        return request.Scope switch
        {
            OcrRunExecutionScope.Document => RunPresetOnDocumentAsync(request.DocumentInstanceId,
                request.PresetId, cancellationToken, progress),
            OcrRunExecutionScope.Pages when request.WorkingDraft => request.PageIds.Count == 1
                ? RunWorkingOnPageAsync(request.DocumentInstanceId, request.PresetId, request.PageIds[0],
                    cancellationToken)
                : Task.FromResult(Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed,
                    "Working-draft OCR runs on exactly one physical page.")),
            OcrRunExecutionScope.Pages => RunPresetOnPagesAsync(request.DocumentInstanceId,
                request.PresetId, request.PageIds, cancellationToken, progress),
            OcrRunExecutionScope.Region => request is { PageIds.Count: 1, RegionBBox: { } region }
                ? RunPresetOnRegionAsync(request.DocumentInstanceId, request.PresetId, request.PageIds[0],
                    region, cancellationToken)
                : Task.FromResult(Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed,
                    "Region OCR requires exactly one physical page and a normalized region.")),
            _ => Task.FromResult(Result<OcrRun>.Failure(AppErrorCodes.UnsupportedOperation,
                "Unsupported OCR execution scope."))
        };
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
