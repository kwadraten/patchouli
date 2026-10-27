using System.Collections.Concurrent;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.MetadataLookup;
using Patchouli.Core.Credentials;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Core.Settings;
using Patchouli.Core.Translation;
using Patchouli.Host.Composition;
using Patchouli.Host.Metadata;
using Patchouli.Ocr;
using Patchouli.UI;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;
using Patchouli.UI.Views;
using MetadataLookupOutcome = Patchouli.Core.Bibliography.MetadataLookup.MetadataLookupOutcome;
using MetadataSourcePreference = Patchouli.Core.Bibliography.MetadataLookup.MetadataSourcePreference;

namespace Patchouli.Tests;

[Collection("Avalonia")]
[Trait("Category", "ui")]
public sealed class MetadataTaskQueueTests : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _settings.Dispose();
    }

    [Fact]
    public async Task Desktop_batch_metadata_query_registers_single_observable_task_in_MetadataRuns()
    {
        string path = _settings.CreateDatabasePath("batch-metadata-single-task");
        HostServices services = await HostServices.CreateAsync(path, PatchouliAppSettings.Default());
        (await services.Library.CreateLibraryAsync("Batch Metadata Library")).IsSuccess.Should().BeTrue();

        Result<ItemMetadata> item1 = await services.Items.CreateItemAsync(new CreateItemRequest("book", "Book 1"));
        Result<ItemMetadata> item2 = await services.Items.CreateItemAsync(new CreateItemRequest("book", "Book 2"));
        item1.IsSuccess.Should().BeTrue();
        item2.IsSuccess.Should().BeTrue();

        ItemId[] itemIds = [item1.Value.ItemId, item2.Value.ItemId];

        List<MetadataLookupProgressInfo> progressReports = [];
        Patchouli.UI.ViewModels.MetadataLookupOutcome outcome = await MetadataLookupUiBridge.LookupBatchAsync(
            services,
            itemIds,
            progress => progressReports.Add(progress),
            CancellationToken.None);

        // Verify task was registered in services.MetadataRuns and is observable in registry
        Result<IReadOnlyList<MetadataBatchRunStatus>> runs = await services.MetadataRuns.ListAsync();
        runs.IsSuccess.Should().BeTrue();
        runs.Value.Should()
            .ContainSingle("exactly one batch task must be registered in MetadataRuns for desktop query");

        MetadataBatchRunStatus run = runs.Value[0];
        run.Total.Should().Be(2);
        run.State.Should().Be(MetadataRunState.Completed);
        run.Completed.Should().Be(2);

        // Observable via GetAsync
        Result<MetadataBatchRunStatus> got = await services.MetadataRuns.GetAsync(run.TaskId);
        got.IsSuccess.Should().BeTrue();
        got.Value.TaskId.Should().Be(run.TaskId);

        // Progress was reported from the Host task
        progressReports.Should().NotBeEmpty();
        progressReports.Last().Completed.Should().Be(2);
        progressReports.Last().Total.Should().Be(2);

        // Outcome reflects accurate counts
        outcome.SucceededCount.Should().Be(run.Succeeded);
        outcome.FailedCount.Should().Be(run.Failed);
    }

    [Fact]
    public async Task Batch_progress_returns_to_headless_UI_dispatcher_after_async_polling()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        StubMetadataRunService runs = new() { CompleteOnFirstPoll = true };
        TaskCompletionSource pollProgressReported = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseProgress = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> dispatched = session.Dispatch<int>(async () =>
        {
            Patchouli.UI.ViewModels.MetadataLookupOutcome outcome = await MetadataLookupUiBridge.LookupBatchAsync(runs,
                [ItemId.New()],
                progress =>
                {
                    if (progress.Completed == 0)
                    {
                        return;
                    }

                    Dispatcher.UIThread.CheckAccess().Should().BeTrue();
                    pollProgressReported.TrySetResult();
                    releaseProgress.Task.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                }, CancellationToken.None);
            outcome.IsSuccess.Should().BeTrue();
            return 0;
        }, CancellationToken.None);

        try
        {
            await pollProgressReported.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            releaseProgress.TrySetResult();
            await dispatched.WaitAsync(TimeSpan.FromSeconds(30));
        }

        (await dispatched).Should().Be(0);
        runs.GetCalls.Should().BeGreaterThan(0, "the asserted callback must follow async host-task polling");
    }

    [Fact]
    public async Task Cancelling_batch_wait_cancels_the_already_accepted_host_task()
    {
        StubMetadataRunService runs = new() { HoldRunning = true };
        using CancellationTokenSource cancellation = new();
        Task task = MetadataLookupUiBridge.LookupBatchAsync(runs, [ItemId.New()], static _ => { },
            cancellation.Token);

        await runs.BatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await FluentActions.Awaiting(async () => await task).Should().ThrowAsync<OperationCanceledException>();
        runs.CancelCalls.Should().Be(1);
    }

    [Fact]
    public void Task_queue_UI_displays_metadata_runs_with_accurate_counts_for_processed_succeeded_failed_and_pending()
    {
        using MainWindowViewModel main = CreateMainWindow();
        OcrQueueViewModel queue = main.OcrQueue;
        StubMetadataRunService service = new();

        ItemId item1 = ItemId.New();
        ItemId item2 = ItemId.New();
        ItemId item3 = ItemId.New();
        ItemId item4 = ItemId.New();

        // 1. Running state with partial progress: 1 succeeded, 1 failed, 2 pending
        MetadataBatchItemResult r1 = new(item1, true, "crossref", null, null);
        MetadataBatchItemResult r2 = new(item2, false, null, "not_found", "Item 2 missing");
        MetadataBatchItemResult r3 = new(item3, false, null, null, null);
        MetadataBatchItemResult r4 = new(item4, false, null, null, null);

        MetadataBatchRunStatus runningRun = new(
            Guid.NewGuid(),
            MetadataRunState.Running,
            4,
            2,
            1,
            1,
            [r1, r2, r3, r4]);

        queue.UpdateMetadataRuns([runningRun], service);

        queue.ActiveTaskRows.Should().ContainSingle();
        OcrQueueTaskViewModel row = queue.ActiveTaskRows[0];

        row.TotalItemCount.Should().Be(4);
        row.ProcessedCount.Should().Be(2);
        row.SucceededCount.Should().Be(1);
        row.FailedCount.Should().Be(1);
        row.PendingCount.Should().Be(2, "4 total - 2 completed = 2 pending");
        row.StageText.Should().Be("2/4 项 · 成功 1 · 失败 1 · 待处理 2");
        row.ProgressValue.Should().Be(50);
        row.IsActive.Should().BeTrue();
        row.IsFailed.Should().BeTrue("has failed items");

        // 2. Completed state: 3 succeeded, 1 failed, 0 pending
        MetadataBatchItemResult r3Done = new(item3, true, "crossref", null, null);
        MetadataBatchItemResult r4Done = new(item4, true, "crossref", null, null);

        MetadataBatchRunStatus completedRun = runningRun with
        {
            State = MetadataRunState.Completed,
            Completed = 4,
            Succeeded = 3,
            Failed = 1,
            Items = [r1, r2, r3Done, r4Done]
        };

        queue.UpdateMetadataRuns([completedRun], service);

        queue.ActiveTaskRows.Should().BeEmpty();
        queue.FinishedTaskRows.Should().ContainSingle();
        OcrQueueTaskViewModel finishedRow = queue.FinishedTaskRows[0];

        finishedRow.TotalItemCount.Should().Be(4);
        finishedRow.ProcessedCount.Should().Be(4);
        finishedRow.SucceededCount.Should().Be(3);
        finishedRow.FailedCount.Should().Be(1);
        finishedRow.PendingCount.Should().Be(0);
        finishedRow.StageText.Should().Be("4/4 项 · 成功 3 · 失败 1 · 待处理 0");
        finishedRow.ProgressValue.Should().Be(100);
        finishedRow.IsActive.Should().BeFalse();
        finishedRow.IsFailed.Should().BeTrue();
        finishedRow.CanRetry.Should().BeTrue("failed items can be retried");
        finishedRow.ErrorText.Should().Contain("Item 2 missing");
    }

    [Fact]
    public async Task
        Retrying_a_metadata_run_only_reprocesses_failed_items_and_does_not_requery_or_duplicate_successful_entries()
    {
        ItemId successItem = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        ItemId failedItem1 = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        ItemId failedItem2 = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));

        TestLookupService lookup = new();
        lookup.EnrichResults[successItem] =
            Result<MetadataLookupOutcome>.Success(CreateOutcome(successItem, "crossref"));
        lookup.EnrichResults[failedItem1] =
            Result<MetadataLookupOutcome>.Failure(MetadataLookupErrorCodes.NotFound, "item 1 missing");
        lookup.EnrichResults[failedItem2] =
            Result<MetadataLookupOutcome>.Failure(MetadataLookupErrorCodes.RateLimited, "item 2 rate limited");

        await using MetadataRunService runs = new(lookup);

        Result<MetadataBatchRunStatus> started = await runs.StartBatchAsync([successItem, failedItem1, failedItem2]);
        started.IsSuccess.Should().BeTrue();

        MetadataBatchRunStatus finished = await WaitForTerminalAsync(runs, started.Value.TaskId);
        finished.State.Should().Be(MetadataRunState.Completed);
        finished.Succeeded.Should().Be(1);
        finished.Failed.Should().Be(2);

        // Initial run enriched all 3 items once
        lookup.Enriched.Should().BeEquivalentTo([successItem, failedItem1, failedItem2]);

        // Retry the batch
        Result<MetadataBatchRunStatus> retried = await runs.RetryAsync(started.Value.TaskId);
        retried.IsSuccess.Should().BeTrue();
        retried.Value.TaskId.Should().NotBe(started.Value.TaskId, "retry creates a new observable task");
        retried.Value.Total.Should().Be(2, "retry should only include the 2 failed items");

        await WaitForTerminalAsync(runs, retried.Value.TaskId);

        // Verify lookup history: successItem was NEVER re-queried or duplicated!
        lookup.Enriched.Count(id => id == successItem).Should().Be(1, "successful items must not be re-queried");
        lookup.Enriched.Count(id => id == failedItem1).Should().Be(2, "failed item 1 should be retried once");
        lookup.Enriched.Count(id => id == failedItem2).Should().Be(2, "failed item 2 should be retried once");
    }

    [Fact]
    public async Task
        Switching_libraries_prevents_background_tasks_from_updating_or_polluting_the_newly_active_library()
    {
        string path1 = _settings.CreateDatabasePath("lib-switch-1");
        string path2 = _settings.CreateDatabasePath("lib-switch-2");

        MainWindowViewModel main = CreateMainWindow();
        main.RuntimeDatabasePath = path1;
        await main.OpenDatabaseCommand.ExecuteAsync();
        await main.Library.CreateCommand.ExecuteAsync();

        HostServices services = await main.ServicesAsync();
        int initialGeneration = main.LibraryGeneration;

        // Set up in-flight metadata task in queue
        OcrQueueViewModel queue = main.OcrQueue;
        StubMetadataRunService metadataService = new();
        MetadataBatchRunStatus run1 = new(
            Guid.NewGuid(),
            MetadataRunState.Running,
            2,
            1,
            1,
            0,
            [new MetadataBatchItemResult(ItemId.New(), true, "src", null, null)]);

        queue.UpdateMetadataRuns([run1], metadataService);
        queue.ActiveTaskRows.Should().ContainSingle();

        // Initiate library switch
        Task switching = main.BeginLibrarySwitchAsync();
        main.LibraryGeneration.Should().Be(initialGeneration + 1, "library generation must increment immediately");

        // Verify task queue cleared context and detached
        queue.ActiveTaskRows.Should().BeEmpty("active rows must be cleared on library switch");
        queue.FinishedTaskRows.Should().BeEmpty("finished rows must be cleared on library switch");
        queue.StatusSummary.Should().Be("等待运行数据库打开。");

        // Stale late callback from previous library's background task must be discarded
        queue.UpdateMetadataRuns([run1 with { State = MetadataRunState.Completed }], metadataService);
        // It must NOT appear in queue for the new context
        queue.ActiveTaskRows.Should().BeEmpty();
        queue.FinishedTaskRows.Should().BeEmpty();

        await switching;

        // Open library 2
        main.RuntimeDatabasePath = path2;
        await main.OpenDatabaseCommand.ExecuteAsync();
        await main.Library.CreateCommand.ExecuteAsync();

        // Verify the newly active library is clean
        main.Shell.Items.Should().BeEmpty();
        queue.ActiveTaskRows.Should().BeEmpty();

        await main.ShutdownAsync();
    }

    [Fact]
    public async Task
        Existing_OCR_and_translation_queue_behaviors_are_not_regressed_and_metadata_tasks_show_no_fake_pause_resume()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<int>(async () =>
        {
            await using MainWindowViewModel main = CreateMainWindow();
            OcrQueueViewModel queue = main.OcrQueue;

            // 1. TaskTypes verification
            queue.TaskTypes.Should().Equal(["全部任务", "OCR", "翻译", "元数据"]);

            // 2. Add OCR task, translation task, and metadata task
            OcrQueueTask ocrTask = new(
                OcrQueueTaskId.New(), LibraryId.New(), DocumentInstanceId.New(), OcrPresetId.New(),
                [PageId.New()], OcrQueueTaskKind.Document, OcrEngineIds.MinerU, OcrAdapterKind.CloudApi,
                ProviderIds.MinerU, OcrQueuePriority.UserStartedDocument, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                OcrQueueTaskState.Running, 1, 1, null, null, null, null);

            OcrQueueTaskViewModel ocrRow = new(ocrTask, "OCR Document", queue, null, null, null, DateTimeOffset.UtcNow);

            TranslationRunStatus transRun = new(
                Guid.NewGuid(), DocumentInstanceId.New(), TranslationRunLifecycle.Running,
                5, 2, 2, 0, 0, 0, 0,
                []);

            bool transPaused = false;
            OcrQueueTaskViewModel transRow = new(
                transRun, "Translation Document",
                () => Task.CompletedTask,
                () =>
                {
                    transPaused = true;
                    return Task.CompletedTask;
                },
                () =>
                {
                    transPaused = false;
                    return Task.CompletedTask;
                },
                () => Task.CompletedTask);

            MetadataBatchRunStatus metaRun = new(
                Guid.NewGuid(), MetadataRunState.Running, 3, 1, 1, 0,
                [new MetadataBatchItemResult(ItemId.New(), true, "crossref", null, null)]);

            bool metaCancelled = false;
            OcrQueueTaskViewModel metaRow = new(
                metaRun, "Metadata Task",
                () =>
                {
                    metaCancelled = true;
                    return Task.CompletedTask;
                });

            // 3. Pause/Resume behavior: OCR & Translation support pause, Metadata DOES NOT support pause
            ocrRow.SupportsPause.Should().BeTrue("OCR tasks support pause");
            transRow.SupportsPause.Should().BeTrue("Translation tasks support pause");
            await transRow.PauseCommand.ExecuteAsync();
            transPaused.Should().BeTrue("Translation PauseCommand must execute callback");
            metaRow.SupportsPause.Should().BeFalse("Metadata tasks must NOT support fake pause/resume");
            metaRow.PauseCommand.CanExecute(null).Should().BeFalse("Metadata PauseCommand must be disabled");
            metaRow.ResumeCommand.CanExecute(null).Should().BeFalse("Metadata ResumeCommand must be disabled");
            metaRow.CancelCommand.CanExecute(null).Should()
                .BeTrue("Metadata CancelCommand must be enabled while running");

            // Execute metadata cancel
            await metaRow.CancelCommand.ExecuteAsync();
            metaCancelled.Should().BeTrue();

            // 4. Filtering behavior
            queue.AddOcrRowForTesting(ocrRow);
            queue.UpdateTranslations([transRun], new StubTranslationService());
            queue.UpdateMetadataRuns([metaRun], new StubMetadataRunService());

            queue.SelectedTaskType = "全部任务";
            queue.ActiveTaskRows.Count.Should().Be(3);

            queue.SelectedTaskType = "OCR";
            queue.ActiveTaskRows.Should().ContainSingle(row => row.TaskTypeText == "OCR");

            queue.SelectedTaskType = "翻译";
            queue.ActiveTaskRows.Should().ContainSingle(row => row.TaskTypeText == "翻译");

            queue.SelectedTaskType = "元数据";
            queue.ActiveTaskRows.Should().ContainSingle(row => row.TaskTypeText == "元数据");

            // 5. Focused task behavior
            queue.SelectedTaskType = "全部任务";
            queue.FocusedTaskId = metaRun.TaskId.ToString();
            queue.ApplyTaskFilter();
            queue.ActiveTaskRows[0].TaskId.Should()
                .Be(metaRun.TaskId.ToString(), "focused metadata task must appear first");

            // 6. UI visual presentation in OcrQueuePage
            OcrQueuePage page = new() { DataContext = queue };
            Window window = new() { Width = 1440, Height = 900, Content = page };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                ComboBox filterCombo = page.GetVisualDescendants().OfType<ComboBox>().Single();
                filterCombo.ItemsSource!.Cast<string>().Should().Contain("元数据");

                // Switch to metadata
                filterCombo.SelectedItem = "元数据";
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                queue.SelectedTaskType.Should().Be("元数据");
                queue.ActiveTaskRows.Should().ContainSingle(r => r.IsMetadata);
            }
            finally
            {
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }

            return 0;
        }, CancellationToken.None);
    }

    private MainWindowViewModel CreateMainWindow()
    {
        return new MainWindowViewModel(new FakeClipboard(), settingsPath: _settings.Path);
    }

    private static async Task<MetadataBatchRunStatus> WaitForTerminalAsync(MetadataRunService runs, Guid taskId)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        while (!timeout.IsCancellationRequested)
        {
            MetadataBatchRunStatus status = (await runs.GetAsync(taskId)).Value;
            if (status.State is not MetadataRunState.Running)
            {
                return status;
            }

            await Task.Delay(20);
        }

        throw new InvalidOperationException("The metadata task did not finish in time.");
    }

    private static MetadataLookupOutcome CreateOutcome(ItemId itemId, string sourceId)
    {
        ItemMetadata item = new(
            itemId,
            new LibraryId(Guid.Parse("33333333-3333-3333-3333-333333333333")),
            "journal-article",
            "key",
            "A Study",
            null,
            null,
            "[]",
            [],
            null,
            [],
            [],
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            "[]",
            "[]",
            "{}",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        MetadataCandidate candidate = new(sourceId, "A Study");
        return new MetadataLookupOutcome(item, candidate, [new MetadataLookupAttempt(sourceId, null, null)], []);
    }

    private sealed class TestLookupService : IMetadataLookupService
    {
        public readonly Dictionary<ItemId, Result<MetadataLookupOutcome>> EnrichResults = new();
        public readonly ConcurrentQueue<ItemId> Enriched = new();

        public bool CanLookup(string scheme)
        {
            return true;
        }

        public Task<Result<MetadataLookupOutcome>> LookupAndApplyAsync(ItemId itemId, ItemIdentifier identifier,
            CancellationToken cancellationToken = default)
        {
            return EnrichAsync(itemId, cancellationToken);
        }

        public Task<Result<MetadataLookupOutcome>> LookupAndMergeAsync(ItemId itemId, string scheme, string value,
            IReadOnlyList<MetadataSourcePreference>? preferences = null,
            CancellationToken cancellationToken = default)
        {
            return EnrichAsync(itemId, cancellationToken);
        }

        public Task<Result<MetadataBatchResult>> LookupAndApplyBatchAsync(IReadOnlyList<ItemId> itemIds,
            IProgress<MetadataBatchProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<MetadataBatchResult>.Success(new MetadataBatchResult(0, 0, [])));
        }

        public Task<Result<MetadataLookupOutcome>> EnrichAsync(ItemId itemId,
            CancellationToken cancellationToken = default)
        {
            Enriched.Enqueue(itemId);
            return Task.FromResult(EnrichResults.TryGetValue(itemId, out Result<MetadataLookupOutcome>? result)
                ? result
                : Result<MetadataLookupOutcome>.Failure(MetadataLookupErrorCodes.NotFound, "missing"));
        }
    }

    private sealed class StubMetadataRunService : IMetadataRunService
    {
        private int _getCalls;
        public bool CompleteOnFirstPoll { get; init; }
        public bool HoldRunning { get; init; }
        public TaskCompletionSource BatchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int GetCalls => Volatile.Read(ref _getCalls);
        public int CancelCalls { get; private set; }

        public Task<Result<MetadataCandidateProposal>> LookupAsync(ItemId itemId, string scheme, string value,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<MetadataCandidateProposal>.Failure("unsupported", ""));
        }

        public Task<Result<MetadataLookupOutcome>> ApplyAsync(MetadataCandidateProposal proposal,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<MetadataLookupOutcome>.Failure("unsupported", ""));
        }

        public Task<Result<MetadataLookupOutcome>> EnrichAsync(ItemId itemId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<MetadataLookupOutcome>.Failure("unsupported", ""));
        }

        public Task<Result<MetadataBatchRunStatus>> StartBatchAsync(IReadOnlyList<ItemId> itemIds,
            string? requestId = null,
            CancellationToken cancellationToken = default)
        {
            if (CompleteOnFirstPoll || HoldRunning)
            {
                BatchStarted.TrySetResult();
                return Task.FromResult(Result<MetadataBatchRunStatus>.Success(new MetadataBatchRunStatus(
                    Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), MetadataRunState.Running,
                    itemIds.Count, 0, 0, 0, itemIds.Select(id =>
                        new MetadataBatchItemResult(id, false, null, null, null)).ToArray())));
            }

            return Task.FromResult(Result<MetadataBatchRunStatus>.Failure("unsupported", ""));
        }

        public Task<Result<IReadOnlyList<MetadataBatchRunStatus>>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<IReadOnlyList<MetadataBatchRunStatus>>.Success([]));
        }

        public Task<Result<MetadataBatchRunStatus>> GetAsync(Guid taskId,
            CancellationToken cancellationToken = default)
        {
            if (CompleteOnFirstPoll)
            {
                Interlocked.Increment(ref _getCalls);
                return Task.FromResult(Result<MetadataBatchRunStatus>.Success(new MetadataBatchRunStatus(
                    taskId, MetadataRunState.Completed, 1, 1, 1, 0,
                    [new MetadataBatchItemResult(ItemId.New(), true, "stub", null, null)])));
            }

            return Task.FromResult(Result<MetadataBatchRunStatus>.Failure("unsupported", ""));
        }

        public Task<Result<MetadataBatchRunStatus>> CancelAsync(Guid taskId,
            CancellationToken cancellationToken = default)
        {
            if (HoldRunning)
            {
                CancelCalls++;
                return Task.FromResult(Result<MetadataBatchRunStatus>.Success(new MetadataBatchRunStatus(
                    taskId, MetadataRunState.Cancelled, 1, 0, 0, 0,
                    [new MetadataBatchItemResult(ItemId.New(), false, null, null, null)])));
            }

            return Task.FromResult(Result<MetadataBatchRunStatus>.Failure("unsupported", ""));
        }

        public Task<Result<MetadataBatchRunStatus>> RetryAsync(Guid taskId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<MetadataBatchRunStatus>.Failure("unsupported", ""));
        }
    }

    private sealed class StubTranslationService : ITranslationRunService
    {
        public Task<Result<TranslationRunStatus>> StartAsync(TranslationRunStartRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<TranslationRunStatus>.Failure("unsupported", ""));
        }

        public Task<Result<IReadOnlyList<TranslationRunStatus>>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<IReadOnlyList<TranslationRunStatus>>.Success([]));
        }

        public Task<Result<TranslationRunStatus>> GetAsync(Guid runId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<TranslationRunStatus>.Failure("unsupported", ""));
        }

        public Task<Result<TranslationRunStatus>> CancelAsync(Guid runId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<TranslationRunStatus>.Failure("unsupported", ""));
        }
    }

    private sealed class StubOcrScheduler(OcrQueueTask task) : IOcrQueueScheduler
    {
        public event EventHandler<OcrQueueChangedEventArgs>? Changed
        {
            add { }
            remove { }
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<Result<OcrQueueStatus>> GetQueueStatusAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<OcrQueueStatus>.Success(new OcrQueueStatus(true, 0, 1, 0, 0, 0, 0, [],
                OcrQueueLimits.Default, new Dictionary<string, int>(), new Dictionary<string, int>())));
        }

        public Task<Result<IReadOnlyList<OcrQueueTask>>> ListTasksAsync(OcrQueueTaskFilter filter,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<IReadOnlyList<OcrQueueTask>>.Success([task]));
        }

        public Task<Result<OcrQueueTask>> GetTaskAsync(OcrQueueTaskId taskId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<OcrQueueTask>.Success(task));
        }

        public Task<Result<OcrQueueTask>> EnqueueAsync(OcrQueueTaskRequest request, CancellationToken c = default)
        {
            return Task.FromResult(Result<OcrQueueTask>.Failure("unsupported", ""));
        }

        public Task<Result<OcrQueueTask>> EnqueueDocumentAsync(DocumentInstanceId d, OcrPresetId p,
            IReadOnlyList<PageId> pages,
            string engineId, string adapterKind, string? providerId, string priority, CancellationToken c = default)
        {
            return Task.FromResult(Result<OcrQueueTask>.Failure("unsupported", ""));
        }

        public Task<Result<OcrQueueTask>> EnqueueMockPagesAsync(DocumentInstanceId d, OcrPresetId p,
            IReadOnlyList<PageId> pages,
            string priority, CancellationToken c = default)
        {
            return Task.FromResult(Result<OcrQueueTask>.Failure("unsupported", ""));
        }

        public Task<Result<OcrQueueTask>> EnqueueRegionAsync(DocumentInstanceId d, OcrPresetId p, PageId page,
            NormalizedBBox regionBBox, string engineId, string adapterKind, string? providerId, string priority,
            CancellationToken c = default)
        {
            return Task.FromResult(Result<OcrQueueTask>.Failure("unsupported", ""));
        }

        public Task WaitForIdleAsync(CancellationToken c = default)
        {
            return Task.CompletedTask;
        }

        public Task RunOneSchedulingTickAsync(CancellationToken c = default)
        {
            return Task.CompletedTask;
        }

        public void UpdateLimits(OcrQueueLimits limits)
        {
        }

        public void WakeScheduler()
        {
        }

        public Task<Result<OcrQueueTask>> RetryTaskAsync(OcrQueueTaskId taskId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<OcrQueueTask>.Failure("unsupported", ""));
        }

        public Task<Result> CancelTaskAsync(OcrQueueTaskId taskId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Success());
        }

        public Task<Result> PauseAsync(string scope, string? target = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Success());
        }

        public Task<Result> ResumeAsync(string scope, string? target = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Success());
        }

        public void ClearFinishedTasks()
        {
        }

        public OcrTaskProgressReport? GetTaskProgress(OcrQueueTaskId taskId)
        {
            return null;
        }

        public DateTimeOffset? GetTaskFinishedAt(OcrQueueTaskId taskId)
        {
            return null;
        }
    }

    private sealed class FakeClipboard : IClipboardService
    {
        public Task<string?> GetTextAsync()
        {
            return Task.FromResult<string?>(null);
        }

        public Task SetTextAsync(string text)
        {
            return Task.CompletedTask;
        }
    }
}
