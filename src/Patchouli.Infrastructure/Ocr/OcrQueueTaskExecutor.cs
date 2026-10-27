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
    private readonly OcrEngineLeaseRegistry? _engineLeases;

    public OcrQueueTaskExecutor(IOcrRunEngine engine, ISearchUnitBuilder? searchUnits = null,
        ISearchIndexRebuilder? searchIndex = null, IHostActivityTracker? activityTracker = null,
        OcrEngineLeaseRegistry? engineLeases = null)
    {
        _engine = engine;
        _searchUnits = searchUnits;
        _searchIndex = searchIndex;
        _activityTracker = activityTracker;
        _engineLeases = engineLeases;
    }

    public Task<OcrQueueExecutionResult> ExecuteAsync(OcrQueueTask task, CancellationToken cancellationToken,
        IProgress<OcrTaskProgressReport>? progress = null)
    {
        return ExecuteAsync(task, null, cancellationToken, progress);
    }

    public async Task<OcrQueueExecutionResult> ExecuteAsync(OcrQueueTask task,
        OcrRunExecutionContext? execution, CancellationToken cancellationToken,
        IProgress<OcrTaskProgressReport>? progress = null)
    {
        using IActivityScope? activity = _activityTracker?.BeginScope(
            "OCR",
            HostActivityKind.Ocr,
            "准备处理",
            $"ocr:{task.TaskId}");
        IProgress<OcrTaskStageProgress>? stageProgress = progress is null && activity is null
            ? null
            : new StageProgressForwarder(task.TaskId, task.AttemptCount, progress, activity);
        bool isMock = task.EngineId == OcrEngineIds.Mock;
        if (isMock)
        {
            // Mock tasks run entirely in-process; emit simulated stage markers so the
            // queue progress channel is observable without a cloud round-trip.
            stageProgress?.Report(new OcrTaskStageProgress(OcrTaskStage.Preparing, null, null));
        }

        // Known durable progress, retained through exceptions and cancellation so the queue (and
        // the public committed-truth projection) keeps the run id even when recognition or the
        // post-commit dirty-marker/index chain throws after the database commit.
        OcrRunId? knownRunId = null;
        int knownCompleted = 0;
        int knownFailed = 0;

        try
        {
            Result<OcrRun>? run = null;
            if (task.RunId is not null)
            {
                Result<OcrRun> existingRun = await _engine.GetRunAsync(task.RunId.Value, cancellationToken);
                // Retained recognition must belong to this attempt's frozen version and already
                // cover every requested page with a succeeded result; a different version or
                // partial coverage re-runs recognition rather than adopting the wrong output.
                if (existingRun.IsSuccess &&
                    existingRun.Value.State is OcrRunState.Completed or OcrRunState.CompletedWithErrors &&
                    (task.PresetVersionId is null ||
                     existingRun.Value.PresetVersionId == task.PresetVersionId))
                {
                    Result<IReadOnlyList<OcrPageResult>> existingPages =
                        await _engine.ListPageResultsAsync(existingRun.Value.OcrRunId, cancellationToken);
                    if (existingPages.IsSuccess &&
                        existingPages.Value.All(p => p.State == OcrPageResultState.Succeeded) &&
                        task.PageIds.All(page =>
                            existingPages.Value.Any(p => p.PageId == page)))
                    {
                        run = existingRun;
                    }
                }
            }

            if (run is null)
            {
                // Every invocation goes through the unified frozen-request entry; engines that
                // cannot honor a pinned version or the execution context fail closed there rather
                // than silently running the wrong shape.
                OcrRunExecutionRequest? request = CreateExecutionRequest(task);
                if (request is not null)
                {
                    // A pinned task must find its acceptance-time lease; executing an
                    // extension engine without it would run an unpinned code path.
                    if (task.PresetVersionId is not null && _engineLeases is not null &&
                        _engineLeases.Get(task.TaskId) is null)
                    {
                        return new OcrQueueExecutionResult(false, false, "engine_generation_unavailable",
                            "The OCR engine generation pinned at queue acceptance is no longer held.");
                    }

                    run = await _engine.ExecuteAsync(request, execution, cancellationToken,
                        stageProgress);
                }
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

            knownRunId = run.Value.OcrRunId;
            Result<IReadOnlyList<OcrPageResult>> pages =
                await _engine.ListPageResultsAsync(run.Value.OcrRunId, cancellationToken);
            if (pages.IsFailure)
            {
                return new OcrQueueExecutionResult(false, false, pages.ErrorCode, pages.ErrorMessage,
                    knownRunId);
            }

            int completed = pages.Value.Count(page => page.State == OcrPageResultState.Succeeded);
            int failedCount = pages.Value.Count(page =>
                page.State is OcrPageResultState.Failed or OcrPageResultState.Skipped or OcrPageResultState.Cancelled);
            knownCompleted = completed;
            knownFailed = failedCount;
            OcrPageResult? failed = pages.Value.FirstOrDefault(page => page.State != OcrPageResultState.Succeeded);
            if (failed is not null)
            {
                return new OcrQueueExecutionResult(false, false, failed.ErrorCode ?? "ocr_page_failed",
                    failed.ErrorMessage ?? "One or more OCR pages failed.", knownRunId, completed, failedCount);
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
                        knownRunId, completed, failedCount);
                }

                if (_searchUnits is not null && _searchIndex is not null)
                {
                    stageProgress?.Report(new OcrTaskStageProgress(OcrTaskStage.Indexing, 0.2, "units"));
                    Result units =
                        await _searchUnits.RebuildForDocumentInstanceAsync(task.DocumentInstanceId, cancellationToken);
                    if (units.IsFailure)
                    {
                        return new OcrQueueExecutionResult(false, false, units.ErrorCode, units.ErrorMessage,
                            knownRunId, completed, failedCount);
                    }

                    stageProgress?.Report(new OcrTaskStageProgress(OcrTaskStage.Indexing, 0.7, "fts"));
                    Result index =
                        await _searchIndex.RebuildFtsForDocumentInstanceAsync(task.DocumentInstanceId,
                            cancellationToken);
                    if (index.IsFailure)
                    {
                        return new OcrQueueExecutionResult(false, false, index.ErrorCode, index.ErrorMessage,
                            knownRunId, completed, failedCount);
                    }

                    stageProgress?.Report(new OcrTaskStageProgress(OcrTaskStage.Indexing, 1.0, "done"));
                }
            }

            if (isMock)
            {
                stageProgress?.Report(new OcrTaskStageProgress(OcrTaskStage.Importing, null, null));
            }

            return new OcrQueueExecutionResult(true, false, RunId: knownRunId, CompletedPageCount: completed,
                FailedPageCount: failedCount);
        }
        catch (OperationCanceledException)
        {
            return new OcrQueueExecutionResult(false, true, RunId: knownRunId,
                CompletedPageCount: knownCompleted, FailedPageCount: knownFailed);
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(
                                              exception, "infrastructure.ocr-queue-executor"))
        {
            // Never surface the raw exception message: it can contain local paths or secrets.
            return new OcrQueueExecutionResult(false, false, "worker_crashed",
                "The OCR worker stopped unexpectedly.", knownRunId, knownCompleted, knownFailed);
        }
    }

    private OcrRunExecutionRequest? CreateExecutionRequest(OcrQueueTask task)
    {
        string? scope = task.TaskKind switch
        {
            OcrQueueTaskKind.Document => OcrRunExecutionScope.Document,
            OcrQueueTaskKind.Pages => OcrRunExecutionScope.Pages,
            OcrQueueTaskKind.Region => OcrRunExecutionScope.Region,
            _ => null
        };
        return scope is null
            ? null
            : new OcrRunExecutionRequest(task.DocumentInstanceId, task.PresetId, task.PageIds, scope,
                task.PresetVersionId, task.RegionBBox, !task.CommitOnCompletion,
                _engineLeases?.Get(task.TaskId));
    }

    private sealed class StageProgressForwarder(
        OcrQueueTaskId taskId,
        int attempt,
        IProgress<OcrTaskProgressReport>? inner,
        IActivityScope? activity)
        : IProgress<OcrTaskStageProgress>
    {
        public void Report(OcrTaskStageProgress value)
        {
            inner?.Report(new OcrTaskProgressReport(taskId, value.Stage, value.Fraction, value.Detail,
                attempt));
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
