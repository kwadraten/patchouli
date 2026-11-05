using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;

namespace Patchouli.Ocr;

public interface IOcrRunCoordinator
{
    /// <summary>
    /// Raised once per successful OCR candidate commit, after the commit transaction
    /// and its successor state have been persisted. Progress and working-revision events never raise it.
    /// </summary>
    event EventHandler<OcrCommitCompletedEventArgs>? CommitCompleted;

    Task<Result<OcrQueueTask>> QueueDocumentOcrAsync(DocumentInstanceId documentInstanceId, OcrPresetId presetId,
        IReadOnlyList<PageId> pageIds, string engineId, string adapterKind, string? providerId, string priority,
        CancellationToken cancellationToken = default);

    Task<Result<OcrRun>> RunPresetOnDocumentAsync(DocumentInstanceId documentInstanceId, OcrPresetId presetId,
        CancellationToken cancellationToken = default, IProgress<OcrTaskStageProgress>? progress = null);

    /// <param name="rotationDegrees">
    /// Clockwise onscreen quarter turn (0/90/180/270). The provider sees the rotated page
    /// image; returned geometry is normalized back into the unrotated document basis.
    /// </param>
    Task<Result<OcrRun>> RunPresetOnPagesAsync(DocumentInstanceId documentInstanceId, OcrPresetId presetId,
        IReadOnlyList<PageId> pageIds, CancellationToken cancellationToken = default,
        IProgress<OcrTaskStageProgress>? progress = null, int rotationDegrees = 0);

    /// <summary>Produces a working region result; a region must never be auto-adopted as a whole physical page.</summary>
    /// <param name="rotationDegrees">
    /// Clockwise onscreen quarter turn. <paramref name="regionBBox"/> stays in the unrotated
    /// document basis; the provider sees the rotated region image and its boxes are mapped
    /// back into document coordinates.
    /// </param>
    Task<Result<OcrRun>> RunPresetOnRegionAsync(DocumentInstanceId documentInstanceId, OcrPresetId presetId,
        PageId pageId, NormalizedBBox regionBBox, CancellationToken cancellationToken = default,
        int rotationDegrees = 0);

    /// <summary>Produces a temporary working result without adopting it, regardless of preset auto-apply.</summary>
    /// <param name="rotationDegrees">Clockwise onscreen quarter turn (0/90/180/270).</param>
    Task<Result<OcrRun>> RunWorkingOnPageAsync(DocumentInstanceId documentInstanceId, OcrPresetId presetId,
        PageId pageId, CancellationToken cancellationToken = default, int rotationDegrees = 0)
    {
        return Task.FromResult(Result<OcrRun>.Failure(AppErrorCodes.UnsupportedOperation,
            "Working-only page OCR is not supported by this coordinator."));
    }

    /// <summary>Deletes an OCR run's uncommitted outputs without deleting its audit record or an edit session.</summary>
    Task<Result> DiscardWorkingRunAsync(OcrRunId runId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result.Failure(AppErrorCodes.UnsupportedOperation,
            "Working OCR output cleanup is not supported by this coordinator."));
    }

    /// <param name="rotationDegrees">
    /// Clockwise onscreen quarter turn. <paramref name="regionBBox"/> stays in the unrotated
    /// document basis; the provider sees the rotated region image.
    /// </param>
    Task<Result<OcrRegionCandidate>> RecognizeRegionCandidateAsync(DocumentInstanceId documentInstanceId,
        OcrPresetId presetId, PageId pageId, NormalizedBBox regionBBox,
        CancellationToken cancellationToken = default, int rotationDegrees = 0);

    Task<Result> CancelRunAsync(OcrRunId runId, CancellationToken cancellationToken = default);

    Task<Result> UnsetCurrentOcrAsync(DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default);

    Task<Result> HideOcrRunAsync(OcrRunId runId, CancellationToken cancellationToken = default);

    Task<Result<OcrCandidateCommit>> CommitCandidateRunAsync(OcrRunId runId,
        IReadOnlyList<PageId>? selectedPages = null, CancellationToken cancellationToken = default,
        IProgress<OcrTaskStageProgress>? progress = null);

    Task<Result<OcrRun>> GetRunAsync(OcrRunId runId, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<OcrPageResult>>> ListPageResultsAsync(OcrRunId runId,
        CancellationToken cancellationToken = default);
}
