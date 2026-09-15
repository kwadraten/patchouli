using System.Collections.Concurrent;
using System.Reactive.Concurrency;
using System.Reflection;
using FluentAssertions;
using Microsoft.Reactive.Testing;
using Patchouli.Core.Credentials;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Ocr;
using Avalonia.Headless;
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
            null,
            null,
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
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(async () =>
        {
            string path = _settings.CreateDatabasePath("ui-ocr-coalesced");
            MainWindowViewModel main = WithRuntimeDatabasePath(CreateMainWindow(), path);
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();

            HostServices services = await main.ServicesAsync();
            IOcrQueueScheduler scheduler = (await services.GetOcrQueueAsync()).Value;

            Result<OcrQueueTask> enqueued = await scheduler.EnqueueMockPagesAsync(
                DocumentInstanceId.New(), OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument);
            enqueued.IsSuccess.Should().BeTrue();

            TestScheduler timingScheduler = new();
            TimeSpan throttle = TimeSpan.FromMilliseconds(50);
            OcrQueueViewModel queue = new(main, timingScheduler, ImmediateScheduler.Instance, throttle);
            queue.ObserveQueue(scheduler);

            int collectionChangedCount = 0;
            queue.ActiveTaskRows.CollectionChanged += (_, _) => Interlocked.Increment(ref collectionChangedCount);

            TaskCompletionSource<int> activeTaskCountChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(OcrQueueViewModel.ActiveTaskCount) && queue.ActiveTaskCount > 0)
                {
                    activeTaskCountChanged.TrySetResult(queue.ActiveTaskCount);
                }
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
            activeTaskCountChanged.Task.IsCompleted.Should().BeFalse();
            queue.ActiveTaskCount.Should().Be(0);
            queue.HasActiveTasks.Should().BeFalse();
            collectionChangedCount.Should().Be(0);

            // Advance past buffer window -> single coalesced refresh executes
            timingScheduler.AdvanceBy(10);
            int count = await activeTaskCountChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));

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
        string path = _settings.CreateDatabasePath("ui-ocr-task-pause");
        MainWindowViewModel main = WithRuntimeDatabasePath(CreateMainWindow(), path);
        await main.OpenDatabaseCommand.ExecuteAsync();
        await main.Library.CreateCommand.ExecuteAsync();

        HostServices services = await main.ServicesAsync();
        IOcrQueueScheduler scheduler = (await services.GetOcrQueueAsync()).Value;

        Result<OcrQueueTask> enqueued = await scheduler.EnqueueMockPagesAsync(
            DocumentInstanceId.New(), OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument);
        enqueued.IsSuccess.Should().BeTrue();

        OcrQueueViewModel queue = main.OcrQueue;
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
        Result<OcrQueueTask> enqueued = await scheduler.EnqueueMockPagesAsync(
            DocumentInstanceId.New(), OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument);
        enqueued.IsSuccess.Should().BeTrue();

        timingScheduler.AdvanceBy(throttle.Ticks * 2);

        // Because it was disposed, queue.ActiveTaskRows remained untouched (not refreshed)
        queue.ActiveTaskRows.Should().BeEmpty();
    }
}
