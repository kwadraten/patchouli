using System.Collections.Concurrent;
using System.Reactive.Concurrency;
using System.Reflection;
using FluentAssertions;
using Microsoft.Reactive.Testing;
using Patchouli.Core.Credentials;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Core.Translation;
using Patchouli.Host.Composition;
using Patchouli.Ocr;
using Avalonia.Headless;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.UI.Views;
using Patchouli.UI;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public sealed class OcrQueueReactivityTests : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    public void Dispose()
    {
        _settings.Dispose();
    }

    private static TranslationRunStatus Translation(TranslationRunLifecycle state = TranslationRunLifecycle.Running)
    {
        return new TranslationRunStatus(Guid.NewGuid(), DocumentInstanceId.New(), state, 4,
            state == TranslationRunLifecycle.Running ? 1 : 4, 1, 0, 0, 0, 0, []);
    }

    private sealed class TranslationService : ITranslationRunService
    {
        public int Cancellations { get; private set; }

        public Task<Result<TranslationRunStatus>> StartAsync(TranslationRunStartRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<TranslationRunStatus>.Success(Translation()));
        }

        public Task<Result<IReadOnlyList<TranslationRunStatus>>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<IReadOnlyList<TranslationRunStatus>>.Success([]));
        }

        public Task<Result<TranslationRunStatus>> GetAsync(Guid runId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<TranslationRunStatus>.Success(Translation() with { RunId = runId }));
        }

        public Task<Result<TranslationRunStatus>> CancelAsync(Guid runId, CancellationToken cancellationToken = default)
        {
            Cancellations++;
            return GetAsync(runId, cancellationToken);
        }
    }

    [Fact]
    public void Task_type_filter_updates_counts_preserves_running_rows_and_resets_on_library_detach()
    {
        using MainWindowViewModel main = CreateMainWindow();
        OcrQueueViewModel queue = main.OcrQueue;
        TranslationService service = new();
        TranslationRunStatus running = Translation();
        TranslationRunStatus finished = Translation(TranslationRunLifecycle.Completed);
        queue.UpdateTranslations([running, finished], service);
        OcrQueueTaskViewModel row = queue.ActiveTaskRows.Single();
        row.TaskTypeText.Should().Be("翻译");
        row.ProgressValue.Should().Be(25);
        row.SupportsPause.Should().BeFalse();
        row.CancelCommand.CanExecute(null).Should().BeTrue();
        queue.SelectedTaskType = "OCR";
        queue.ActiveTaskCount.Should().Be(0);
        queue.FinishedTaskCount.Should().Be(0);
        queue.NoActiveTasks.Should().BeTrue();
        queue.SelectedTaskType = "翻译";
        queue.ActiveTaskRows.Single().Should().BeSameAs(row);
        queue.ActiveTabHeader.Should().Be("进行中 (1)");
        queue.FinishedTabHeader.Should().Be("已完成 (1)");
        queue.UpdateTranslations(
            [running with { State = TranslationRunLifecycle.Failed, ErrorMessage = "model unavailable" }, finished],
            service);
        queue.ActiveTaskCount.Should().Be(0);
        queue.FinishedTaskCount.Should().Be(2);
        queue.FinishedTaskRows.Should().Contain(task => task.ErrorText == "model unavailable" && !task.CanRetry);
        queue.HasRetryableTasks.Should().BeFalse();
        queue.DetachLibraryContext();
        queue.SelectedTaskType = "全部任务";
        queue.ActiveTaskRows.Should().BeEmpty();
        queue.FinishedTaskRows.Should().BeEmpty();
        service.Cancellations.Should().Be(0);
    }

    [Fact]
    public async Task Ocr_controls_and_refresh_keep_translation_tasks_independent()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch(async () =>
        {
            await using MainWindowViewModel main =
                WithRuntimeDatabasePath(CreateMainWindow(), _settings.CreateDatabasePath("mixed-task-queue"));
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            IOcrQueueScheduler scheduler = (await services.GetOcrQueueAsync()).Value;
            await scheduler.PauseAsync(OcrPauseScope.Global);
            (await scheduler.EnqueueTestPagesAsync(DocumentInstanceId.New(), OcrPresetId.New(), [PageId.New()],
                OcrQueuePriority.UserStartedDocument)).IsSuccess.Should().BeTrue();
            OcrQueueViewModel queue = main.OcrQueue;
            queue.StartObserving();
            await queue.RefreshAsync();
            TranslationService translations = new();
            TranslationRunStatus running = Translation();
            queue.UpdateTranslations([running], translations);
            queue.ActiveTaskRows.Should().HaveCount(2);
            await queue.StopCommand.ExecuteAsync();
            queue.ActiveTaskRows.Should().Contain(row => row.IsTranslation && row.IsActive);
            await queue.ResumeGlobalCommand.ExecuteAsync();
            await queue.PauseGlobalCommand.ExecuteAsync();
            queue.ActiveTaskRows.Should().Contain(row => row.IsTranslation && row.IsActive);
            translations.Cancellations.Should().Be(0);
            queue.SelectedTaskType = "翻译";
            queue.ActiveTaskRows.Should().ContainSingle().Which.IsTranslation.Should().BeTrue();
            queue.SelectedTaskType = "OCR";
            queue.ActiveTaskRows.Should().ContainSingle().Which.IsTranslation.Should().BeFalse();
            queue.StopObserving();
            queue.SelectedTaskType = "全部任务";
            queue.ActiveTaskRows.Should().HaveCount(2);
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Queue_page_filter_and_active_tab_are_bound_and_closing_does_not_cancel_translation()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<int>(async () =>
        {
            await using MainWindowViewModel main = CreateMainWindow();
            TranslationService service = new();
            OcrQueueViewModel queue = main.OcrQueue;
            queue.UpdateTranslations([Translation(), Translation(TranslationRunLifecycle.Completed)], service);
            OcrQueuePage page = new() { DataContext = queue };
            Window window = new() { Width = 1440, Height = 900, Content = page };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Button[] buttons = page.GetVisualDescendants().OfType<Button>().ToArray();
                buttons.Should().Contain(button => Equals(button.Content, "停止任务"));
                buttons.Should().Contain(button => Equals(button.Content, "暂停全部任务"));
                buttons.Select(button => button.Content).OfType<Patchouli.Lucide.Avalonia.Lucide>().Should()
                    .Contain(icon => icon.Icon == "X");
                page.GetVisualDescendants().OfType<TextBlock>().Should().NotContain(text =>
                    text.Text == queue.StatusSummary && !string.IsNullOrEmpty(text.Text));
                ComboBox filter = page.GetVisualDescendants().OfType<ComboBox>().Single();
                filter.SelectedItem.Should().Be("全部任务");
                filter.SelectedItem = "OCR";
                queue.SelectedTaskType.Should().Be("OCR");
                queue.ActiveTaskCount.Should().Be(0);
                queue.FinishedTaskCount.Should().Be(0);
                filter.SelectedItem = "翻译";
                queue.ActiveTaskCount.Should().Be(1);
                queue.FinishedTaskCount.Should().Be(1);
                TabControl tabs = page.GetVisualDescendants().OfType<TabControl>().Single();
                queue.SelectedTaskTabIndex = 1;
                tabs.SelectedIndex.Should().Be(1);
                tabs.SelectedIndex = 0;
                queue.SelectedTaskTabIndex.Should().Be(0);
            }
            finally
            {
                window.Close();
            }

            service.Cancellations.Should().Be(0);
            queue.ActiveTaskRows.Should().ContainSingle().Which.IsActive.Should().BeTrue();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Task_queue_global_buttons_control_both_types_and_filter_does_not_limit_controls()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch(async () =>
        {
            await using MainWindowViewModel main =
                WithRuntimeDatabasePath(CreateMainWindow(), _settings.CreateDatabasePath("generic-task-controls"));
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            ItemMetadata item = (await services.Items.CreateItemAsync("document", "Translation source")).Value;
            DocumentInstance document = (await services.Documents.AttachDocumentInstanceAsync(item.ItemId, null,
                DocumentInstanceType.PrimaryScan)).Value;
            await services.Pages.CreatePageAsync(document.DocumentInstanceId, 0, "1", null, null, 0,
                Core.Layout.CoordinateBasis.NormalizedPage, null, null, "test", null);
            OcrQueueViewModel queue = main.OcrQueue;
            queue.StartObserving();
            await queue.RefreshAsync();
            await queue.PauseGlobalCommand.ExecuteAsync();
            TranslationRunStatus run =
                (await services.TranslationRuns.StartAsync(
                    new TranslationRunStartRequest(document.DocumentInstanceId, []))).Value;
            run.IsPaused.Should().BeTrue();
            await WaitForQueueAsync(queue, () => queue.ActiveTaskRows.Count == 1);
            queue.ActiveTaskRows.Single().SupportsPause.Should().BeTrue();
            OcrQueuePage page = new() { DataContext = queue };
            Window window = new() { Width = 1440, Height = 600, Content = page };
            window.Show();
            try
            {
                window.UpdateLayout();
                page.GetVisualDescendants().OfType<Patchouli.Lucide.Avalonia.Lucide>()
                    .Select(icon => icon.Icon).Should().Contain(["Pause", "Play", "X"]);
                using Avalonia.Media.Imaging.RenderTargetBitmap image = new(new Avalonia.PixelSize(1440, 600));
                image.Render(page);
                string path = TestPaths.FromRepositoryRoot(".tmp", "task-queue-controls", "queue.png");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                image.Save(path);
            }
            finally
            {
                window.Close();
            }

            // Closing the window detaches the page and suspends the queue observer; re-arm it so
            // the refreshes below run real snapshot cycles instead of collapsing to dirty marks.
            queue.StartObserving();
            queue.SelectedTaskType = "OCR";
            await queue.StopCommand.ExecuteAsync();
            await queue.ResumeGlobalCommand.ExecuteAsync();
            (await services.TranslationRuns.GetAsync(run.RunId)).Value.IsPaused.Should().BeTrue("the queue is stopped");
            queue.SelectedTaskType = "翻译";
            await queue.PauseGlobalCommand.ExecuteAsync();
            await queue.StartCommand.ExecuteAsync();
            (await services.TranslationRuns.GetAsync(run.RunId)).Value.IsPaused.Should()
                .BeTrue("global pause survives start");
            await services.TranslationRuns.CancelAsync(run.RunId);
            await WaitForQueueAsync(queue, () => queue.FinishedTaskRows.Count == 1);
            queue.SelectedTaskType = "OCR";
            queue.HasFinishedTasks.Should().BeTrue("global actions cover hidden task types");
            await queue.ClearFinishedCommand.ExecuteAsync();
            queue.SelectedTaskType = "全部任务";
            queue.FinishedTaskRows.Should().BeEmpty();
            return 0;
        }, CancellationToken.None);
    }

    private static async Task WaitForQueueAsync(OcrQueueViewModel queue, Func<bool> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!condition())
        {
            // RefreshAsync collapses into a dirty mark while a poll cycle is in flight, so a
            // single await is not a happens-before edge for the apply; pump and retry.
            await queue.RefreshAsync();
            Dispatcher.UIThread.RunJobs();
            if (DateTimeOffset.UtcNow > deadline)
            {
                condition().Should().BeTrue("the queue projection did not reach the expected state");
                return;
            }

            await Task.Delay(20);
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

        public Task ClearAsync()
        {
            return Task.CompletedTask;
        }
    }

    private MainWindowViewModel CreateMainWindow()
    {
        return new MainWindowViewModel(new FakeClipboard(), settingsPath: _settings.Path);
    }

    private static MainWindowViewModel WithRuntimeDatabasePath(MainWindowViewModel viewModel, string path)
    {
        viewModel.RuntimeDatabasePath = path;
        return viewModel;
    }

    private static OcrQueueTask CreateTask(
        string state = OcrQueueTaskState.Running,
        string? lastError = null)
    {
        return new OcrQueueTask(
            OcrQueueTaskId.New(),
            LibraryId.New(),
            DocumentInstanceId.New(),
            OcrPresetId.New(),
            [PageId.New()],
            OcrQueueTaskKind.Document,
            OcrEngineIds.MinerU,
            OcrAdapterKind.CloudApi,
            ProviderIds.MinerU,
            OcrQueuePriority.UserStartedDocument,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            state,
            1,
            1,
            null,
            lastError is not null ? "err_test" : null,
            lastError,
            null);
    }

    [Fact]
    public void IsQueueRunning_change_updates_derived_properties_and_commands_without_manual_refresh()
    {
        MainWindowViewModel main = CreateMainWindow();
        OcrQueueViewModel queue = main.OcrQueue;
        ConcurrentQueue<string?> changes = new();
        queue.PropertyChanged += (_, args) => changes.Enqueue(args.PropertyName);

        queue.IsQueueRunning.Should().BeFalse();
        queue.IsQueueStopped.Should().BeTrue();
        queue.StartCommand.CanExecute(null).Should().BeTrue();
        queue.StopCommand.CanExecute(null).Should().BeFalse();

        queue.IsQueueRunning = true;

        queue.IsQueueRunning.Should().BeTrue();
        queue.IsQueueStopped.Should().BeFalse();
        queue.StartCommand.CanExecute(null).Should().BeFalse();
        queue.StopCommand.CanExecute(null).Should().BeTrue();

        changes.Should().Contain([nameof(OcrQueueViewModel.IsQueueRunning), nameof(OcrQueueViewModel.IsQueueStopped)]);

        queue.IsQueueRunning = false;

        queue.IsQueueRunning.Should().BeFalse();
        queue.IsQueueStopped.Should().BeTrue();
        queue.StartCommand.CanExecute(null).Should().BeTrue();
        queue.StopCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void IsGloballyPaused_change_updates_derived_properties_and_commands()
    {
        MainWindowViewModel main = CreateMainWindow();
        OcrQueueViewModel queue = main.OcrQueue;
        ConcurrentQueue<string?> changes = new();
        queue.PropertyChanged += (_, args) => changes.Enqueue(args.PropertyName);

        queue.IsGloballyPaused.Should().BeFalse();
        queue.IsGloballyResumed.Should().BeTrue();
        queue.PauseGlobalCommand.CanExecute(null).Should().BeTrue();
        queue.ResumeGlobalCommand.CanExecute(null).Should().BeFalse();

        queue.IsGloballyPaused = true;

        queue.IsGloballyPaused.Should().BeTrue();
        queue.IsGloballyResumed.Should().BeFalse();
        queue.PauseGlobalCommand.CanExecute(null).Should().BeFalse();
        queue.ResumeGlobalCommand.CanExecute(null).Should().BeTrue();

        changes.Should().Contain([
            nameof(OcrQueueViewModel.IsGloballyPaused), nameof(OcrQueueViewModel.IsGloballyResumed)
        ]);
    }

    [Fact]
    public void OcrQueueTaskViewModel_state_and_progress_changes_update_pure_derived_properties_and_commands()
    {
        MainWindowViewModel main = CreateMainWindow();
        OcrQueueViewModel queue = main.OcrQueue;
        OcrQueueTask task = CreateTask(OcrQueueTaskState.Running);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        OcrQueueTaskViewModel taskVm = new(task, "Test Document", queue, null, null, null, now);

        taskVm.StateText.Should().Be("运行中");
        taskVm.IsPaused.Should().BeFalse();
        taskVm.PauseCommand.CanExecute(null).Should().BeTrue();
        taskVm.ResumeCommand.CanExecute(null).Should().BeFalse();
        taskVm.CancelCommand.CanExecute(null).Should().BeTrue();
        taskVm.RetryCommand.CanExecute(null).Should().BeFalse();
        taskVm.HasError.Should().BeFalse();

        ConcurrentQueue<string?> changes = new();
        taskVm.PropertyChanged += (_, args) => changes.Enqueue(args.PropertyName);

        // Pause: Driven by IsPaused (simulating PausedScopes) rather than State = Paused
        taskVm.IsPaused = true;
        taskVm.IsPaused.Should().BeTrue();
        taskVm.PauseCommand.CanExecute(null).Should().BeFalse();
        taskVm.ResumeCommand.CanExecute(null).Should().BeTrue();
        taskVm.CancelCommand.CanExecute(null).Should().BeTrue();
        taskVm.RetryCommand.CanExecute(null).Should().BeFalse();
        changes.Should().Contain(nameof(OcrQueueTaskViewModel.IsPaused));

        // Resume: flipping IsPaused back
        taskVm.IsPaused = false;
        taskVm.IsPaused.Should().BeFalse();
        taskVm.PauseCommand.CanExecute(null).Should().BeTrue();
        taskVm.ResumeCommand.CanExecute(null).Should().BeFalse();

        // Fail with error
        taskVm.Update(task with { State = OcrQueueTaskState.Failed, LastErrorMessage = "Provider timeout" },
            "Test Document", null, null, null, now);
        taskVm.StateText.Should().Be("失败");
        taskVm.PauseCommand.CanExecute(null).Should().BeFalse();
        taskVm.ResumeCommand.CanExecute(null).Should().BeFalse();
        taskVm.CancelCommand.CanExecute(null).Should().BeFalse();
        taskVm.RetryCommand.CanExecute(null).Should().BeTrue();
        taskVm.ErrorText.Should().Be("Provider timeout");
        taskVm.HasError.Should().BeTrue();
        changes.Should().Contain(nameof(OcrQueueTaskViewModel.HasError));

        // Progress text
        taskVm.ProgressPercentText.Should().Be($"{taskVm.ProgressValue:F0}%");
    }

    [Fact]
    public void Collection_changes_project_counts_tab_headers_and_retry_command_can_execute()
    {
        MainWindowViewModel main = CreateMainWindow();
        OcrQueueViewModel queue = main.OcrQueue;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        queue.ActiveTaskCount.Should().Be(0);
        queue.ActiveTabHeader.Should().Be("进行中 (0)");
        queue.HasActiveTasks.Should().BeFalse();
        queue.NoActiveTasks.Should().BeTrue();

        queue.FinishedTaskCount.Should().Be(0);
        queue.FinishedTabHeader.Should().Be("已完成 (0)");
        queue.HasFinishedTasks.Should().BeFalse();
        queue.NoFinishedTasks.Should().BeTrue();
        queue.HasRetryableTasks.Should().BeFalse();
        queue.RetryFailedCommand.CanExecute(null).Should().BeFalse();
        queue.ClearFinishedCommand.CanExecute(null).Should().BeFalse();

        // Add active task
        OcrQueueTask runningTask = CreateTask(OcrQueueTaskState.Running);
        OcrQueueTaskViewModel activeVm = new(runningTask, "Active Doc", queue, null, null, null, now);
        queue.ActiveTaskRows.Add(activeVm);

        queue.ActiveTaskCount.Should().Be(1);
        queue.ActiveTabHeader.Should().Be("进行中 (1)");
        queue.HasActiveTasks.Should().BeTrue();
        queue.NoActiveTasks.Should().BeFalse();

        // Add succeeded finished task
        OcrQueueTask succeededTask = CreateTask(OcrQueueTaskState.Succeeded);
        OcrQueueTaskViewModel succeededVm = new(succeededTask, "Done Doc", queue, null, null, null, now);
        queue.FinishedTaskRows.Add(succeededVm);

        queue.FinishedTaskCount.Should().Be(1);
        queue.FinishedTabHeader.Should().Be("已完成 (1)");
        queue.HasFinishedTasks.Should().BeTrue();
        queue.NoFinishedTasks.Should().BeFalse();
        queue.HasRetryableTasks.Should().BeFalse();
        queue.RetryFailedCommand.CanExecute(null).Should().BeFalse();
        queue.ClearFinishedCommand.CanExecute(null).Should().BeTrue();

        // Add failed finished task (terminal task enters)
        OcrQueueTask failedTask = CreateTask(OcrQueueTaskState.Failed, "Network failed");
        OcrQueueTaskViewModel failedVm = new(failedTask, "Failed Doc", queue, null, null, null, now);
        queue.FinishedTaskRows.Add(failedVm);

        queue.FinishedTaskCount.Should().Be(2);
        queue.FinishedTabHeader.Should().Be("已完成 (2)");
        queue.HasRetryableTasks.Should().BeTrue();
        queue.RetryFailedCommand.CanExecute(null).Should().BeTrue();

        // Remove failed task -> retry command should automatically disable
        queue.FinishedTaskRows.Remove(failedVm);

        queue.FinishedTaskCount.Should().Be(1);
        queue.FinishedTabHeader.Should().Be("已完成 (1)");
        queue.HasRetryableTasks.Should().BeFalse();
        queue.RetryFailedCommand.CanExecute(null).Should().BeFalse();

        // Clear all finished
        queue.FinishedTaskRows.Clear();
        queue.FinishedTaskCount.Should().Be(0);
        queue.HasFinishedTasks.Should().BeFalse();
        queue.ClearFinishedCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Bursts_of_queue_changed_events_are_coalesced_and_result_in_eventual_consistency()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch(async () =>
        {
            string path = _settings.CreateDatabasePath("ui-ocr-coalesced");
            MainWindowViewModel main = WithRuntimeDatabasePath(CreateMainWindow(), path);
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();

            HostServices services = await main.ServicesAsync();
            IOcrQueueScheduler scheduler = (await services.GetOcrQueueAsync()).Value;
            await scheduler.PauseAsync(OcrPauseScope.Global);

            Result<OcrQueueTask> enqueued = await scheduler.EnqueueTestPagesAsync(
                DocumentInstanceId.New(), OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument);
            enqueued.IsSuccess.Should().BeTrue();

            TestScheduler timingScheduler = new();
            TimeSpan throttle = TimeSpan.FromMilliseconds(50);
            OcrQueueViewModel queue = new(main, timingScheduler, ImmediateScheduler.Instance, throttle);
            queue.ObserveQueue(scheduler);
            // The page starts suspended: events only mark dirty until the page becomes visible.
            queue.StartObserving();

            int collectionChangedCount = 0;
            // Resolved from the last CollectionChanged handler: when it runs, every earlier
            // subscriber (the VM's count/header bindings and the counter above) has completed,
            // so post-await assertions observe fully-applied state instead of racing mid-dispatch.
            TaskCompletionSource<int> applyDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.ActiveTaskRows.CollectionChanged += (_, _) =>
            {
                Interlocked.Increment(ref collectionChangedCount);
                applyDone.TrySetResult(queue.ActiveTaskRows.Count);
            };

            // Burst: trigger queue Changed event 5 times rapidly
            MethodInfo onQueueChanged = typeof(OcrQueueViewModel).GetMethod("OnQueueChanged",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            for (int i = 0; i < 5; i++)
            {
                onQueueChanged.Invoke(queue, [null, new OcrQueueChangedEventArgs(null, OcrQueueChangeKind.Updated)]);
            }

            // Before buffer window expires, refresh has not executed
            timingScheduler.AdvanceBy(throttle.Ticks - 1);
            applyDone.Task.IsCompleted.Should().BeFalse();
            queue.ActiveTaskCount.Should().Be(0);
            queue.HasActiveTasks.Should().BeFalse();
            collectionChangedCount.Should().Be(0);

            // Advance past buffer window -> single coalesced refresh executes
            timingScheduler.AdvanceBy(10);
            int count = await applyDone.Task.WaitAsync(TimeSpan.FromSeconds(5));

            count.Should().Be(1);
            queue.ActiveTaskCount.Should().Be(1);
            queue.ActiveTabHeader.Should().Be("进行中 (1)");
            queue.HasActiveTasks.Should().BeTrue();
            queue.ActiveTaskRows.Should().ContainSingle();
            collectionChangedCount.Should().Be(1);

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Refresh_applies_task_pause_scope_to_task_view_model_is_paused()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch(async () =>
        {
            string path = _settings.CreateDatabasePath("ui-ocr-task-pause");
            await using MainWindowViewModel main = WithRuntimeDatabasePath(CreateMainWindow(), path);
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();

            HostServices services = await main.ServicesAsync();
            IOcrQueueScheduler scheduler = (await services.GetOcrQueueAsync()).Value;
            await scheduler.PauseAsync(OcrPauseScope.Global);

            Result<OcrQueueTask> enqueued = await scheduler.EnqueueTestPagesAsync(
                DocumentInstanceId.New(), OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument);
            enqueued.IsSuccess.Should().BeTrue();

            OcrQueueViewModel queue = main.OcrQueue;
            // RefreshAsync is suspended-only-dirty until the page arms observation.
            queue.StartObserving();
            await queue.RefreshAsync();

            queue.ActiveTaskRows.Should().ContainSingle();
            OcrQueueTaskViewModel row = queue.ActiveTaskRows[0];
            row.IsPaused.Should().BeFalse();
            row.PauseCommand.CanExecute(null).Should().BeTrue();
            row.ResumeCommand.CanExecute(null).Should().BeFalse();

            // Pause the task via scheduler
            await scheduler.PauseAsync(OcrPauseScope.Task, row.TaskId);
            await queue.RefreshAsync();

            row.IsPaused.Should().BeTrue();
            row.PauseCommand.CanExecute(null).Should().BeFalse();
            row.ResumeCommand.CanExecute(null).Should().BeTrue();

            // Resume the task via scheduler
            await scheduler.ResumeAsync(OcrPauseScope.Task, row.TaskId);
            await queue.RefreshAsync();

            row.IsPaused.Should().BeFalse();
            row.PauseCommand.CanExecute(null).Should().BeTrue();
            row.ResumeCommand.CanExecute(null).Should().BeFalse();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Disposing_view_model_unsubscribes_queue_changed_event()
    {
        string path = _settings.CreateDatabasePath("ui-ocr-dispose");
        MainWindowViewModel main = WithRuntimeDatabasePath(CreateMainWindow(), path);
        await main.OpenDatabaseCommand.ExecuteAsync();
        await main.Library.CreateCommand.ExecuteAsync();

        TestScheduler timingScheduler = new();
        TimeSpan throttle = TimeSpan.FromMilliseconds(50);
        OcrQueueViewModel queue = new(main, timingScheduler, ImmediateScheduler.Instance, throttle);

        HostServices services = await main.ServicesAsync();
        IOcrQueueScheduler scheduler = (await services.GetOcrQueueAsync()).Value;
        queue.ObserveQueue(scheduler);

        // Dispose the queue view model
        queue.Dispose();

        // Fire a change on the scheduler
        Result<OcrQueueTask> enqueued = await scheduler.EnqueueTestPagesAsync(
            DocumentInstanceId.New(), OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument);
        enqueued.IsSuccess.Should().BeTrue();

        timingScheduler.AdvanceBy(throttle.Ticks * 2);

        // Because it was disposed, queue.ActiveTaskRows remained untouched (not refreshed)
        queue.ActiveTaskRows.Should().BeEmpty();
    }

    [Fact]
    public async Task Refresh_supports_multiple_tasks_with_same_document_instance_id_without_throwing()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch(async () =>
        {
            string path = _settings.CreateDatabasePath("ui-ocr-multi-tasks-same-doc");
            await using MainWindowViewModel main = WithRuntimeDatabasePath(CreateMainWindow(), path);
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();

            HostServices services = await main.ServicesAsync();
            IOcrQueueScheduler scheduler = (await services.GetOcrQueueAsync()).Value;
            await scheduler.PauseAsync(OcrPauseScope.Global);

            DocumentInstanceId docId = DocumentInstanceId.New();
            Result<OcrQueueTask> task1 = await scheduler.EnqueueTestPagesAsync(
                docId, OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument);
            Result<OcrQueueTask> task2 = await scheduler.EnqueueTestPagesAsync(
                docId, OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument);

            task1.IsSuccess.Should().BeTrue();
            task2.IsSuccess.Should().BeTrue();

            OcrQueueViewModel queue = main.OcrQueue;
            queue.StartObserving();
            Func<Task> act = () => queue.RefreshAsync();
            await act.Should().NotThrowAsync();

            queue.ActiveTaskRows.Should().HaveCount(2);
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public void TaskViewModel_never_displays_100_percent_progress_before_succeeded()
    {
        MainWindowViewModel main = CreateMainWindow();
        OcrQueueViewModel queue = main.OcrQueue;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        OcrQueueTask runningTask = CreateTask(OcrQueueTaskState.Running);
        OcrQueueProgress pageProgress100 = new(10, 10, 0, 0);

        OcrQueueTaskViewModel vm = new(runningTask, "Doc", queue, pageProgress100, null, null, now);
        vm.ProgressValue.Should().Be(99.0);

        // Even with fraction = 1.0 during Indexing stage
        OcrTaskProgressReport indexingStage = new(runningTask.TaskId, OcrTaskStage.Indexing, 1.0, null);
        vm.Update(runningTask, "Doc", pageProgress100, indexingStage, null, now);
        vm.ProgressValue.Should().Be(99.0);

        // When succeeded, progress reaches 100
        OcrQueueTask succeededTask = runningTask with { State = OcrQueueTaskState.Succeeded };
        vm.Update(succeededTask, "Doc", pageProgress100, null, now, now);
        vm.ProgressValue.Should().Be(100.0);
    }

    [Fact]
    public void TaskViewModel_displays_adopting_and_indexing_stage_labels()
    {
        MainWindowViewModel main = CreateMainWindow();
        OcrQueueViewModel queue = main.OcrQueue;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        OcrQueueTask runningTask = CreateTask(OcrQueueTaskState.Running);

        OcrQueueTaskViewModel vm = new(runningTask, "Doc", queue, null, null, null, now);

        OcrTaskProgressReport adoptingStage = new(runningTask.TaskId, OcrTaskStage.Adopting, 0.5, "pages:2/5");
        vm.Update(runningTask, "Doc", null, adoptingStage, null, now);
        vm.StageText.Should().Be("采纳结果 · 2/5 页");

        OcrTaskProgressReport indexingStage = new(runningTask.TaskId, OcrTaskStage.Indexing, 0.5, null);
        vm.Update(runningTask, "Doc", null, indexingStage, null, now);
        vm.StageText.Should().Be("更新索引");
    }

    [Fact]
    [Trait("Category", "ui")]
    public async Task Task_page_updates_bound_percentage_and_page_region_stage_details()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<int>(async () =>
        {
            await using MainWindowViewModel main = CreateMainWindow();
            OcrQueueTask task = CreateTask();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            OcrQueueTaskViewModel row = new(task, "Live progress", main.OcrQueue, null, null, null, now);
            main.OcrQueue.AddOcrRowForTesting(row);
            OcrQueuePage page = new() { DataContext = main.OcrQueue };
            Window window = new() { Width = 1440, Height = 900, Content = page };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                row.Update(task, "Live progress", null,
                    new OcrTaskProgressReport(task.TaskId, OcrTaskStage.Recognizing, 0.5,
                        "pages:1/4;page:7;inputs:0/1;phase:region-ocr;units:3/8"), null, now);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                row.StageText.Should().Be("逐页识别 · 1/4 页 · 当前第 7 页 · 输入 0/1 · 分区识别 3/8");
                page.GetVisualDescendants().OfType<TextBlock>().Should()
                    .Contain(text => text.Text == row.StageText).And
                    .Contain(text => text.Text == row.ProgressPercentText);
                page.GetVisualDescendants().OfType<ProgressBar>().Should().ContainSingle()
                    .Which.Value.Should().Be(42.5);

                row.Update(task, "Live progress", null,
                    new OcrTaskProgressReport(task.TaskId, OcrTaskStage.ParsingTextLayer, 0.5, "pages:2/4"), null, now);
                row.StageText.Should().Be("解析 PDF 文本层 · 2/4 页");
                row.Update(task, "Live progress", null,
                    new OcrTaskProgressReport(task.TaskId, OcrTaskStage.Importing, 0.5, "pages:2/4"), null, now);
                row.StageText.Should().Be("导入数据库 · 2/4 页");
            }
            finally
            {
                window.Close();
            }

            return 0;
        }, CancellationToken.None);
    }
}
