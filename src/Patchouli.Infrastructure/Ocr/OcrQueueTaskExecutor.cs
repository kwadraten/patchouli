using Patchouli.Core.Results;
using Patchouli.Ocr;
using Patchouli.Search;

namespace Patchouli.Infrastructure.Ocr;

public sealed class OcrQueueTaskExecutor : IOcrQueueTaskExecutor
{
    private readonly IOcrRunEngine _engine;
    private readonly ISearchUnitBuilder? _searchUnits;
    private readonly ISearchIndexRebuilder? _searchIndex;

    public OcrQueueTaskExecutor(IOcrRunEngine engine, ISearchUnitBuilder? searchUnits = null,
        ISearchIndexRebuilder? searchIndex = null)
    {
        _engine = engine;
        _searchUnits = searchUnits;
        _searchIndex = searchIndex;
    }

    public async Task<OcrQueueExecutionResult> ExecuteAsync(OcrQueueTask task, CancellationToken cancellationToken)
    {
        try
        {
            Result<OcrRun>? run = task.TaskKind switch
            {
                OcrQueueTaskKind.Document => await _engine.RunPresetOnDocumentAsync(task.DocumentInstanceId,
                    task.PresetId, cancellationToken),
                OcrQueueTaskKind.MockPages => await _engine.RunPresetOnPagesAsync(task.DocumentInstanceId,
                    task.PresetId, task.PageIds, cancellationToken),
                OcrQueueTaskKind.ImagePage => await _engine.RunPresetOnImagePageAsync(task.DocumentInstanceId,
                    task.PresetId, task.PageIds.Single(), task.ImagePath!, cancellationToken),
                OcrQueueTaskKind.RenderedPdfPage => await _engine.RunPresetOnRenderedPdfPageAsync(
                    task.DocumentInstanceId, task.PresetId, task.PageIds.Single(), task.Dpi ?? 200, cancellationToken),
                OcrQueueTaskKind.Region => await _engine.RunPresetOnRegionAsync(task.DocumentInstanceId,
                    task.PresetId, task.PageIds.Single(), task.RegionBBox!.Value, cancellationToken),
                _ => null
            };
            if (run is null)
            {
                return new OcrQueueExecutionResult(false, false, "unsupported_operation",
                    "Unsupported OCR queue task kind.");
            }

            if (run.IsFailure)
            {
                return new OcrQueueExecutionResult(false, false, run.ErrorCode, run.ErrorMessage);
            }

            Result<IReadOnlyList<OcrPageResult>> pages =
                await _engine.ListPageResultsAsync(run.Value.OcrRunId, cancellationToken);
            if (pages.IsFailure)
            {
                return new OcrQueueExecutionResult(false, false, pages.ErrorCode, pages.ErrorMessage);
            }

            int completed = pages.Value.Count(page => page.State == OcrPageResultState.Succeeded);
            int failedCount = pages.Value.Count(page =>
                page.State is OcrPageResultState.Failed or OcrPageResultState.Skipped or OcrPageResultState.Cancelled);
            OcrPageResult? failed = pages.Value.FirstOrDefault(page => page.State != OcrPageResultState.Succeeded);
            if (failed is not null)
            {
                return new OcrQueueExecutionResult(false, false, failed.ErrorCode ?? "ocr_page_failed",
                    failed.ErrorMessage ?? "One or more OCR pages failed.", run.Value.OcrRunId, completed, failedCount);
            }

            if (task.AdoptOnCompletion)
            {
                Result<OcrCandidateAdoption> adoption = await _engine.AdoptCandidateRunAsync(
                    run.Value.OcrRunId, cancellationToken: cancellationToken);
                if (adoption.IsFailure)
                {
                    return new OcrQueueExecutionResult(false, false, adoption.ErrorCode,
                        adoption.ErrorMessage ?? "OCR candidate adoption requires attention.",
                        run.Value.OcrRunId, completed, failedCount);
                }

                if (_searchUnits is not null && _searchIndex is not null)
                {
                    Result units =
                        await _searchUnits.RebuildForDocumentInstanceAsync(task.DocumentInstanceId, cancellationToken);
                    if (units.IsFailure)
                    {
                        return new OcrQueueExecutionResult(false, false, units.ErrorCode, units.ErrorMessage,
                            run.Value.OcrRunId, completed, failedCount);
                    }

                    Result index =
                        await _searchIndex.RebuildFtsForDocumentInstanceAsync(task.DocumentInstanceId,
                            cancellationToken);
                    if (index.IsFailure)
                    {
                        return new OcrQueueExecutionResult(false, false, index.ErrorCode, index.ErrorMessage,
                            run.Value.OcrRunId, completed, failedCount);
                    }
                }
            }

            return new OcrQueueExecutionResult(true, false, RunId: run.Value.OcrRunId, CompletedPageCount: completed,
                FailedPageCount: failedCount);
        }
        catch (OperationCanceledException)
        {
            return new OcrQueueExecutionResult(false, true);
        }
    }
}
