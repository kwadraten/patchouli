using Patchouli.Core.Diagnostics;
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
    private readonly IHostActivityTracker? _activityTracker;

    public OcrQueueTaskExecutor(IOcrRunEngine engine, ISearchUnitBuilder? searchUnits = null,
        ISearchIndexRebuilder? searchIndex = null, IHostActivityTracker? activityTracker = null)
    {
        _engine = engine;
        _searchUnits = searchUnits;
        _searchIndex = searchIndex;
        _activityTracker = activityTracker;
    }

    public async Task<OcrQueueExecutionResult> ExecuteAsync(OcrQueueTask task, CancellationToken cancellationToken,
        IProgress<OcrTaskProgressReport>? progress = null)
    {
        using IActivityScope? activity = _activityTracker?.BeginScope(
            "OCR",
            HostActivityKind.Ocr,
            "准备处理",
            $"ocr:{task.TaskId}");
        IProgress<OcrTaskStageProgress>? stageProgress = progress is null && activity is null
            ? null
            : new StageProgressForwarder(task.TaskId, progress, activity);
        bool isMock = task.EngineId == OcrEngineIds.Mock;
        if (isMock)
        {
            // Mock tasks run entirely in-process; emit simulated stage markers so the
            // queue progress channel is observable without a cloud round-trip.
            stageProgress?.Report(new OcrTaskStageProgress(OcrTaskStage.Preparing, null, null));
        }

        try
        {
            Result<OcrRun>? run = null;
            if (task.RunId is not null)
            {
                Result<OcrRun> existingRun = await _engine.GetRunAsync(task.RunId.Value, cancellationToken);
                if (existingRun.IsSuccess &&
                    existingRun.Value.State is OcrRunState.Completed or OcrRunState.CompletedWithErrors)
                {
                    Result<IReadOnlyList<OcrPageResult>> existingPages =
                        await _engine.ListPageResultsAsync(existingRun.Value.OcrRunId, cancellationToken);
                    if (existingPages.IsSuccess &&
                        existingPages.Value.All(p => p.State == OcrPageResultState.Succeeded))
                    {
                        run = existingRun;
                    }
                }
            }

            if (run is null)
            {
                run = task.TaskKind switch
                {
                    OcrQueueTaskKind.Document => await _engine.RunPresetOnDocumentAsync(task.DocumentInstanceId,
                        task.PresetId, cancellationToken, stageProgress),
                    OcrQueueTaskKind.MockPages => await _engine.RunPresetOnPagesAsync(task.DocumentInstanceId,
                        task.PresetId, task.PageIds, cancellationToken, stageProgress),
                    OcrQueueTaskKind.ImagePage => await _engine.RunPresetOnImagePageAsync(task.DocumentInstanceId,
                        task.PresetId, task.PageIds.Single(), task.ImagePath!, cancellationToken),
                    OcrQueueTaskKind.RenderedPdfPage => await _engine.RunPresetOnRenderedPdfPageAsync(
                        task.DocumentInstanceId, task.PresetId, task.PageIds.Single(), task.Dpi ?? 200,
                        cancellationToken),
                    OcrQueueTaskKind.Region => await _engine.RunPresetOnRegionAsync(task.DocumentInstanceId,
                        task.PresetId, task.PageIds.Single(), task.RegionBBox!.Value, cancellationToken),
                    _ => null
                };
            }

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
                stageProgress?.Report(new OcrTaskStageProgress(OcrTaskStage.Adopting, 0, null));
                Result<OcrCandidateCommit> commit = await _engine.CommitCandidateRunAsync(
                    run.Value.OcrRunId, cancellationToken: cancellationToken, progress: stageProgress);
                if (commit.IsFailure)
                {
                    return new OcrQueueExecutionResult(false, false, commit.ErrorCode,
                        commit.ErrorMessage ?? "OCR candidate commit requires attention.",
                        run.Value.OcrRunId, completed, failedCount);
                }

                if (_searchUnits is not null && _searchIndex is not null)
                {
                    stageProgress?.Report(new OcrTaskStageProgress(OcrTaskStage.Indexing, 0.2, "units"));
                    Result units =
                        await _searchUnits.RebuildForDocumentInstanceAsync(task.DocumentInstanceId, cancellationToken);
                    if (units.IsFailure)
                    {
                        return new OcrQueueExecutionResult(false, false, units.ErrorCode, units.ErrorMessage,
                            run.Value.OcrRunId, completed, failedCount);
                    }

                    stageProgress?.Report(new OcrTaskStageProgress(OcrTaskStage.Indexing, 0.7, "fts"));
                    Result index =
                        await _searchIndex.RebuildFtsForDocumentInstanceAsync(task.DocumentInstanceId,
                            cancellationToken);
                    if (index.IsFailure)
                    {
                        return new OcrQueueExecutionResult(false, false, index.ErrorCode, index.ErrorMessage,
                            run.Value.OcrRunId, completed, failedCount);
                    }

                    stageProgress?.Report(new OcrTaskStageProgress(OcrTaskStage.Indexing, 1.0, "done"));
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

    private sealed class StageProgressForwarder(
        OcrQueueTaskId taskId,
        IProgress<OcrTaskProgressReport>? inner,
        IActivityScope? activity)
        : IProgress<OcrTaskStageProgress>
    {
        public void Report(OcrTaskStageProgress value)
        {
            inner?.Report(new OcrTaskProgressReport(taskId, value.Stage, value.Fraction, value.Detail));
            string detail = value.Fraction is { } fraction
                ? $"{value.Stage} {fraction:P0}"
                : value.Stage.ToString();
            if (!string.IsNullOrWhiteSpace(value.Detail))
            {
                detail = $"{detail}: {value.Detail}";
            }

            activity?.UpdateDetail(detail);
        }
    }
}
