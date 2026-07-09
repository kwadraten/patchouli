using Patchouli.Ocr;

namespace Patchouli.Infrastructure.Ocr;

public sealed class OcrQueueTaskExecutor : IOcrQueueTaskExecutor
{
    private readonly IOcrRunCoordinator _coordinator;
    public OcrQueueTaskExecutor(IOcrRunCoordinator coordinator) => _coordinator = coordinator;
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
            var failed = pages.Value.FirstOrDefault(page => page.State != OcrPageResultState.Succeeded);
            return failed is null
                ? new(true, false)
                : new(false, false, failed.ErrorCode ?? "ocr_page_failed", failed.ErrorMessage ?? "One or more OCR pages failed.");
        }
        catch (OperationCanceledException) { return new(false, true); }
    }
}
