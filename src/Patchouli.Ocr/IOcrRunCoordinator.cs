using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;

namespace Patchouli.Ocr;

public interface IOcrRunCoordinator
{
    Task<Result<OcrRun>> RunPresetOnDocumentAsync(DocumentInstanceId documentInstanceId, OcrPresetId presetId, CancellationToken cancellationToken = default);
    Task<Result<OcrRun>> RunPresetOnPagesAsync(DocumentInstanceId documentInstanceId, OcrPresetId presetId, IReadOnlyList<PageId> pageIds, CancellationToken cancellationToken = default);
    Task<Result<OcrRun>> RunPresetOnRegionAsync(DocumentInstanceId documentInstanceId, OcrPresetId presetId, PageId pageId, NormalizedBBox regionBBox, CancellationToken cancellationToken = default);
    Task<Result<OcrRun>> RunPresetOnImagePageAsync(DocumentInstanceId documentInstanceId, OcrPresetId presetId, PageId pageId, string imagePath, CancellationToken cancellationToken = default);
    Task<Result<OcrRun>> RunPresetOnRenderedPdfPageAsync(DocumentInstanceId documentInstanceId, OcrPresetId presetId, PageId pageId, int dpi = 200, CancellationToken cancellationToken = default);
    Task<Result> CancelRunAsync(OcrRunId runId, CancellationToken cancellationToken = default);
    Task<Result> UnsetCurrentOcrAsync(DocumentInstanceId documentInstanceId, CancellationToken cancellationToken = default);
    Task<Result> HideOcrRunAsync(OcrRunId runId, CancellationToken cancellationToken = default);
    Task<Result<OcrCandidateAdoption>> AdoptCandidateRunAsync(OcrRunId runId, IReadOnlyList<PageId>? selectedPages = null, CancellationToken cancellationToken = default);
    Task<Result<OcrRun>> GetRunAsync(OcrRunId runId, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<OcrPageResult>>> ListPageResultsAsync(OcrRunId runId, CancellationToken cancellationToken = default);
}
