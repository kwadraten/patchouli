using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Ocr;

namespace Patchouli.Tests;

public sealed class OcrQueueSchedulingRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IsPaused_matches_global_task_kind_and_provider_scopes()
    {
        OcrQueueTask local = CreateTask(adapterKind: OcrAdapterKind.LocalLibrary, providerId: "ndl");
        OcrQueueTask cloud = CreateTask(adapterKind: OcrAdapterKind.CloudApi, providerId: "mineru");

        OcrQueueSchedulingRules.IsPaused(new HashSet<string> { "global:" }, local).Should().BeTrue();
        OcrQueueSchedulingRules.IsPaused(new HashSet<string> { "local:" }, local).Should().BeTrue();
        OcrQueueSchedulingRules.IsPaused(new HashSet<string> { "local:" }, cloud).Should().BeFalse();
        OcrQueueSchedulingRules.IsPaused(new HashSet<string> { "cloud:" }, cloud).Should().BeTrue();
        OcrQueueSchedulingRules.IsPaused(new HashSet<string> { "cloud:" }, local).Should().BeFalse();
        OcrQueueSchedulingRules.IsPaused(new HashSet<string> { "provider:mineru" }, cloud).Should().BeTrue();
        OcrQueueSchedulingRules.IsPaused(new HashSet<string> { "provider:ndl" }, cloud).Should().BeFalse();
        OcrQueueSchedulingRules.IsPaused(new HashSet<string> { $"task:{local.TaskId}" }, local).Should().BeTrue();
        OcrQueueSchedulingRules.IsPaused(new HashSet<string>(), local).Should().BeFalse();
    }

    [Fact]
    public void Dispatch_eligibility_requires_queued_unpaused_and_due()
    {
        OcrQueueTask due = CreateTask();
        OcrQueueTask delayed = CreateTask(scheduledAfter: Now.AddMinutes(5));
        OcrQueueTask running = CreateTask(OcrQueueTaskState.Running);
        HashSet<string> pauses = ["global:"];

        OcrQueueSchedulingRules.IsDispatchEligible(due, new HashSet<string>(), Now).Should().BeTrue();
        OcrQueueSchedulingRules.IsDispatchEligible(due, pauses, Now).Should().BeFalse();
        OcrQueueSchedulingRules.IsDispatchEligible(delayed, new HashSet<string>(), Now).Should().BeFalse();
        OcrQueueSchedulingRules.IsDispatchEligible(running, new HashSet<string>(), Now).Should().BeFalse();
        OcrQueueSchedulingRules.IsDispatchEligible(delayed with { ScheduledAfter = Now }, new HashSet<string>(), Now)
            .Should().BeTrue("a task due exactly now may dispatch");
    }

    [Fact]
    public void HasDispatchCapacity_enforces_all_concurrency_limits()
    {
        OcrQueueLimits limits = new(2, 1, 1,
            1, 1);
        OcrQueueTask local = CreateTask(engineId: "rapidocr", adapterKind: OcrAdapterKind.LocalLibrary,
            providerId: "local");
        OcrQueueTask cloud = CreateTask(engineId: "mineru", adapterKind: OcrAdapterKind.CloudApi,
            providerId: "mineru");

        OcrQueueSchedulingRules.HasDispatchCapacity(cloud, [], limits).Should().BeTrue();
        OcrQueueSchedulingRules.HasDispatchCapacity(cloud, [Running(cloud)], limits)
            .Should().BeFalse("cloud cap is 1");
        OcrQueueSchedulingRules.HasDispatchCapacity(local, [Running(cloud)], limits)
            .Should().BeTrue("a local task still fits under cloud and global caps");
        OcrQueueSchedulingRules.HasDispatchCapacity(local, [Running(cloud), Running(local)], limits)
            .Should().BeFalse("global cap is 2");
        OcrQueueSchedulingRules.HasDispatchCapacity(
                CreateTask(engineId: "mineru", adapterKind: OcrAdapterKind.CloudApi, providerId: "other"),
                [Running(cloud)], limits)
            .Should().BeFalse("per-engine cap blocks a second mineru run");
        OcrQueueSchedulingRules.HasDispatchCapacity(
                CreateTask(engineId: "other", adapterKind: OcrAdapterKind.CloudApi, providerId: "mineru"),
                [Running(cloud)], limits)
            .Should().BeFalse("per-provider cap blocks a second mineru provider run");
    }

    [Fact]
    public void DispatchScore_orders_by_priority_then_age()
    {
        OcrQueueTask interactive = CreateTask(priority: OcrQueuePriority.InteractiveCurrentPage,
            createdAt: Now.AddMinutes(-1));
        OcrQueueTask maintenance = CreateTask(priority: OcrQueuePriority.Maintenance,
            createdAt: Now.AddDays(-10));

        OcrQueueSchedulingRules.DispatchScore(interactive, Now)
            .Should().BeGreaterThan(OcrQueueSchedulingRules.DispatchScore(maintenance, Now));
        OcrQueueSchedulingRules.DispatchScore(maintenance, Now).Should().Be(1000 + 999,
            "age bonus saturates at 999");
    }

    [Fact]
    public void Describe_marks_blocked_paused_delayed_and_running_states()
    {
        OcrQueueActivityState blocked = OcrQueueSchedulingRules.Describe(
            CreateTask(OcrQueueTaskState.Blocked, lastErrorMessage: "missing_executable"), new HashSet<string>(), Now);
        blocked.Detail.Should().Be("等待人工处理");
        blocked.IsPaused.Should().BeTrue();
        blocked.PauseReason.Should().Contain("missing_executable");

        OcrQueueActivityState blockedNoMessage = OcrQueueSchedulingRules.Describe(
            CreateTask(OcrQueueTaskState.Blocked), new HashSet<string>(), Now);
        blockedNoMessage.PauseReason.Should().Be("需要人工处理后重试");

        OcrQueueActivityState paused = OcrQueueSchedulingRules.Describe(
            CreateTask(), new HashSet<string> { "global:" }, Now);
        paused.Detail.Should().Be("已暂停");
        paused.IsPaused.Should().BeTrue();
        paused.IsWaitingRetry.Should().BeFalse();

        DateTimeOffset scheduled = Now.AddMinutes(3);
        OcrQueueActivityState waiting = OcrQueueSchedulingRules.Describe(
            CreateTask(scheduledAfter: scheduled), new HashSet<string>(), Now);
        waiting.Detail.Should().Be("等待延迟重试");
        waiting.IsWaitingRetry.Should().BeTrue();
        waiting.RetryReason.Should().Be($"将在 {scheduled:HH:mm:ss} 重试");

        OcrQueueActivityState running = OcrQueueSchedulingRules.Describe(
            CreateTask(OcrQueueTaskState.Running), new HashSet<string>(), Now);
        running.Detail.Should().Be("正在执行");

        OcrQueueActivityState queued = OcrQueueSchedulingRules.Describe(CreateTask(), new HashSet<string>(), Now);
        queued.Detail.Should().Be("等待调度");
    }

    [Fact]
    public void ActiveStates_excludes_finished_and_retry_superseded_blocked_tasks()
    {
        OcrQueueTask blocked = CreateTask(OcrQueueTaskState.Blocked);
        OcrQueueTask retry = CreateTask(retryOf: blocked.TaskId);
        OcrQueueTask succeeded = CreateTask(OcrQueueTaskState.Succeeded);
        OcrQueueTask running = CreateTask(OcrQueueTaskState.Running);

        OcrQueueActivityState[] states = OcrQueueSchedulingRules.ActiveStates(
            [blocked, retry, succeeded, running], new HashSet<string>(), Now);

        states.Select(state => state.TaskId).Should().BeEquivalentTo([retry.TaskId, running.TaskId],
            "a blocked task hidden behind a queued retry and finished tasks are not activities");
    }

    private static OcrQueueTask Running(OcrQueueTask task)
    {
        return task with { State = OcrQueueTaskState.Running };
    }

    private static OcrQueueTask CreateTask(
        string state = OcrQueueTaskState.Queued,
        string engineId = "rapidocr",
        string adapterKind = OcrAdapterKind.LocalLibrary,
        string? providerId = null,
        string priority = OcrQueuePriority.UserStartedDocument,
        DateTimeOffset? scheduledAfter = null,
        OcrQueueTaskId? retryOf = null,
        string? lastErrorMessage = null,
        DateTimeOffset? createdAt = null)
    {
        DateTimeOffset created = createdAt ?? Now;
        return new OcrQueueTask(
            OcrQueueTaskId.New(),
            LibraryId.New(),
            DocumentInstanceId.New(),
            OcrPresetId.New(),
            [PageId.New()],
            OcrQueueTaskKind.MockPages,
            engineId,
            adapterKind,
            providerId,
            priority,
            created,
            created,
            state,
            0,
            3,
            retryOf,
            null,
            lastErrorMessage,
            scheduledAfter,
            null,
            null);
    }
}
