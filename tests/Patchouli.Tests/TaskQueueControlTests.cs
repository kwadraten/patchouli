using FluentAssertions;
using Patchouli.Core.Results;
using Patchouli.Core.Translation;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Translation;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.Translation;
using Patchouli.Llm;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

public sealed class TaskQueueControlTests
{
    [Fact]
    public async Task Global_resume_preserves_local_pause_and_stopped_runs_can_be_cancelled()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        await using TranslationRunService service = CreateService(context);
        (await service.SetPausedAsync(true)).IsSuccess.Should().BeTrue();
        TranslationRunStartRequest request = new(context.DocumentId, []);
        TranslationRunStatus first = (await service.StartAsync(request)).Value;
        TranslationRunStatus second = (await service.StartAsync(request)).Value;
        first.IsPaused.Should().BeTrue();
        second.IsPaused.Should().BeTrue();
        await service.SetPausedAsync(true, first.RunId);
        await service.SetPausedAsync(false);
        (await service.GetAsync(first.RunId)).Value.IsPaused.Should().BeTrue();
        await WaitForFinishAsync(service, second.RunId);
        await service.SetQueueRunningAsync(false);
        TranslationRunStatus third = (await service.StartAsync(request)).Value;
        third.IsPaused.Should().BeTrue();
        await service.SetPausedAsync(false, first.RunId);
        (await service.GetAsync(first.RunId)).Value.IsPaused.Should().BeTrue();
        (await service.CancelAsync(first.RunId)).Value.State.Should().Be(TranslationRunLifecycle.Cancelled);
        await service.SetQueueRunningAsync(true);
        await WaitForFinishAsync(service, third.RunId);
        (await service.SetPausedAsync(true, first.RunId)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Retrying_failed_run_is_idempotent_and_inherits_queue_pause()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        await using TranslationRunService service = CreateService(context);
        TranslationRunStatus first =
            (await service.StartAsync(new TranslationRunStartRequest(context.DocumentId, [1], "zh-CN"))).Value;
        TranslationRunStatus failed = await WaitForFinishAsync(service, first.RunId);
        failed.Failed.Should().Be(1, "the fixture has no committed source revision");
        await service.SetQueueRunningAsync(false);
        Task<Result<TranslationRunStatus>>[] retryTasks = new Task<Result<TranslationRunStatus>>[8];
        for (int index = 0; index < retryTasks.Length; index++)
        {
            retryTasks[index] = service.RetryAsync(first.RunId);
        }

        Result<TranslationRunStatus>[] retries = await Task.WhenAll(retryTasks);
        retries.Should().OnlyContain(result => result.IsSuccess);
        retries.Select(result => result.Value.RunId).Distinct().Should().ContainSingle();
        TranslationRunStatus retry = retries[0].Value;
        retry.RunId.Should().NotBe(first.RunId);
        retry.IsPaused.Should().BeTrue();
        retry.TotalPages.Should().Be(1);
        (await service.ListAsync()).Value.Should().HaveCount(2);
        (await service.RetryAsync(retry.RunId)).IsFailure.Should().BeTrue();
        await service.CancelAsync(retry.RunId);
    }

    [Fact]
    public async Task Translation_row_controls_follow_lifecycle_and_pause_state()
    {
        TranslationRunStatus run = new(Guid.NewGuid(), Core.Ids.DocumentInstanceId.New(),
            TranslationRunLifecycle.Running, 1, 0, 0, 0, 0, 0, 0, []);
        int pauses = 0, resumes = 0, cancels = 0, retries = 0;
        OcrQueueTaskViewModel row = new(run, "source", () =>
            {
                cancels++;
                return Task.CompletedTask;
            },
            () =>
            {
                pauses++;
                return Task.CompletedTask;
            }, () =>
            {
                resumes++;
                return Task.CompletedTask;
            },
            () =>
            {
                retries++;
                return Task.CompletedTask;
            });
        row.SupportsPause.Should().BeTrue();
        row.ResumeCommand.CanExecute(null).Should().BeFalse();
        await row.PauseCommand.ExecuteAsync();
        row.UpdateTranslation(run with { IsPaused = true }, "source");
        row.StateText.Should().Be("已暂停");
        row.PauseCommand.CanExecute(null).Should().BeFalse();
        await row.ResumeCommand.ExecuteAsync();
        await row.CancelCommand.ExecuteAsync();
        row.UpdateTranslation(run with { State = TranslationRunLifecycle.Failed, Failed = 1 }, "source");
        row.CanRetry.Should().BeTrue();
        row.CancelCommand.CanExecute(null).Should().BeFalse();
        await row.RetryCommand.ExecuteAsync();
        row.RetryStarted = true;
        row.CanRetry.Should().BeFalse();
        row.RetryCommand.CanExecute(null).Should().BeFalse();
        (pauses, resumes, cancels, retries).Should().Be((1, 1, 1, 1));
    }

    [Fact]
    public async Task Execution_gate_is_cancellable_and_reusable()
    {
        TranslationExecutionGate gate = new();
        gate.SetPaused(true);
        using CancellationTokenSource cancellation = new();
        Task cancelled = gate.WaitAsync(cancellation.Token);
        Task resumed = gate.WaitAsync();
        cancelled.IsCompleted.Should().BeFalse();
        resumed.IsCompleted.Should().BeFalse();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        resumed.IsCompleted.Should().BeFalse();
        gate.SetPaused(false);
        await resumed.WaitAsync(TimeSpan.FromSeconds(5));
        gate.SetPaused(true);
        Task next = gate.WaitAsync();
        next.IsCompleted.Should().BeFalse();
        gate.SetPaused(false);
        await next.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static TranslationRunService CreateService(TranslationTestContext context)
    {
        return new TranslationRunService(new PageService(context.ConnectionFactory, context.Clock), context.Trees,
            context.Translations, () => LlmAppSettings.Default(),
            TranslationTestExtensionHarness.CreateRunner(async (ITranslationJob job, IExtensionResources _,
                CancellationToken cancellationToken) =>
            {
                // Pull pages through the Host job so unreadable-page failures are actually settled.
                while (await job.NextPageAsync(cancellationToken) is { } page)
                {
                    ExtensionBoxOutcome[] outcomes = page.Boxes.Select(box =>
                        new ExtensionBoxOutcome(box.Id, ExtensionBoxOutcomeKind.Translated, "translated")).ToArray();
                    await job.CommitPageAsync(new TranslationPageResult(page.Page, outcomes), cancellationToken);
                }
            }, true, null),
            _ => Task.CompletedTask);
    }

    private static async Task<TranslationRunStatus> WaitForFinishAsync(TranslationRunService service, Guid id)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (true)
        {
            TranslationRunStatus status = (await service.GetAsync(id, timeout.Token)).Value;
            if (status.State != TranslationRunLifecycle.Running)
            {
                return status;
            }

            await Task.Delay(10, timeout.Token);
        }
    }
}
