using Patchouli.Core.Downloads;
using System.Collections.Concurrent;
using FluentAssertions;
using Patchouli.Core.Results;
using Patchouli.Host.Downloads;

namespace Patchouli.Tests;

public sealed class DownloadTaskServiceTests
{
    [Fact]
    public async Task A_started_task_is_visible_as_running_and_awaits_the_real_callback()
    {
        await using DownloadTaskService service = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        DownloadTaskHandle handle = service.Start("model", async (progress, token) =>
        {
            started.TrySetResult();
            await release.Task;
            return Result.Success();
        });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        DownloadTaskStatus status = service.List().Should().ContainSingle().Subject;
        status.TaskId.Should().Be(handle.TaskId);
        status.State.Should().Be(DownloadTaskService.RunningState);
        status.DisplayName.Should().Be("model");
        status.FinishedAt.Should().BeNull();
        handle.Completion.IsCompleted.Should().BeFalse(
            "the completion is the real worker completion, not admission");

        release.TrySetResult();
        (await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Progress_reports_update_the_snapshot_and_clamp_to_the_unit_range()
    {
        DownloadTaskService service = new();
        TaskCompletionSource reportsSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource progressSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += (_, _) =>
        {
            if (service.List().FirstOrDefault()?.Progress >= 1.0)
            {
                progressSeen.TrySetResult();
            }
        };

        DownloadTaskHandle handle = service.Start("model", async (progress, token) =>
        {
            progress?.Report(0.5);
            progress?.Report(double.NaN);
            progress?.Report(2.0);
            reportsSent.TrySetResult();
            await release.Task;
            return Result.Success();
        });

        await reportsSent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await progressSeen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        service.List().Single().Progress.Should().Be(1.0,
            "the last valid report wins and NaN never poisons the snapshot");

        release.TrySetResult();
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        DownloadTaskStatus finished = service.List().Single();
        handle.TaskId.Should().Be(finished.TaskId);
        finished.Progress.Should().Be(1.0);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task Progress_after_finish_is_ignored()
    {
        await using DownloadTaskService service = new();
        IProgress<double>? captured = null;
        DownloadTaskHandle handle = service.Start("model", (progress, token) =>
        {
            captured = progress;
            return Task.FromResult(Result.Success());
        });

        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        captured?.Report(0.25);
        service.List().Single().Progress.Should().Be(1.0,
            "a late report cannot reopen a finished task's progress");
    }

    [Fact]
    public async Task A_successful_callback_marks_the_task_succeeded()
    {
        await using DownloadTaskService service = new();
        DownloadTaskHandle handle = service.Start("model",
            (progress, token) => Task.FromResult(Result.Success()));

        (await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess.Should().BeTrue();
        DownloadTaskStatus status = service.List().Single();
        status.State.Should().Be(DownloadTaskService.SucceededState);
        status.Progress.Should().Be(1.0);
        status.FinishedAt.Should().NotBeNull();
        status.ErrorCode.Should().BeNull();
    }

    [Fact]
    public async Task A_failure_result_marks_the_task_failed_with_the_callback_details()
    {
        await using DownloadTaskService service = new();
        DownloadTaskHandle handle = service.Start("model",
            (progress, token) => Task.FromResult(Result.Failure("network_error", "HTTP 404.")));

        Result result = await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("network_error");
        DownloadTaskStatus status = service.List().Single();
        status.State.Should().Be(DownloadTaskService.FailedState);
        status.ErrorCode.Should().Be("network_error");
        status.ErrorMessage.Should().Be("HTTP 404.");
    }

    [Fact]
    public async Task A_throwing_callback_marks_the_task_failed_with_a_sanitized_message()
    {
        await using DownloadTaskService service = new();
        DownloadTaskHandle handle = service.Start("model", (progress, token) =>
            Task.FromResult(Result.Success()).ContinueWith<Result>(_ =>
                throw new InvalidOperationException("signed-url-secret=leak")));

        Result result = await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(DownloadTaskService.FailedErrorCode);
        DownloadTaskStatus status = service.List().Single();
        status.State.Should().Be(DownloadTaskService.FailedState);
        status.ErrorMessage.Should().NotContain("signed-url-secret=leak",
            "raw exception text may carry signed URLs or credentials and never leaks");
    }

    [Fact]
    public async Task Cancel_requests_cancellation_and_the_worker_completes_as_cancelled()
    {
        await using DownloadTaskService service = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DownloadTaskHandle handle = service.Start("model", async (progress, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Result.Success();
        });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        service.Cancel(handle.TaskId).IsSuccess.Should().BeTrue();
        Result result = await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(DownloadTaskService.CancelledErrorCode);
        service.List().Single().State.Should().Be(DownloadTaskService.CancelledState);
    }

    [Fact]
    public async Task Cancel_of_an_unknown_or_finished_task_is_a_failure()
    {
        await using DownloadTaskService service = new();
        service.Cancel(Guid.NewGuid()).IsFailure.Should().BeTrue();

        DownloadTaskHandle handle = service.Start("model",
            (progress, token) => Task.FromResult(Result.Success()));
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        service.Cancel(handle.TaskId).IsFailure.Should().BeTrue(
            "a terminal task has nothing left to cancel");
    }

    [Fact]
    public async Task DisposeAsync_drains_a_cancelled_worker_that_is_still_running()
    {
        DownloadTaskService service = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DownloadTaskHandle handle = service.Start("model", async (progress, token) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
            }

            await release.Task;
            return Result.Success();
        });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task draining = service.DisposeAsync().AsTask();
        await Task.Delay(50);
        draining.IsCompleted.Should().BeFalse(
            "dispose awaits the actual worker, not merely the cancellation request");

        release.TrySetResult();
        await draining.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_disposed_service_refuses_new_admission()
    {
        DownloadTaskService service = new();
        service.Dispose();

        Action act = () => service.Start("model",
            (progress, token) => Task.FromResult(Result.Success()));
        act.Should().Throw<ObjectDisposedException>();
        await service.DisposeAsync();
    }

    [Fact]
    public async Task A_pre_cancelled_caller_token_never_invokes_the_callback()
    {
        await using DownloadTaskService service = new();
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        bool invoked = false;

        DownloadTaskHandle handle = service.Start("model", (progress, token) =>
        {
            invoked = true;
            return Task.FromResult(Result.Success());
        }, cancelled.Token);

        Result result = await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        invoked.Should().BeFalse("a pre-cancelled admission settles without running the callback");
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(DownloadTaskService.CancelledErrorCode);
        service.List().Single().State.Should().Be(DownloadTaskService.CancelledState);
    }

    [Fact]
    public async Task Admitted_tasks_all_settle_across_shutdown()
    {
        DownloadTaskService service = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<DownloadTaskHandle> handles = [];
        for (int i = 0; i < 4; i++)
        {
            handles.Add(service.Start($"model-{i}", async (progress, token) =>
            {
                await release.Task;
                return Result.Success();
            }));
        }

        service.Dispose();
        release.TrySetResult();
        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        foreach (DownloadTaskHandle handle in handles)
        {
            Result result = await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            (result.IsSuccess || result.ErrorCode == DownloadTaskService.CancelledErrorCode)
                .Should().BeTrue("every admitted task settles through its completion, never hangs");
        }

        service.List().Should().OnlyContain(task =>
            task.State == DownloadTaskService.SucceededState ||
            task.State == DownloadTaskService.FailedState ||
            task.State == DownloadTaskService.CancelledState);
    }

    [Fact]
    public async Task Repeated_cancel_vs_completion_races_never_throw_disposed()
    {
        await using DownloadTaskService service = new();
        for (int i = 0; i < 50; i++)
        {
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            DownloadTaskHandle handle = service.Start($"model-{i}", async (progress, token) =>
            {
                started.TrySetResult();
                await release.Task;
                return Result.Success();
            });

            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task cancelling = CancelOnWorkerAsync(service, handle.TaskId);
            Task releasing = Task.Run(() => release.TrySetResult());
            await Task.WhenAll(cancelling, releasing).WaitAsync(TimeSpan.FromSeconds(10));
            Result result = await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            (result.IsSuccess || result.ErrorCode == DownloadTaskService.CancelledErrorCode)
                .Should().BeTrue();
        }
    }

    private static Task<Result> CancelOnWorkerAsync(DownloadTaskService service, Guid id)
    {
        return Task.Run(() => service.Cancel(id));
    }

    [Fact]
    public async Task Concurrent_start_and_dispose_close_admission_and_drain()
    {
        DownloadTaskService service = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource closeRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentBag<Task<Result>> completions = [];
        Task disposing;
        try
        {
            completions.Add(service.Start("seed", async (progress, token) =>
            {
                started.TrySetResult();
                await release.Task;
                return Result.Success();
            }).Completion);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Task starter = Task.Run(async () =>
            {
                await barrier.Task;
                for (int i = 0; i < 8; i++)
                {
                    try
                    {
                        completions.Add(service.Start($"model-{i}", async (progress, token) =>
                        {
                            await release.Task;
                            return Result.Success();
                        }).Completion);
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
            });
            disposing = Task.Run(async () =>
            {
                await barrier.Task;
                service.Dispose();
                closeRequested.TrySetResult();
                await service.DisposeAsync();
            });

            barrier.TrySetResult();
            await Task.WhenAll(starter, closeRequested.Task).WaitAsync(TimeSpan.FromSeconds(10));
            disposing.IsCompleted.Should().BeFalse(
                "the seeded worker still runs, so disposal must be draining it");

            release.TrySetResult();
            await disposing.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            release.TrySetResult();
            await service.DisposeAsync();
        }

        foreach (Task<Result> completion in completions)
        {
            Result result = await completion.WaitAsync(TimeSpan.FromSeconds(10));
            (result.IsSuccess || result.ErrorCode == DownloadTaskService.CancelledErrorCode)
                .Should().BeTrue("every admitted task settles through its completion");
        }

        service.List().Should().OnlyContain(task =>
            task.State == DownloadTaskService.SucceededState ||
            task.State == DownloadTaskService.FailedState ||
            task.State == DownloadTaskService.CancelledState);
        Action act = () => service.Start("late",
            (progress, token) => Task.FromResult(Result.Success()));
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task Disposal_with_immediately_completing_tasks_does_not_fault()
    {
        DownloadTaskService service = new();
        for (int i = 0; i < 8; i++)
        {
            service.Start($"model-{i}", (progress, token) => Task.FromResult(Result.Success()));
        }

        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Retry_starts_a_new_task_for_a_failed_download_and_is_idempotent()
    {
        await using DownloadTaskService service = new();
        int attempts = 0;

        Task<Result> Download(IProgress<double>? progress, CancellationToken token)
        {
            attempts++;
            return Task.FromResult(attempts == 1
                ? Result.Failure("network_error", "HTTP 500.")
                : Result.Success());
        }

        DownloadTaskHandle failed = service.Start("model", Download);
        (await failed.Completion.WaitAsync(TimeSpan.FromSeconds(10))).IsFailure.Should().BeTrue();

        Result<DownloadTaskHandle> retried = service.Retry(failed.TaskId);
        retried.IsSuccess.Should().BeTrue();
        retried.Value.TaskId.Should().NotBe(failed.TaskId);
        service.List().Single(task => task.TaskId == failed.TaskId).RetryTaskId.Should()
            .Be(retried.Value.TaskId, "the failed task records its replacement");

        Result<DownloadTaskHandle> again = service.Retry(failed.TaskId);
        again.IsSuccess.Should().BeTrue();
        again.Value.TaskId.Should().Be(retried.Value.TaskId,
            "a second retry of the same failed task returns the existing replacement");

        (await retried.Value.Completion.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess
            .Should().BeTrue();
        attempts.Should().Be(2, "the idempotent retry never started a second replacement");
    }

    [Fact]
    public async Task Only_failed_tasks_can_be_retried()
    {
        await using DownloadTaskService service = new();
        DownloadTaskHandle succeeded = service.Start("model",
            (progress, token) => Task.FromResult(Result.Success()));
        await succeeded.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        service.Retry(succeeded.TaskId).IsFailure.Should().BeTrue(
            "a succeeded task is not retryable");

        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DownloadTaskHandle running = service.Start("other", async (progress, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Result.Success();
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        service.Retry(running.TaskId).IsFailure.Should().BeTrue();
        service.Cancel(running.TaskId).IsSuccess.Should().BeTrue();
        await running.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        service.Retry(running.TaskId).IsFailure.Should().BeTrue(
            "a cancelled download is never retried as a failure");
        service.Retry(Guid.NewGuid()).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task ClearFinished_removes_only_terminal_tasks()
    {
        await using DownloadTaskService service = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DownloadTaskHandle done = service.Start("done",
            (progress, token) => Task.FromResult(Result.Success()));
        DownloadTaskHandle running = service.Start("running", async (progress, token) =>
        {
            started.TrySetResult();
            await release.Task;
            return Result.Success();
        });
        await done.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        service.ClearFinished();

        DownloadTaskStatus remaining = service.List().Should().ContainSingle().Subject;
        remaining.TaskId.Should().Be(running.TaskId);
        release.TrySetResult();
        await running.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task The_changed_event_fires_on_state_and_progress_updates()
    {
        await using DownloadTaskService service = new();
        int changes = 0;
        service.Changed += (_, _) => Interlocked.Increment(ref changes);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        DownloadTaskHandle handle = service.Start("model", async (progress, token) =>
        {
            progress?.Report(0.4);
            await release.Task;
            return Result.Success();
        });

        release.TrySetResult();
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        changes.Should().BeGreaterThanOrEqualTo(3,
            "admission, running/progress and finish each publish a change");
    }

    [Fact]
    public async Task Seeded_file_rows_are_visible_immediately_and_ignore_scalar_reports()
    {
        await using DownloadTaskService service = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DownloadTaskHandle handle = service.Start("model", async (progress, token) =>
            {
                progress?.Report(0.9);
                await release.Task;
                return Result.Success();
            }, files:
            [
                new DownloadFileProgress("a.bin", "queued", 0, 100),
                new DownloadFileProgress("b.bin", "queued", 0, null)
            ]);

        DownloadTaskStatus status = service.List().Single();
        status.Files.Should().HaveCount(2);
        status.Files.Should().OnlyContain(file => file.State == "queued");
        status.IsIndeterminate.Should().BeTrue("b.bin has no usable total");
        try
        {
            for (int i = 0; i < 50 && service.List().Single().State != "running"; i++)
            {
                await Task.Delay(20);
            }

            DownloadTaskStatus running = service.List().Single();
            running.Progress.Should().Be(0,
                "scalar reports must not erase the seeded per-file aggregate");
        }
        finally
        {
            release.TrySetResult();
        }

        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        DownloadTaskStatus done = service.List().Single();
        done.State.Should().Be("succeeded");
        done.Files.Should().OnlyContain(file => file.State == "succeeded");
        done.IsIndeterminate.Should().BeFalse();
    }

    [Fact]
    public async Task File_reports_aggregate_byte_weighted_when_all_totals_known()
    {
        await using DownloadTaskService service = new();
        TaskCompletionSource reported = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DownloadTaskHandle handle = service.Start("model", async (progress, token) =>
            {
                IDownloadProgress rich = (IDownloadProgress)progress!;
                rich.ReportFile(new DownloadFileProgress("a.bin", "running", 50, 100));
                rich.ReportFile(new DownloadFileProgress("b.bin", "running", 300, 300));
                reported.TrySetResult();
                await release.Task;
                return Result.Success();
            }, files:
            [
                new DownloadFileProgress("a.bin", "queued", 0, 100),
                new DownloadFileProgress("b.bin", "queued", 0, 300)
            ]);

        await reported.Task.WaitAsync(TimeSpan.FromSeconds(10));
        DownloadTaskStatus running = service.List().Single();
        running.Progress.Should().BeApproximately(350.0 / 400.0, 0.001);
        running.IsIndeterminate.Should().BeFalse("both files have known totals");
        release.TrySetResult();
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task File_reports_after_terminal_are_ignored()
    {
        await using DownloadTaskService service = new();
        IDownloadProgress? captured = null;
        Task<Result> completion = service.Start("model", (progress, token) =>
        {
            captured = (IDownloadProgress)progress!;
            captured.ReportFile(new DownloadFileProgress("a.bin", "running", 10, 100));
            return Task.FromResult(Result.Success());
        }, files: [new DownloadFileProgress("a.bin", "queued", 0, 100)]).Completion;

        (await completion.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess.Should().BeTrue();
        DownloadTaskStatus settled = service.List().Single();
        captured!.ReportFile(new DownloadFileProgress("a.bin", "running", 90, 100));
        DownloadTaskStatus status = service.List().Single();
        status.Files.Should().ContainSingle();
        status.Files[0].State.Should().Be("succeeded");
        status.Files[0].DownloadedBytes.Should().Be(settled.Files[0].DownloadedBytes,
            "a file report after the task settled must not mutate the terminal snapshot");
    }

    [Fact]
    public async Task Cancel_marks_pending_rows_cancelled_but_keeps_finished_ones()
    {
        await using DownloadTaskService service = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DownloadTaskHandle handle = service.Start("model", async (progress, token) =>
            {
                IDownloadProgress rich = (IDownloadProgress)progress!;
                rich.ReportFile(new DownloadFileProgress("a.bin", "succeeded", 100, 100));
                rich.ReportFile(new DownloadFileProgress("b.bin", "running", 10, null));
                await release.Task;
                token.ThrowIfCancellationRequested();
                return Result.Success();
            }, files:
            [
                new DownloadFileProgress("a.bin", "queued", 0, 100),
                new DownloadFileProgress("b.bin", "queued", 0, null)
            ]);

        for (int i = 0; i < 50 && service.List().Single().Files[0].State != "succeeded"; i++)
        {
            await Task.Delay(20);
        }

        service.Cancel(handle.TaskId);
        release.TrySetResult();
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        DownloadTaskStatus status = service.List().Single();
        status.State.Should().Be("cancelled");
        status.Files[0].State.Should().Be("succeeded", "finished files keep their result");
        status.Files[1].State.Should().Be("cancelled", "pending rows are never marked succeeded");
    }

    [Fact]
    public async Task Retry_resets_file_rows_to_fresh_queued_state()
    {
        await using DownloadTaskService service = new();
        TaskCompletionSource first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource retryEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource retryRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0;
        DownloadTaskHandle handle = service.Start("model", async (progress, token) =>
        {
            attempts++;
            if (attempts == 1)
            {
                IDownloadProgress rich = (IDownloadProgress)progress!;
                rich.ReportFile(new DownloadFileProgress("a.bin", "running", 99, 100));
                await first.Task;
                return Result.Failure("download_failed", "boom");
            }

            retryEntered.TrySetResult();
            await retryRelease.Task;
            return Result.Success();
        }, files: [new DownloadFileProgress("a.bin", "queued", 0, 100)]);

        first.TrySetResult();
        (await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10))).IsFailure.Should().BeTrue();
        Result<DownloadTaskHandle> retried = service.Retry(handle.TaskId);
        retried.IsSuccess.Should().BeTrue();
        try
        {
            await retryEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            DownloadTaskStatus pending = service.List().Single(s => s.TaskId == retried.Value.TaskId);
            pending.State.Should().Be("running");
            pending.Files.Should().ContainSingle();
            pending.Files[0].State.Should().Be("queued",
                "retry re-seeds rows fresh before any file report");
            pending.Files[0].DownloadedBytes.Should().Be(0);
            pending.Files[0].ErrorMessage.Should().BeNull();
        }
        finally
        {
            retryRelease.TrySetResult();
        }

        await retried.Value!.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        DownloadTaskStatus retryStatus = service.List().Single(s => s.TaskId == retried.Value.TaskId);
        retryStatus.State.Should().Be("succeeded");
        retryStatus.Files[0].State.Should().Be("succeeded");
        DownloadTaskStatus original = service.List().Single(s => s.TaskId == handle.TaskId);
        original.RetryTaskId.Should().Be(retried.Value.TaskId);
        original.Files[0].State.Should().Be("failed",
            "the original's terminal row keeps its failure");
    }

    [Fact]
    public async Task Tasks_started_with_canRetry_false_cannot_be_retried()
    {
        await using DownloadTaskService service = new();
        DownloadTaskHandle handle = service.Start("install", (_, _) =>
                Task.FromResult(Result.Failure("download_failed", "boom")),
            canRetry: false);
        (await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10))).IsFailure.Should().BeTrue();

        DownloadTaskStatus status = service.List().Single();
        status.State.Should().Be("failed");
        status.CanRetry.Should().BeFalse();
        Result<DownloadTaskHandle> retried = service.Retry(handle.TaskId);
        retried.IsFailure.Should().BeTrue();
        retried.ErrorCode.Should().Be(DownloadTaskService.InvalidStateErrorCode);
        service.List().Should().ContainSingle("no replacement task is admitted");
    }
}
