using System.Reactive.Concurrency;
using System.Reflection;
using FluentAssertions;
using Microsoft.Reactive.Testing;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Host.Downloads;
using Patchouli.UI;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

[Collection("RetainedTabUI")]
[Trait("Category", "ui")]
public sealed class DownloadTaskQueueTests(AvaloniaSharedSessionFixture headless) : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private readonly TemporaryAppSettingsFile _settings = new();

    public void Dispose()
    {
        _settings.Dispose();
    }

    [Fact]
    public async Task Download_snapshot_is_applied_even_when_OCR_queue_is_unavailable()
    {
        await WithMainAsync(async (main, services) =>
        {
            DownloadTaskHandle handle = services.DownloadTasks.Start("Model archive",
                (_, _) => Task.FromResult(Result.Success()));
            await handle.Completion.WaitAsync(Deadline);
            using OcrQueueViewModel queue = CreateQueue(main);
            queue.StartObserving();
            (object context, object snapshot) = await LoadSnapshotAsync(queue, main, services);
            snapshot.GetType().GetProperty("QueueError")!.SetValue(snapshot, "OCR engine unavailable");

            ApplySnapshot(queue, context, snapshot);

            queue.FinishedTaskRows.Should().ContainSingle();
            OcrQueueTaskViewModel row = queue.FinishedTaskRows.Single();
            row.IsDownload.Should().BeTrue();
            row.DocumentTitle.Should().Be("Model archive");
            row.TaskTypeText.Should().Be("下载");
            row.TaskId.Should().Be(handle.TaskId.ToString());
            queue.FinishedTaskCount.Should().Be(1);
        });
    }

    [Fact]
    public async Task Download_progress_active_finished_filtering_and_finish_time_ordering_are_projected()
    {
        await WithMainAsync((main, services) =>
        {
            OcrQueueViewModel queue = main.OcrQueue;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            DownloadTaskStatus running = Status("running", now) with { Progress = 0.37 };
            DownloadTaskStatus older = Status("failed", now) with { FinishedAt = now.AddMinutes(-5) };
            DownloadTaskStatus newer = Status("succeeded", now) with
            {
                UpdatedAt = now.AddMinutes(-10),
                FinishedAt = now.AddMinutes(-1),
                Progress = 1
            };

            queue.UpdateDownloads([older, running, newer], services.DownloadTasks);

            queue.TaskTypes.Should().Contain("下载");
            queue.ActiveTaskCount.Should().Be(1);
            queue.FinishedTaskCount.Should().Be(2);
            OcrQueueTaskViewModel active = queue.ActiveTaskRows.Single();
            active.ProgressValue.Should().Be(37);
            active.CancelCommand.CanExecute(null).Should().BeTrue();
            active.RetryCommand.CanExecute(null).Should().BeFalse();
            active.SupportsPause.Should().BeFalse();
            queue.FinishedTaskRows.Select(row => row.TaskId).Should()
                .Equal(newer.TaskId.ToString(), older.TaskId.ToString());
            queue.HasFinishedTasks.Should().BeTrue();
            queue.HasRetryableTasks.Should().BeTrue();
            queue.SelectedTaskType = "OCR";
            queue.ActiveTaskRows.Should().BeEmpty();
            queue.FinishedTaskRows.Should().BeEmpty();
            queue.HasFinishedTasks.Should().BeTrue();
            queue.HasRetryableTasks.Should().BeTrue();
            queue.SelectedTaskType = "翻译";
            queue.ActiveTaskRows.Should().BeEmpty();
            queue.SelectedTaskType = "元数据";
            queue.FinishedTaskRows.Should().BeEmpty();
            queue.SelectedTaskType = "下载";
            queue.ActiveTaskRows.Single().Should().BeSameAs(active);

            queue.UpdateDownloads([running with { Progress = 1, State = "succeeded", FinishedAt = now }],
                services.DownloadTasks);
            queue.ActiveTaskCount.Should().Be(0);
            queue.FinishedTaskCount.Should().Be(1);
            queue.FinishedTaskRows.Single().Should().BeSameAs(active);
            active.CancelCommand.CanExecute(null).Should().BeFalse();
            active.ProgressValue.Should().Be(100);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Download_row_commands_use_delegates_and_real_state_without_pause_support()
    {
        int cancellations = 0;
        int retries = 0;
        DownloadTaskStatus status = Status("queued", DateTimeOffset.UtcNow);
        using OcrQueueTaskViewModel row = new(status, () =>
        {
            cancellations++;
            return Task.CompletedTask;
        }, () =>
        {
            retries++;
            return Task.CompletedTask;
        });

        row.CancelCommand.CanExecute(null).Should().BeTrue();
        row.RetryCommand.CanExecute(null).Should().BeFalse();
        row.PauseCommand.CanExecute(null).Should().BeFalse();
        row.ResumeCommand.CanExecute(null).Should().BeFalse();
        await row.CancelCommand.ExecuteAsync();
        cancellations.Should().Be(1);
        row.UpdateDownload(status with { State = "failed" });
        row.CancelCommand.CanExecute(null).Should().BeFalse();
        row.RetryCommand.CanExecute(null).Should().BeTrue();
        await row.RetryCommand.ExecuteAsync();
        retries.Should().Be(1);
        row.UpdateDownload(status with { State = "failed", RetryTaskId = Guid.NewGuid() });
        row.RetryCommand.CanExecute(null).Should().BeFalse();
        row.CanRetry.Should().BeFalse();
        row.UpdateDownload(status with { State = "cancelled" });
        row.RetryCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Download_retry_starts_a_single_attempt_and_projects_retry_identity()
    {
        await WithMainAsync(async (main, services) =>
        {
            int[] attempts = [0];
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            DownloadTaskHandle first = services.DownloadTasks.Start("Retryable", async (_, token) =>
            {
                if (Interlocked.Increment(ref attempts[0]) == 1)
                {
                    return Result.Failure("download_test", "failed");
                }

                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Result.Success();
            });
            await first.Completion.WaitAsync(Deadline);
            OcrQueueViewModel queue = main.OcrQueue;
            queue.UpdateDownloads(services.DownloadTasks.List(), services.DownloadTasks);
            OcrQueueTaskViewModel original = queue.FinishedTaskRows.Single();

            await Task.WhenAll(original.RetryCommand.ExecuteAsync(), original.RetryCommand.ExecuteAsync())
                .WaitAsync(Deadline);
            await started.Task.WaitAsync(Deadline);
            await original.RetryCommand.ExecuteAsync();
            queue.UpdateDownloads(services.DownloadTasks.List(), services.DownloadTasks);

            attempts[0].Should().Be(2);
            original.RetryStarted.Should().BeTrue();
            original.CanRetry.Should().BeFalse();
            DownloadTaskStatus oldStatus = services.DownloadTasks.List().Single(task => task.TaskId == first.TaskId);
            oldStatus.RetryTaskId.Should().NotBeNull();
            queue.ActiveTaskRows.Should().ContainSingle(row => row.TaskId == oldStatus.RetryTaskId.ToString());
            await queue.ActiveTaskRows.Single().CancelCommand.ExecuteAsync();
            using CancellationTokenSource timeout = new(Deadline);
            while (services.DownloadTasks.List().Any(task => task.State is "running" or "queued"))
            {
                await Task.Delay(10, timeout.Token);
            }

            queue.UpdateDownloads(services.DownloadTasks.List(), services.DownloadTasks);
            queue.ActiveTaskRows.Should().BeEmpty();
            queue.FinishedTaskRows.Should().HaveCount(2);
        });
    }

    [Fact]
    public async Task Clear_finished_removes_downloads_from_host_registry_but_keeps_running_downloads()
    {
        await WithMainAsync(async (main, services) =>
        {
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            DownloadTaskHandle running = services.DownloadTasks.Start("Running", async (_, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Result.Success();
            });
            DownloadTaskHandle done = services.DownloadTasks.Start("Done", (_, _) => Task.FromResult(Result.Success()));
            await Task.WhenAll(started.Task, done.Completion).WaitAsync(Deadline);
            OcrQueueViewModel queue = main.OcrQueue;
            queue.UpdateDownloads(services.DownloadTasks.List(), services.DownloadTasks);
            queue.SelectedTaskType = "OCR";

            await queue.ClearFinishedCommand.ExecuteAsync().WaitAsync(Deadline);

            services.DownloadTasks.List().Should().ContainSingle(task => task.TaskId == running.TaskId);
            queue.SelectedTaskType = "下载";
            queue.ActiveTaskRows.Should().ContainSingle();
            queue.FinishedTaskRows.Should().BeEmpty();
            queue.HasFinishedTasks.Should().BeFalse();
            services.DownloadTasks.Cancel(running.TaskId).IsSuccess.Should().BeTrue();
            await running.Completion.WaitAsync(Deadline);
        });
    }

    [Fact]
    public async Task Retry_failed_includes_downloads_even_when_the_download_filter_is_hidden()
    {
        await WithMainAsync(async (main, services) =>
        {
            int[] attempts = [0];
            DownloadTaskHandle failed = services.DownloadTasks.Start("Global retry", (_, _) =>
                Task.FromResult(Interlocked.Increment(ref attempts[0]) == 1
                    ? Result.Failure("download_test", "failed")
                    : Result.Success()));
            await failed.Completion.WaitAsync(Deadline);
            OcrQueueViewModel queue = main.OcrQueue;
            queue.UpdateDownloads(services.DownloadTasks.List(), services.DownloadTasks);
            queue.SelectedTaskType = "OCR";
            queue.RetryFailedCommand.CanExecute(null).Should().BeTrue();

            await queue.RetryFailedCommand.ExecuteAsync().WaitAsync(Deadline);
            await queue.RetryFailedCommand.ExecuteAsync().WaitAsync(Deadline);

            using CancellationTokenSource timeout = new(Deadline);
            while (Volatile.Read(ref attempts[0]) < 2)
            {
                await Task.Delay(10, timeout.Token);
            }

            attempts[0].Should().Be(2);
            services.DownloadTasks.List().Should().HaveCount(2);
            services.DownloadTasks.List().Single(task => task.TaskId == failed.TaskId).RetryTaskId.Should().NotBeNull();
        });
    }

    [Fact]
    public async Task Download_events_do_not_poll_or_apply_while_hidden_and_showing_catches_up()
    {
        await WithMainAsync(async (main, services) =>
        {
            TestScheduler timing = new();
            using OcrQueueViewModel queue = new(main, timing,
                new SynchronizationContextScheduler(SynchronizationContext.Current!));
            queue.StartObserving();
            await queue.RefreshAsync().WaitAsync(Deadline);
            queue.StopObserving();
            long sequence = SnapshotSequence(queue);

            DownloadTaskHandle download = services.DownloadTasks.Start("Hidden download",
                (_, _) => Task.FromResult(Result.Success()));
            await download.Completion.WaitAsync(Deadline);
            timing.AdvanceBy(TimeSpan.FromSeconds(30).Ticks);
            await queue.RefreshAsync();

            SnapshotSequence(queue).Should().Be(sequence);
            queue.ActiveTaskRows.Should().BeEmpty();
            queue.FinishedTaskRows.Should().BeEmpty();
            queue.StartObserving();
            await queue.RefreshAsync().WaitAsync(Deadline);
            queue.FinishedTaskRows.Should().ContainSingle(row => row.TaskId == download.TaskId.ToString());
        });
    }

    [Fact]
    public async Task Detach_rejects_stale_download_snapshot_rows_controls_and_events()
    {
        await WithMainAsync(async (main, services) =>
        {
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            DownloadTaskHandle running = services.DownloadTasks.Start("Old context", async (_, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Result.Success();
            });
            await started.Task.WaitAsync(Deadline);
            using OcrQueueViewModel queue = CreateQueue(main);
            queue.StartObserving();
            await queue.RefreshAsync().WaitAsync(Deadline);
            OcrQueueTaskViewModel staleRow = queue.ActiveTaskRows.Single();
            (object context, object snapshot) = await LoadSnapshotAsync(queue, main, services);
            queue.StopObserving();
            queue.DetachLibraryContext();

            ApplySnapshot(queue, context, snapshot);
            queue.UpdateDownloads(services.DownloadTasks.List(), services.DownloadTasks);
            await staleRow.CancelCommand.ExecuteAsync();
            DownloadTaskHandle later = services.DownloadTasks.Start("Stale event",
                (_, _) => Task.FromResult(Result.Success()));
            await later.Completion.WaitAsync(Deadline);

            queue.ActiveTaskRows.Should().BeEmpty();
            queue.FinishedTaskRows.Should().BeEmpty();
            queue.HasFinishedTasks.Should().BeFalse();
            queue.HasRetryableTasks.Should().BeFalse();
            services.DownloadTasks.List().Single(task => task.TaskId == running.TaskId).State.Should().Be("running");
            typeof(OcrQueueViewModel).GetField("_subscribedDownloads", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(queue).Should().BeNull();
            services.DownloadTasks.Cancel(running.TaskId);
            await running.Completion.WaitAsync(Deadline);
        });
    }

    private async Task WithMainAsync(Func<MainWindowViewModel, HostServices, Task> action)
    {
        await headless.Session.Dispatch<bool>(async () =>
        {
            await using MainWindowViewModel main = new(new FakeClipboard(), settingsPath: _settings.Path)
            {
                RuntimeDatabasePath = _settings.CreateDatabasePath("download-task-queue")
            };
            try
            {
                await main.OpenDatabaseCommand.ExecuteAsync();
                await main.Library.CreateCommand.ExecuteAsync();
                HostServices services = await main.ServicesAsync();
                await action(main, services);
                return true;
            }
            finally
            {
                await main.ShutdownAsync();
            }
        }, CancellationToken.None);
    }

    private static OcrQueueViewModel CreateQueue(MainWindowViewModel main)
    {
        return new OcrQueueViewModel(main, new TestScheduler(),
            new SynchronizationContextScheduler(SynchronizationContext.Current ??
                                                throw new InvalidOperationException("Dispatcher context required")),
            TimeSpan.FromMilliseconds(50));
    }

    private static DownloadTaskStatus Status(string state, DateTimeOffset now)
    {
        return new DownloadTaskStatus(Guid.NewGuid(), "Model", state, 0, now, now,
            state is "queued" or "running" ? null : now, null, null);
    }

    private static async Task<(object Context, object Snapshot)> LoadSnapshotAsync(OcrQueueViewModel queue,
        MainWindowViewModel main, HostServices services)
    {
        Type type = typeof(OcrQueueViewModel);
        long epoch = (long)type.GetField("_visibilityEpoch", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(queue)!;
        object context = Activator.CreateInstance(type.GetNestedType("QueueSnapshotContext", BindingFlags.NonPublic)!,
            services, main.LibraryGeneration, epoch,
            new Dictionary<string, DateTimeOffset>(), new Dictionary<string, DateTimeOffset>(),
            new Dictionary<string, DateTimeOffset>(), null)!;
        object snapshot = await Task.Run(async () =>
        {
            Task load = (Task)type.GetMethod("LoadSnapshotAsync", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [context, CancellationToken.None])!;
            await load;
            return load.GetType().GetProperty("Result")!.GetValue(load)!;
        });
        return (context, snapshot);
    }

    private static void ApplySnapshot(OcrQueueViewModel queue, object context, object snapshot)
    {
        typeof(OcrQueueViewModel).GetMethod("ApplySnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(queue, [context, snapshot]);
    }

    private static long SnapshotSequence(OcrQueueViewModel queue)
    {
        return (long)typeof(OcrQueueViewModel)
            .GetField("_snapshotSequence", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(queue)!;
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
}
