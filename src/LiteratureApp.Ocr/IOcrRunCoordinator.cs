using LiteratureApp.Core.Ids;
using LiteratureApp.Core.Results;

namespace LiteratureApp.Ocr;

public interface IOcrRunCoordinator
{
    Task<Result<OcrRun>> RunPresetOnPagesAsync(DocumentInstanceId documentInstanceId, OcrPresetId presetId, IReadOnlyList<PageId> pageIds, CancellationToken cancellationToken = default);
    Task<Result<OcrRun>> RunPresetOnImagePageAsync(DocumentInstanceId documentInstanceId, OcrPresetId presetId, PageId pageId, string imagePath, CancellationToken cancellationToken = default);
    Task<Result<OcrRun>> RunPresetOnRenderedPdfPageAsync(DocumentInstanceId documentInstanceId, OcrPresetId presetId, PageId pageId, int dpi = 200, CancellationToken cancellationToken = default);
    Task<Result> CancelRunAsync(OcrRunId runId, CancellationToken cancellationToken = default);
    Task<Result<OcrCandidateAdoption>> AdoptCandidateRunAsync(OcrRunId runId, IReadOnlyList<PageId>? selectedPages = null, CancellationToken cancellationToken = default);
    Task<Result<OcrRun>> GetRunAsync(OcrRunId runId, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<OcrPageResult>>> ListPageResultsAsync(OcrRunId runId, CancellationToken cancellationToken = default);
}
