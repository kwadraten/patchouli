using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Ocr;

namespace Patchouli.Tests;

public sealed class OcrPolicyRefactorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly OcrRetryPolicy Retry = new();

    private static OcrQueueTask TaskRow(
        string state = OcrQueueTaskState.Queued,
        string adapterKind = OcrAdapterKind.LocalLibrary,
        string? providerId = null,
        string engineId = "engine-a",
        string priority = OcrQueuePriority.UserStartedDocument,
        int attemptCount = 0,
        int pageCount = 2,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? scheduledAfter = null,
        OcrRunId? runId = null,
        int completedPages = 0,
        int failedPages = 0,
        OcrPresetVersionId? presetVersionId = null)
    {
        DateTimeOffset created = createdAt ?? Now;
        return new OcrQueueTask(OcrQueueTaskId.New(), LibraryId.New(), DocumentInstanceId.New(),
            OcrPresetId.New(), Enumerable.Range(0, pageCount).Select(_ => PageId.New()).ToArray(),
            OcrQueueTaskKind.Document, engineId, adapterKind, providerId, priority, created, created,
            state, attemptCount, 3, null, null, null, scheduledAfter, runId,
            completedPages, failedPages, PresetVersionId: presetVersionId);
    }

    [Fact]
    public void Run_binding_is_idempotent_for_the_same_run_and_records_ownership()
    {
        OcrQueueTask task = TaskRow(OcrQueueTaskState.Running, attemptCount: 1);
        OcrRunId runId = OcrRunId.New();
        OcrPresetVersionId version = OcrPresetVersionId.New();

        OcrRunBindingPolicy.Decision bound =
            OcrRunBindingPolicy.Evaluate(task, 1, runId, version, Now);
        bound.Result.IsSuccess.Should().BeTrue();
        bound.RecordsBinding.Should().BeTrue();
        bound.UpdatedTask.Should().NotBeNull();
        bound.UpdatedTask!.RunId.Should().Be(runId);
        bound.UpdatedTask.PresetVersionId.Should().Be(version,
            "an unpinned task learns the durable version at binding");

        OcrRunBindingPolicy.Decision repeated =
            OcrRunBindingPolicy.Evaluate(bound.UpdatedTask, 1, runId, null, Now);
        repeated.Result.IsSuccess.Should().BeTrue();
        repeated.UpdatedTask.Should().BeNull();
        repeated.RecordsBinding.Should().BeTrue(
            "the repeat still records so a later foreign run conflicts");
    }

    [Fact]
    public void Run_binding_conflicts_on_foreign_runs_and_fixed_version_mismatch()
    {
        OcrQueueTask bound = TaskRow(OcrQueueTaskState.Running, attemptCount: 1,
            runId: OcrRunId.New());
        OcrRunBindingPolicy.Decision foreign =
            OcrRunBindingPolicy.Evaluate(bound, 1, OcrRunId.New(), null, Now);
        foreign.Result.ErrorCode.Should().Be(AppErrorCodes.Conflict);
        foreign.UpdatedTask.Should().BeNull();
        foreign.RecordsBinding.Should().BeFalse();

        OcrPresetVersionId pinned = OcrPresetVersionId.New();
        OcrQueueTask pinnedTask = TaskRow(OcrQueueTaskState.Running, attemptCount: 1,
            presetVersionId: pinned);
        OcrRunBindingPolicy.Evaluate(pinnedTask, 1, OcrRunId.New(), OcrPresetVersionId.New(), Now)
            .Result.ErrorCode.Should().Be(AppErrorCodes.Conflict);
        OcrRunBindingPolicy.Evaluate(pinnedTask, 1, OcrRunId.New(), pinned, Now)
            .UpdatedTask!.PresetVersionId.Should().Be(pinned, "the fixed version is kept");
    }

    [Fact]
    public void Run_binding_and_progress_reject_removed_or_superseded_attempts()
    {
        OcrRunBindingPolicy.Evaluate(null, 1, OcrRunId.New(), null, Now)
            .Result.ErrorCode.Should().Be(AppErrorCodes.InvalidState);

        OcrQueueTask moved = TaskRow(OcrQueueTaskState.Running, attemptCount: 2);
        OcrRunBindingPolicy.Evaluate(moved, 1, OcrRunId.New(), null, Now)
            .Result.ErrorCode.Should().Be(AppErrorCodes.InvalidState,
                "a report from a superseded attempt cannot mutate its replacement");
        OcrRunBindingPolicy.Evaluate(TaskRow(attemptCount: 1), 1, OcrRunId.New(), null, Now)
            .Result.ErrorCode.Should().Be(AppErrorCodes.InvalidState);

        OcrQueueLifecyclePolicy.IsLiveClaim(moved, 2).Should().BeTrue();
        OcrQueueLifecyclePolicy.IsLiveClaim(moved, 1).Should().BeFalse();
        OcrQueueLifecyclePolicy.IsLiveClaim(
            moved with { State = OcrQueueTaskState.Cancelled }, 2).Should().BeFalse();

        OcrTaskProgressReport report = new(moved.TaskId, OcrTaskStage.Recognizing, 0.5, null,
            2);
        OcrQueueProgressPolicy.Accepts(moved, report).Should().BeTrue();
        OcrQueueProgressPolicy.Accepts(moved, report with { Attempt = 1 }).Should().BeFalse();
        OcrQueueProgressPolicy.Accepts(
            moved with { State = OcrQueueTaskState.Queued }, report).Should().BeFalse();
    }

    [Fact]
    public void Settlement_fails_an_attempt_whose_worker_reports_a_foreign_run()
    {
        OcrRunId owned = OcrRunId.New();
        OcrQueueTask claimed = TaskRow(OcrQueueTaskState.Running, runId: owned);

        OcrQueueTask settled = OcrQueueCompletionPolicy.SettleAttempt(claimed, claimed,
            new OcrQueueExecutionResult(true, false, RunId: OcrRunId.New()), owned, Retry, Now);

        settled.State.Should().Be(OcrQueueTaskState.Failed);
        settled.LastErrorCode.Should().Be("run_identity_mismatch");
        settled.RunId.Should().Be(owned, "ownership never transfers to the foreign run");

        OcrRunId reported = OcrRunId.New();
        OcrQueueTask unbound = TaskRow(OcrQueueTaskState.Running);
        OcrQueueTask adopted = OcrQueueCompletionPolicy.SettleAttempt(unbound, unbound,
            new OcrQueueExecutionResult(true, false, RunId: reported), null, Retry, Now);
        adopted.State.Should().Be(OcrQueueTaskState.Succeeded);
        adopted.RunId.Should().Be(reported, "an unbound attempt adopts the reported run");
    }

    [Fact]
    public void Manual_retry_retains_a_finished_run_only_for_a_matching_preset_version()
    {
        OcrPresetVersionId version = OcrPresetVersionId.New();
        OcrRunId runId = OcrRunId.New();
        OcrQueueTask failed = TaskRow(OcrQueueTaskState.Failed, runId: runId,
            completedPages: 2, presetVersionId: version);

        OcrQueueTask same =
            OcrQueueLifecyclePolicy.CreateRetry(failed, version, OcrQueueTaskId.New(), Now);
        same.RunId.Should().Be(runId);
        same.CompletedPageCount.Should().Be(2);
        OcrQueueLifecyclePolicy.CreateRetry(failed, null, OcrQueueTaskId.New(), Now)
            .RunId.Should().Be(runId, "a retry without an explicit version keeps the pin");

        OcrPresetVersionId other = OcrPresetVersionId.New();
        OcrQueueTask repinned =
            OcrQueueLifecyclePolicy.CreateRetry(failed, other, OcrQueueTaskId.New(), Now);
        repinned.RunId.Should().BeNull("a different pinned version must re-recognize");
        repinned.CompletedPageCount.Should().Be(0);
        repinned.PresetVersionId.Should().Be(other);
    }

    [Fact]
    public void Manual_retry_drops_the_run_for_partial_coverage_and_requeues_fresh()
    {
        OcrRunId runId = OcrRunId.New();
        OcrQueueTask partial = TaskRow(OcrQueueTaskState.Failed, runId: runId,
            completedPages: 1);
        OcrQueueLifecyclePolicy.CreateRetry(partial, null, OcrQueueTaskId.New(), Now)
            .RunId.Should().BeNull();
        OcrQueueTask withFailures = TaskRow(OcrQueueTaskState.Failed, runId: runId,
            completedPages: 2, failedPages: 1);
        OcrQueueLifecyclePolicy.CreateRetry(withFailures, null, OcrQueueTaskId.New(), Now)
            .CompletedPageCount.Should().Be(0);

        OcrQueueTask blocked = TaskRow(OcrQueueTaskState.Blocked, attemptCount: 3,
                scheduledAfter: Now.AddMinutes(5)) with
            {
                LastErrorCode = "missing_executable", LastErrorMessage = "install it"
            };
        OcrQueueTaskId retryId = OcrQueueTaskId.New();
        OcrQueueTask retry = OcrQueueLifecyclePolicy.CreateRetry(blocked, null, retryId, Now);
        retry.TaskId.Should().Be(retryId);
        retry.RetryOfTaskId.Should().Be(blocked.TaskId);
        retry.State.Should().Be(OcrQueueTaskState.Queued);
        retry.AttemptCount.Should().Be(0);
        retry.Priority.Should().Be(OcrQueuePriority.BackgroundRetry);
        retry.Source.Should().Be(OcrQueueSource.Retry);
        retry.ScheduledAfter.Should().BeNull();
        retry.LastErrorCode.Should().BeNull();
    }

    [Fact]
    public void Transient_requeue_backs_off_and_retains_only_trusted_recognition()
    {
        OcrRunId runId = OcrRunId.New();
        OcrQueueTask claimed = TaskRow(OcrQueueTaskState.Running, attemptCount: 1);

        OcrQueueTask retained = OcrQueueCompletionPolicy.SettleAttempt(claimed, claimed,
            new OcrQueueExecutionResult(false, false, "network_timeout", "temporary",
                CompletedPageCount: 2), runId, Retry, Now);
        retained.State.Should().Be(OcrQueueTaskState.Queued);
        retained.AttemptCount.Should().Be(2);
        retained.ScheduledAfter.Should().Be(Now.AddSeconds(30));
        retained.RunId.Should().Be(runId);
        retained.CompletedPageCount.Should().Be(2);

        OcrQueueTask restarted = OcrQueueCompletionPolicy.SettleAttempt(claimed, claimed,
            new OcrQueueExecutionResult(false, false, "network_timeout", "temporary",
                CompletedPageCount: 1), runId, Retry, Now);
        restarted.RunId.Should().BeNull("incomplete recognition starts a new run");
        restarted.CompletedPageCount.Should().Be(0);

        OcrQueueTask failures = OcrQueueCompletionPolicy.SettleAttempt(claimed, claimed,
            new OcrQueueExecutionResult(false, false, "network_timeout", "temporary",
                CompletedPageCount: 2, FailedPageCount: 1), runId, Retry, Now);
        failures.RunId.Should().BeNull("failed pages disqualify retention");
    }

    [Fact]
    public void Settlement_blocks_manual_repairs_and_local_timeout_retries_only_once()
    {
        OcrQueueTask claimed = TaskRow(OcrQueueTaskState.Running, attemptCount: 2);
        OcrQueueCompletionPolicy.SettleAttempt(claimed, claimed,
                new OcrQueueExecutionResult(false, false, "missing_executable", "install it"),
                null, Retry, Now)
            .State.Should().Be(OcrQueueTaskState.Blocked);
        OcrQueueCompletionPolicy.SettleAttempt(claimed, claimed,
                new OcrQueueExecutionResult(false, false, "unmapped_failure", "boom"),
                null, Retry, Now)
            .State.Should().Be(OcrQueueTaskState.Failed);

        Retry.ShouldRetry(TaskRow(attemptCount: 0), OcrFailureCode.LocalOcrTimeout)
            .Should().BeTrue();
        Retry.ShouldRetry(TaskRow(attemptCount: 1), OcrFailureCode.LocalOcrTimeout)
            .Should().BeFalse("a local OCR timeout gets one retry regardless of MaxAttempts");
        Retry.ShouldRetry(TaskRow(attemptCount: 1), "network_timeout").Should().BeTrue();
        Retry.ShouldRetry(TaskRow(attemptCount: 3), "network_timeout").Should().BeFalse();
    }

    [Fact]
    public void Pause_keys_and_scopes_suppress_only_their_own_lane()
    {
        OcrQueuePausePolicy.Key(OcrPauseScope.Global, null).Should().Be("global:");
        OcrQueuePausePolicy.Key(OcrPauseScope.Task, "abc").Should().Be("task:abc");
        OcrQueuePausePolicy.IsUnsupportedScope("preset").Should().BeTrue();
        OcrQueuePausePolicy.IsUnsupportedScope(OcrPauseScope.Provider).Should().BeFalse();

        OcrQueueTask local = TaskRow(providerId: "prov-a");
        OcrQueueTask cloud = TaskRow(adapterKind: OcrAdapterKind.CloudApi, providerId: "prov-b");
        OcrQueuePausePolicy.IsPaused(local, new HashSet<string> { "local:" }).Should().BeTrue();
        OcrQueuePausePolicy.IsPaused(cloud, new HashSet<string> { "local:" }).Should().BeFalse();
        OcrQueuePausePolicy.IsPaused(cloud, new HashSet<string> { "cloud:" }).Should().BeTrue();
        OcrQueuePausePolicy.IsPaused(local, new HashSet<string> { "cloud:" }).Should().BeFalse();
        OcrQueuePausePolicy.IsPaused(local, new HashSet<string> { "provider:prov-a" }).Should().BeTrue();
        OcrQueuePausePolicy.IsPaused(cloud, new HashSet<string> { "provider:prov-a" }).Should().BeFalse();
        OcrQueuePausePolicy.IsPaused(local, new HashSet<string> { $"task:{local.TaskId}" }).Should().BeTrue();
        OcrQueuePausePolicy.IsPaused(cloud, new HashSet<string> { $"task:{local.TaskId}" }).Should().BeFalse();
        OcrQueuePausePolicy.IsPaused(local, new HashSet<string> { "global:" }).Should().BeTrue();
        OcrQueuePausePolicy.IsPaused(cloud, new HashSet<string> { "global:" }).Should().BeTrue();
        OcrQueuePausePolicy.IsPaused(TaskRow(), new HashSet<string> { "provider:prov-a" }).Should()
            .BeFalse("a task without a provider id cannot match a provider pause");
    }

    [Fact]
    public void Dispatch_score_prioritizes_priority_then_caps_aging()
    {
        OcrQueueTask fresh = TaskRow(priority: OcrQueuePriority.InteractiveCurrentPage);
        OcrQueueTask ancient = TaskRow(priority: OcrQueuePriority.BatchCollection,
            createdAt: Now.AddDays(-30));
        OcrQueueDispatchPolicy.Score(fresh, Now).Should().BeGreaterThan(
            OcrQueueDispatchPolicy.Score(ancient, Now),
            "aging adds at most 999 points and never crosses a priority band");

        OcrQueueDispatchPolicy.Score(TaskRow(createdAt: Now.AddMinutes(-20)), Now)
            .Should().Be(4002, "one point per 10 queued minutes");
        OcrQueueDispatchPolicy.Score(TaskRow(createdAt: Now.AddMinutes(-9)), Now)
            .Should().Be(4000);
    }

    [Fact]
    public void Concurrency_limits_gate_each_lane_at_its_inclusive_boundary()
    {
        OcrQueueLimits limits = new(2, 1, 2, 1, 1);
        List<OcrQueueTask> tasks =
            [TaskRow(OcrQueueTaskState.Running, providerId: "prov-a")];

        OcrQueueDispatchPolicy.CanRunNow(TaskRow(engineId: "engine-b"), tasks, limits)
            .Should().BeFalse("the local lane is full");
        OcrQueueDispatchPolicy.CanRunNow(
                TaskRow(adapterKind: OcrAdapterKind.CloudApi, providerId: "prov-b"), tasks, limits)
            .Should().BeFalse("the per-engine limit spans lanes");
        OcrQueueDispatchPolicy.CanRunNow(
                TaskRow(adapterKind: OcrAdapterKind.CloudApi, engineId: "engine-b",
                    providerId: "prov-a"), tasks, limits)
            .Should().BeFalse("the per-provider limit spans lanes");
        OcrQueueDispatchPolicy.CanRunNow(
                TaskRow(adapterKind: OcrAdapterKind.CloudApi, engineId: "engine-b",
                    providerId: "prov-b"), tasks, limits)
            .Should().BeTrue("one free slot remains in every dimension");

        tasks.Add(TaskRow(OcrQueueTaskState.Running, OcrAdapterKind.CloudApi,
            engineId: "engine-b", providerId: "prov-b"));
        OcrQueueDispatchPolicy.CanRunNow(
                TaskRow(adapterKind: OcrAdapterKind.CloudApi, engineId: "engine-c",
                    providerId: "prov-c"), tasks, limits)
            .Should().BeFalse("the global ceiling is reached");
    }

    [Fact]
    public void Deferred_wake_and_idle_ignore_paused_or_past_due_work()
    {
        OcrQueueTask soon = TaskRow(scheduledAfter: Now.AddSeconds(30));
        OcrQueueTask later = TaskRow(scheduledAfter: Now.AddSeconds(90));
        OcrQueueTask paused = TaskRow(scheduledAfter: Now.AddSeconds(5));
        OcrQueueTask pastDue = TaskRow(scheduledAfter: Now.AddSeconds(-5));
        HashSet<string> pauses = [$"task:{paused.TaskId}"];

        OcrQueueDispatchPolicy.NextDeferredWakeDelay([soon, later, paused, pastDue], Now, pauses)
            .Should().Be(TimeSpan.FromSeconds(30), "the nearest unpaused deferred task wins");
        OcrQueueDispatchPolicy.NextDeferredWakeDelay([pastDue, paused], Now, pauses)
            .Should().BeNull();

        OcrQueueDispatchPolicy.IsIdle([soon], 0, Now, new HashSet<string>()).Should()
            .BeTrue("a deferred backlog still counts as idle");
        OcrQueueDispatchPolicy.IsIdle([TaskRow()], 0, Now, new HashSet<string>()).Should().BeFalse();
        OcrQueueDispatchPolicy.IsIdle([], 1, Now, new HashSet<string>()).Should()
            .BeFalse("an in-flight attempt is not idle");
    }

    [Fact]
    public void Dispatch_candidates_skip_claimed_or_future_tasks_and_order_by_score_then_age()
    {
        OcrQueueTask winner = TaskRow(priority: OcrQueuePriority.InteractiveSelectedPages,
            createdAt: Now.AddMinutes(-30));
        OcrQueueTask tied = TaskRow(priority: OcrQueuePriority.InteractiveSelectedPages);
        OcrQueueTask claimedElsewhere = TaskRow(priority: OcrQueuePriority.InteractiveCurrentPage);
        OcrQueueTask future = TaskRow(priority: OcrQueuePriority.InteractiveCurrentPage,
            scheduledAfter: Now.AddMinutes(1));

        OcrQueueDispatchPolicy.SelectDispatchCandidates(
                [future, tied, claimedElsewhere, winner], [claimedElsewhere.TaskId], Now, new HashSet<string>())
            .Should().Equal(winner, tied);
    }

    [Fact]
    public void Progress_notifications_are_throttled_except_terminal_reports()
    {
        OcrQueueProgressPolicy.ShouldNotify(false, Now.AddMilliseconds(-199), Now)
            .Should().BeFalse();
        OcrQueueProgressPolicy.ShouldNotify(false, Now.AddMilliseconds(-200), Now)
            .Should().BeTrue();
        OcrQueueProgressPolicy.ShouldNotify(false, null, Now).Should().BeTrue();
        OcrQueueProgressPolicy.ShouldNotify(true, Now, Now).Should()
            .BeTrue("terminal reports always publish");
        OcrQueueProgressPolicy.IsTerminal(
                new OcrTaskProgressReport(OcrQueueTaskId.New(), OcrTaskStage.Recognizing, 1.0, null))
            .Should().BeTrue();
        OcrQueueProgressPolicy.IsTerminal(
                new OcrTaskProgressReport(OcrQueueTaskId.New(), OcrTaskStage.Recognizing, 0.999, null))
            .Should().BeFalse();
    }
}
