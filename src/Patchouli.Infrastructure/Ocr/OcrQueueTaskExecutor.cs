using Patchouli.Ocr;
using Patchouli.Search;

namespace Patchouli.Infrastructure.Ocr;

public sealed class OcrQueueTaskExecutor : IOcrQueueTaskExecutor
{
    private readonly IOcrRunCoordinator _coordinator;
    private readonly ISearchUnitBuilder? _searchUnits;
    private readonly ISearchIndexRebuilder? _searchIndex;

    public OcrQueueTaskExecutor(IOcrRunCoordinator coordinator, ISearchUnitBuilder? searchUnits = null, ISearchIndexRebuilder? searchIndex = null)
    {
        _coordinator = coordinator;
        _searchUnits = searchUnits;
        _searchIndex = searchIndex;
    }

    public async Task<OcrQueueExecutionResult> ExecuteAsync(OcrQueueTask task, CancellationToken cancellationToken)
    {
        try
        {
            var run = task.TaskKind switch
            {
                OcrQueueTaskKind.Document => await _coordinator.RunPresetOnDocumentAsync(task.DocumentInstanceId, task.PresetId, cancellationToken),
                OcrQueueTaskKind.MockPages => await _coordinator.RunPresetOnPagesAsync(task.DocumentInstanceId, task.PresetId, task.PageIds, cancellationToken),
                OcrQueueTaskKind.ImagePage => await _coordinator.RunPresetOnImagePageAsync(task.DocumentInstanceId, task.PresetId, task.PageIds.Single(), task.ImagePath!, cancellationToken),
                OcrQueueTaskKind.RenderedPdfPage => await _coordinator.RunPresetOnRenderedPdfPageAsync(task.DocumentInstanceId, task.PresetId, task.PageIds.Single(), task.Dpi ?? 200, cancellationToken),
                _ => null
            };
            if (run is null) return new(false, false, "unsupported_operation", "Unsupported OCR queue task kind.");
            if (run.IsFailure) return new(false, false, run.ErrorCode, run.ErrorMessage);
            var pages = await _coordinator.ListPageResultsAsync(run.Value.OcrRunId, cancellationToken);
            if (pages.IsFailure) return new(false, false, pages.ErrorCode, pages.ErrorMessage);
            var completed = pages.Value.Count(page => page.State == OcrPageResultState.Succeeded);
            var failedCount = pages.Value.Count(page => page.State is OcrPageResultState.Failed or OcrPageResultState.Skipped or OcrPageResultState.Cancelled);
            var failed = pages.Value.FirstOrDefault(page => page.State != OcrPageResultState.Succeeded);
            if (failed is not null)
                return new(false, false, failed.ErrorCode ?? "ocr_page_failed", failed.ErrorMessage ?? "One or more OCR pages failed.", run.Value.OcrRunId, completed, failedCount);

            if (_searchUnits is not null && _searchIndex is not null)
            {
                var units = await _searchUnits.RebuildForDocumentInstanceAsync(task.DocumentInstanceId, cancellationToken);
                if (units.IsFailure) return new(false, false, units.ErrorCode, units.ErrorMessage, run.Value.OcrRunId, completed, failedCount);
                var index = await _searchIndex.RebuildFtsForDocumentInstanceAsync(task.DocumentInstanceId, cancellationToken);
                if (index.IsFailure) return new(false, false, index.ErrorCode, index.ErrorMessage, run.Value.OcrRunId, completed, failedCount);
            }

            return new(true, false, RunId: run.Value.OcrRunId, CompletedPageCount: completed, FailedPageCount: failedCount);
        }
        catch (OperationCanceledException) { return new(false, true); }
    }
}
