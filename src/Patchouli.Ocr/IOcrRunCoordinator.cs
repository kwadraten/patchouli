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

    Task<Result<OcrRun>> RunPresetOnPagesAsync(DocumentInstanceId documentInstanceId, OcrPresetId presetId,
        IReadOnlyList<PageId> pageIds, CancellationToken cancellationToken = default,
        IProgress<OcrTaskStageProgress>? progress = null);

    Task<Result<OcrRun>> RunPresetOnRegionAsync(DocumentInstanceId documentInstanceId, OcrPresetId presetId,
        PageId pageId, NormalizedBBox regionBBox, CancellationToken cancellationToken = default);

    Task<Result<OcrRegionCandidate>> RecognizeRegionCandidateAsync(DocumentInstanceId documentInstanceId,
        OcrPresetId presetId, PageId pageId, NormalizedBBox regionBBox,
        CancellationToken cancellationToken = default);

    Task<Result<OcrRun>> RunPresetOnImagePageAsync(DocumentInstanceId documentInstanceId, OcrPresetId presetId,
        PageId pageId, string imagePath, CancellationToken cancellationToken = default);

    Task<Result<OcrRun>> RunPresetOnRenderedPdfPageAsync(DocumentInstanceId documentInstanceId, OcrPresetId presetId,
        PageId pageId, int dpi = 200, CancellationToken cancellationToken = default);

    Task<Result> CancelRunAsync(OcrRunId runId, CancellationToken cancellationToken = default);

    Task<Result> UnsetCurrentOcrAsync(DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default);

    Task<Result> HideOcrRunAsync(OcrRunId runId, CancellationToken cancellationToken = default);

    Task<Result<OcrCandidateCommit>> CommitCandidateRunAsync(OcrRunId runId,
        IReadOnlyList<PageId>? selectedPages = null, CancellationToken cancellationToken = default);

    Task<Result<OcrRun>> GetRunAsync(OcrRunId runId, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<OcrPageResult>>> ListPageResultsAsync(OcrRunId runId,
        CancellationToken cancellationToken = default);
}
