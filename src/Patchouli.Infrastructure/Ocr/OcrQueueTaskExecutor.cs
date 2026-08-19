using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Ocr;
using Patchouli.Core.Search;

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

    public async Task<OcrQueueExecutionResult> ExecuteAsync(OcrQueueTask task, CancellationToken cancellationToken,
        IProgress<OcrTaskProgressReport>? progress = null)
    {
        IProgress<OcrTaskStageProgress>? stageProgress = progress is null
            ? null
            : new StageProgressForwarder(task.TaskId, progress);
        bool isMock = task.EngineId == OcrEngineIds.Mock;
        if (isMock)
        {
            // Mock tasks run entirely in-process; emit simulated stage markers so the
            // queue progress channel is observable without a cloud round-trip.
            stageProgress?.Report(new OcrTaskStageProgress(OcrTaskStage.Preparing, null, null));
        }

        try
        {
            Result<OcrRun>? run = task.TaskKind switch
            {
                OcrQueueTaskKind.Document => await _engine.RunPresetOnDocumentAsync(task.DocumentInstanceId,
                    task.PresetId, cancellationToken, stageProgress),
                OcrQueueTaskKind.MockPages => await _engine.RunPresetOnPagesAsync(task.DocumentInstanceId,
                    task.PresetId, task.PageIds, cancellationToken, stageProgress),
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

            if (task.CommitOnCompletion)
            {
                Result<OcrCandidateCommit> commit = await _engine.CommitCandidateRunAsync(
                    run.Value.OcrRunId, cancellationToken: cancellationToken);
                if (commit.IsFailure)
                {
                    return new OcrQueueExecutionResult(false, false, commit.ErrorCode,
                        commit.ErrorMessage ?? "OCR candidate commit requires attention.",
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

            if (isMock)
            {
                stageProgress?.Report(new OcrTaskStageProgress(OcrTaskStage.Importing, null, null));
            }

            return new OcrQueueExecutionResult(true, false, RunId: run.Value.OcrRunId, CompletedPageCount: completed,
                FailedPageCount: failedCount);
        }
        catch (OperationCanceledException)
        {
            return new OcrQueueExecutionResult(false, true);
        }
    }

    private sealed class StageProgressForwarder(OcrQueueTaskId taskId, IProgress<OcrTaskProgressReport> inner)
        : IProgress<OcrTaskStageProgress>
    {
        public void Report(OcrTaskStageProgress value)
        {
            inner.Report(new OcrTaskProgressReport(taskId, value.Stage, value.Fraction, value.Detail));
        }
    }
}
