using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Ocr;

namespace Patchouli.Tests;

public sealed class QueuedOcrRunCoordinatorTests
{
    [Fact]
    public async Task Document_run_enqueues_document_task_and_returns_engine_run()
    {
        (QueuedOcrRunCoordinator facade, OcrQueueScheduler scheduler, FakeEngine engine) = Create();

        Result<OcrRun> result = await facade.RunPresetOnDocumentAsync(
            engine.Run.DocumentInstanceId, engine.Run.PresetId);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.OcrRunId.Should().Be(engine.Run.OcrRunId);
        engine.Calls.Should().Contain(["list_page_ids", "document", "adopt", "get_run"]);
        OcrQueueTask task = (await scheduler.ListTasksAsync(new OcrQueueTaskFilter())).Value.Single();
        task.TaskKind.Should().Be(OcrQueueTaskKind.Document);
        task.Priority.Should().Be(OcrQueuePriority.UserStartedDocument);
        task.State.Should().Be(OcrQueueTaskState.Succeeded);
        task.RunId.Should().Be(engine.Run.OcrRunId);
        task.PageIds.Should().BeEquivalentTo(engine.PageIds);
    }

    [Fact]
    public async Task Document_run_without_pages_fails_validation()
    {
        (QueuedOcrRunCoordinator facade, _, FakeEngine engine) = Create();
        engine.PageIds.Clear();

        Result<OcrRun> result = await facade.RunPresetOnDocumentAsync(
            engine.Run.DocumentInstanceId, engine.Run.PresetId);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
        engine.Calls.Should().NotContain("document");
    }

    [Fact]
    public async Task Pages_run_enqueues_mock_pages_kind_with_selection_priority()
    {
        (QueuedOcrRunCoordinator facade, OcrQueueScheduler scheduler, FakeEngine engine) = Create();

        Result<OcrRun> result = await facade.RunPresetOnPagesAsync(
            engine.Run.DocumentInstanceId, engine.Run.PresetId, engine.PageIds);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        engine.Calls.Should().Contain("pages");
        OcrQueueTask task = (await scheduler.ListTasksAsync(new OcrQueueTaskFilter())).Value.Single();
        task.TaskKind.Should().Be(OcrQueueTaskKind.MockPages);
        task.Priority.Should().Be(OcrQueuePriority.InteractiveSelectedPages);
        task.EngineId.Should().Be(OcrEngineIds.Mock);
    }

    [Fact]
    public async Task Single_page_run_uses_current_page_priority()
    {
        (QueuedOcrRunCoordinator facade, OcrQueueScheduler scheduler, FakeEngine engine) = Create();

        Result<OcrRun> result = await facade.RunPresetOnPagesAsync(
            engine.Run.DocumentInstanceId, engine.Run.PresetId, [engine.PageIds[0]]);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        OcrQueueTask task = (await scheduler.ListTasksAsync(new OcrQueueTaskFilter())).Value.Single();
        task.Priority.Should().Be(OcrQueuePriority.InteractiveCurrentPage);
    }

    [Fact]
    public async Task Region_run_enqueues_region_kind_without_adoption()
    {
        (QueuedOcrRunCoordinator facade, OcrQueueScheduler scheduler, FakeEngine engine) = Create();
        NormalizedBBox region = new(.1, .2, .3, .4);

        Result<OcrRun> result = await facade.RunPresetOnRegionAsync(
            engine.Run.DocumentInstanceId, engine.Run.PresetId, engine.PageIds[0], region);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        engine.Calls.Should().Contain("region").And.NotContain("adopt");
        OcrQueueTask task = (await scheduler.ListTasksAsync(new OcrQueueTaskFilter())).Value.Single();
        task.TaskKind.Should().Be(OcrQueueTaskKind.Region);
        task.Priority.Should().Be(OcrQueuePriority.InteractiveCurrentPage);
        task.RegionBBox.Should().Be(region);
        task.AdoptOnCompletion.Should().BeFalse();
    }

    [Fact]
    public async Task Image_page_run_enqueues_image_page_kind()
    {
        (QueuedOcrRunCoordinator facade, OcrQueueScheduler scheduler, FakeEngine engine) = Create();

        Result<OcrRun> result = await facade.RunPresetOnImagePageAsync(
            engine.Run.DocumentInstanceId, engine.Run.PresetId, engine.PageIds[0], "image.png");

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        engine.Calls.Should().Contain("image");
        OcrQueueTask task = (await scheduler.ListTasksAsync(new OcrQueueTaskFilter())).Value.Single();
        task.TaskKind.Should().Be(OcrQueueTaskKind.ImagePage);
        task.ImagePath.Should().Be("image.png");
        task.Priority.Should().Be(OcrQueuePriority.InteractiveCurrentPage);
    }

    [Fact]
    public async Task Rendered_pdf_page_run_enqueues_rendered_kind_with_dpi()
    {
        (QueuedOcrRunCoordinator facade, OcrQueueScheduler scheduler, FakeEngine engine) = Create();

        Result<OcrRun> result = await facade.RunPresetOnRenderedPdfPageAsync(
            engine.Run.DocumentInstanceId, engine.Run.PresetId, engine.PageIds[0], 300);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        engine.Calls.Should().Contain("rendered");
        OcrQueueTask task = (await scheduler.ListTasksAsync(new OcrQueueTaskFilter())).Value.Single();
        task.TaskKind.Should().Be(OcrQueueTaskKind.RenderedPdfPage);
        task.Dpi.Should().Be(300);
    }

    [Fact]
    public async Task Enqueue_failure_propagates()
    {
        (QueuedOcrRunCoordinator facade, OcrQueueScheduler scheduler, FakeEngine engine) = Create();

        Result<OcrRun> result = await facade.RunPresetOnPagesAsync(
            engine.Run.DocumentInstanceId, engine.Run.PresetId, []);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
        engine.Calls.Should().NotContain("pages");
        (await scheduler.ListTasksAsync(new OcrQueueTaskFilter())).Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Task_failure_maps_to_result_failure_with_last_error_code()
    {
        (QueuedOcrRunCoordinator facade, OcrQueueScheduler scheduler, FakeEngine engine) = Create();
        engine.RunFailureCode = "empty_ocr_output";

        Result<OcrRun> result = await facade.RunPresetOnPagesAsync(
            engine.Run.DocumentInstanceId, engine.Run.PresetId, [engine.PageIds[0]]);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("empty_ocr_output");
        OcrQueueTask task = (await scheduler.ListTasksAsync(new OcrQueueTaskFilter())).Value.Single();
        task.State.Should().Be(OcrQueueTaskState.Failed);
    }

    [Fact]
    public async Task Preset_resolution_failure_propagates()
    {
        (QueuedOcrRunCoordinator facade, _, FakeEngine engine) = Create();
        engine.FailVersion = true;

        Result<OcrRun> result = await facade.RunPresetOnPagesAsync(
            engine.Run.DocumentInstanceId, engine.Run.PresetId, [engine.PageIds[0]]);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.NotFound);
        engine.Calls.Should().NotContain("pages");
    }

    [Fact]
    public async Task Caller_cancellation_cancels_the_queue_task()
    {
        (QueuedOcrRunCoordinator facade, OcrQueueScheduler scheduler, FakeEngine engine) = Create();
        engine.BlockUntilCancelled = true;
        using CancellationTokenSource cancellation = new();

        Task<Result<OcrRun>> pending = facade.RunPresetOnPagesAsync(
            engine.Run.DocumentInstanceId, engine.Run.PresetId, [engine.PageIds[0]], cancellation.Token);
        await Task.Delay(300);
        await cancellation.CancelAsync();
        Result<OcrRun> result = await pending;

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(OcrFailureCode.Cancelled);
        OcrQueueTask task = await WaitForTaskStateAsync(scheduler, OcrQueueTaskState.Cancelled);
        task.State.Should().Be(OcrQueueTaskState.Cancelled);
    }

    [Fact]
    public async Task QueueDocumentOcrAsync_returns_the_queued_task_promptly()
    {
        (QueuedOcrRunCoordinator facade, OcrQueueScheduler scheduler, FakeEngine engine) = Create();

        Result<OcrQueueTask> queued = await facade.QueueDocumentOcrAsync(
            engine.Run.DocumentInstanceId, engine.Run.PresetId, engine.PageIds, OcrEngineIds.Mock,
            OcrAdapterKind.LocalLibrary, null, OcrQueuePriority.UserStartedDocument);

        queued.IsSuccess.Should().BeTrue(queued.ErrorMessage);
        queued.Value.TaskKind.Should().Be(OcrQueueTaskKind.Document);
        queued.Value.State.Should().Be(OcrQueueTaskState.Queued);
        queued.Value.PageIds.Should().BeEquivalentTo(engine.PageIds);
        await scheduler.WaitForIdleAsync();
        engine.Calls.Should().Contain("document");
    }

    [Fact]
    public async Task RecognizeRegionCandidateAsync_delegates_to_engine_without_enqueueing()
    {
        (QueuedOcrRunCoordinator facade, OcrQueueScheduler scheduler, FakeEngine engine) = Create();
        NormalizedBBox region = new(.1, .2, .3, .4);

        Result<OcrRegionCandidate> candidate = await facade.RecognizeRegionCandidateAsync(
            engine.Run.DocumentInstanceId, engine.Run.PresetId, engine.PageIds[0], region);

        candidate.IsSuccess.Should().BeTrue(candidate.ErrorMessage);
        candidate.Value.Payload.Should().BeOfType<TextBoxPayload>();
        engine.Calls.Should().Contain("region_candidate");
        (await scheduler.ListTasksAsync(new OcrQueueTaskFilter())).Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Query_and_management_members_delegate_to_engine()
    {
        (QueuedOcrRunCoordinator facade, _, FakeEngine engine) = Create();
        OcrRunId runId = engine.Run.OcrRunId;

        (await facade.CancelRunAsync(runId)).IsSuccess.Should().BeTrue();
        (await facade.HideOcrRunAsync(runId)).IsSuccess.Should().BeTrue();
        (await facade.UnsetCurrentOcrAsync(engine.Run.DocumentInstanceId)).IsSuccess.Should().BeTrue();
        (await facade.GetRunAsync(runId)).IsSuccess.Should().BeTrue();
        (await facade.ListPageResultsAsync(runId)).IsSuccess.Should().BeTrue();
        (await facade.AdoptCandidateRunAsync(runId)).IsSuccess.Should().BeTrue();

        engine.Calls.Should().Contain(
            ["cancel_run", "hide_run", "unset_current", "get_run", "list_results", "adopt"]);
    }

    private static (QueuedOcrRunCoordinator Facade, OcrQueueScheduler Scheduler, FakeEngine Engine) Create()
    {
        FakeEngine engine = new();
        OcrQueueTaskExecutor executor = new(engine);
        OcrQueueScheduler scheduler = new(LibraryId.New(), new FixedClock(DateTimeOffset.UtcNow), executor,
            loopInterval: TimeSpan.FromMilliseconds(10));
        return (new QueuedOcrRunCoordinator(scheduler, engine), scheduler, engine);
    }

    private static async Task<OcrQueueTask> WaitForTaskStateAsync(OcrQueueScheduler scheduler, string state)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            OcrQueueTask? task = (await scheduler.ListTasksAsync(new OcrQueueTaskFilter())).Value
                .SingleOrDefault(candidate => candidate.State == state);
            if (task is not null)
            {
                return task;
            }

            await Task.Delay(20);
        }

        throw new InvalidOperationException($"Queue task never reached state {state}.");
    }

    private sealed class FakeEngine : IOcrRunEngine
    {
        public event EventHandler<OcrAdoptionCommittedEventArgs>? AdoptionCommitted
        {
            add { }
            remove { }
        }

        public FakeEngine()
        {
            OcrPresetVersion version = new(OcrPresetVersionId.New(), OcrPresetId.New(), OcrEngineIds.Mock, "model",
                null, "{}", false, DateTimeOffset.UtcNow);
            Version = version;
            Run = new OcrRun(OcrRunId.New(), DocumentInstanceId.New(), version.PresetId, version.PresetVersionId,
                version.EngineId, version.ModelId, "{}", null, null, null, OcrRunState.Completed,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        }

        public List<string> Calls { get; } = [];
        public List<PageId> PageIds { get; } = [PageId.New(), PageId.New()];
        public OcrPresetVersion Version { get; }
        public OcrRun Run { get; }
        public string? RunFailureCode { get; set; }
        public bool FailVersion { get; set; }
        public bool BlockUntilCancelled { get; set; }

        public Task<Result<IReadOnlyList<PageId>>> ListPageIdsAsync(DocumentInstanceId d,
            CancellationToken c = default)
        {
            Calls.Add("list_page_ids");
            return Task.FromResult(Result<IReadOnlyList<PageId>>.Success(PageIds.ToArray()));
        }

        public Task<Result<OcrPresetVersion>> ResolvePresetVersionAsync(OcrPresetId p, CancellationToken c = default)
        {
            return Task.FromResult(FailVersion
                ? Result<OcrPresetVersion>.Failure(AppErrorCodes.NotFound, "test")
                : Result<OcrPresetVersion>.Success(Version));
        }

        public Task<Result> ReconcileInterruptedRunsAsync(CancellationToken c = default)
        {
            return Task.FromResult(Result.Success());
        }

        public Task<Result<OcrQueueTask>> QueueDocumentOcrAsync(DocumentInstanceId d, OcrPresetId p,
            IReadOnlyList<PageId> pages, string engineId, string adapterKind, string? providerId, string priority,
            CancellationToken c = default)
        {
            return Task.FromResult(Result<OcrQueueTask>.Failure(AppErrorCodes.UnsupportedOperation, "test"));
        }

        public Task<Result<OcrRun>> RunPresetOnDocumentAsync(DocumentInstanceId d, OcrPresetId p,
            CancellationToken c = default, IProgress<OcrTaskStageProgress>? progress = null)
        {
            return RunAsync("document", c);
        }

        public Task<Result<OcrRun>> RunPresetOnPagesAsync(DocumentInstanceId d, OcrPresetId p,
            IReadOnlyList<PageId> pages, CancellationToken c = default,
            IProgress<OcrTaskStageProgress>? progress = null)
        {
            return RunAsync("pages", c);
        }

        public Task<Result<OcrRun>> RunPresetOnRegionAsync(DocumentInstanceId d, OcrPresetId p, PageId page,
            NormalizedBBox bbox, CancellationToken c = default)
        {
            return RunAsync("region", c);
        }

        public Task<Result<OcrRegionCandidate>> RecognizeRegionCandidateAsync(DocumentInstanceId d, OcrPresetId p,
            PageId page, NormalizedBBox bbox, CancellationToken c = default)
        {
            Calls.Add("region_candidate");
            return Task.FromResult(Result<OcrRegionCandidate>.Success(
                new OcrRegionCandidate(page, bbox, DocumentBoxType.Text, new TextBoxPayload("text"))));
        }

        public Task<Result<OcrRun>> RunPresetOnImagePageAsync(DocumentInstanceId d, OcrPresetId p, PageId page,
            string path, CancellationToken c = default)
        {
            return RunAsync("image", c);
        }

        public Task<Result<OcrRun>> RunPresetOnRenderedPdfPageAsync(DocumentInstanceId d, OcrPresetId p, PageId page,
            int dpi = 200, CancellationToken c = default)
        {
            return RunAsync("rendered", c);
        }

        public Task<Result> CancelRunAsync(OcrRunId r, CancellationToken c = default)
        {
            Calls.Add("cancel_run");
            return Task.FromResult(Result.Success());
        }

        public Task<Result> UnsetCurrentOcrAsync(DocumentInstanceId d, CancellationToken c = default)
        {
            Calls.Add("unset_current");
            return Task.FromResult(Result.Success());
        }

        public Task<Result> HideOcrRunAsync(OcrRunId r, CancellationToken c = default)
        {
            Calls.Add("hide_run");
            return Task.FromResult(Result.Success());
        }

        public Task<Result<OcrCandidateAdoption>> AdoptCandidateRunAsync(OcrRunId r,
            IReadOnlyList<PageId>? pages = null, CancellationToken c = default)
        {
            Calls.Add("adopt");
            return Task.FromResult(Result<OcrCandidateAdoption>.Success(
                new OcrCandidateAdoption(OcrCandidateAdoptionId.New(), r, Run.DocumentInstanceId, [], "[]",
                    DateTimeOffset.UtcNow)));
        }

        public Task<Result<OcrRun>> GetRunAsync(OcrRunId r, CancellationToken c = default)
        {
            Calls.Add("get_run");
            return Task.FromResult(Result<OcrRun>.Success(Run));
        }

        public Task<Result<IReadOnlyList<OcrPageResult>>> ListPageResultsAsync(OcrRunId r,
            CancellationToken c = default)
        {
            Calls.Add("list_results");
            return Task.FromResult(Result<IReadOnlyList<OcrPageResult>>.Success([
                new OcrPageResult(OcrPageResultId.New(), r, PageIds[0], OcrPageResultState.Succeeded,
                    DocumentTreeRevisionId.New(), null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
            ]));
        }

        private async Task<Result<OcrRun>> RunAsync(string call, CancellationToken cancellationToken)
        {
            Calls.Add(call);
            if (BlockUntilCancelled)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return RunFailureCode is not null
                ? Result<OcrRun>.Failure(RunFailureCode, "run failed")
                : Result<OcrRun>.Success(Run);
        }
    }
}
