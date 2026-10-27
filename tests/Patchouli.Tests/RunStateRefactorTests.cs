using FluentAssertions;
using Patchouli.Core.Bibliography.MetadataLookup;
using Patchouli.Core.Ids;
using Patchouli.Core.Translation;
using Patchouli.Host.Metadata;
using Patchouli.Host.Translation;
using Patchouli.Infrastructure.Translation;

namespace Patchouli.Tests;

public sealed class RunStateRefactorTests
{
    [Fact]
    public void Translation_cancellation_is_accepted_only_while_running_and_preserves_the_step()
    {
        TranslationStepProgress step = new("page-translation", 1, 2, "Started", PageIndex: 1);
        TranslationRunPhase running = TranslationRunPhase.Started(2).WithStep(step);
        TranslationRunPhase cancelled = running.TryCancelled()!;

        cancelled.Lifecycle.Should().Be(TranslationRunLifecycle.Cancelled);
        cancelled.ErrorCode.Should().Be("cancelled");
        cancelled.CurrentStep.Should().Be(step);
        cancelled.TryCancelled().Should().BeNull();
        running.Completed(new TranslationRunResult([], false)).TryCancelled().Should().BeNull();
        running.Failed("failed", "failure").TryCancelled().Should().BeNull();
        running.Lifecycle.Should().Be(TranslationRunLifecycle.Running);
    }

    [Fact]
    public void Existing_terminal_overwrite_order_is_preserved()
    {
        // Characterize the existing late-result behavior independently of thread scheduling.
        TranslationRunPhase cancelled = TranslationRunPhase.Started(1).Cancelled();
        TranslationRunPhase completed = cancelled.Completed(new TranslationRunResult([], false));
        completed.Lifecycle.Should().Be(TranslationRunLifecycle.Completed);
        completed.ErrorCode.Should().BeNull();
        completed.ErrorMessage.Should().BeNull();
        cancelled.Failed("failed", "failure").Lifecycle.Should().Be(TranslationRunLifecycle.Failed);
        completed.Completed(new TranslationRunResult([], true)).Lifecycle
            .Should().Be(TranslationRunLifecycle.Cancelled);

        MetadataRunPhase metadata = MetadataRunPhase.Started.Cancelled();
        metadata.Completed().State.Should().Be(MetadataRunState.Completed);
        metadata.Completed().ErrorCode.Should().BeNull();
        metadata.Failed("failed", "failure").State.Should().Be(MetadataRunState.Failed);
        metadata.TryCancelled().Should().BeNull();
    }

    [Fact]
    public void Translation_projection_counts_settled_slots_and_only_running_runs_are_paused()
    {
        TranslationPageRunState[] states =
        [
            TranslationPageRunState.Committed, TranslationPageRunState.Partial,
            TranslationPageRunState.Failed, TranslationPageRunState.Skipped, TranslationPageRunState.Cancelled
        ];
        TranslationPageOutcome?[] outcomes = states.Select((state, index) =>
                (TranslationPageOutcome?)new TranslationPageOutcome(PageId.New(), index + 1, state, 0))
            .Append(null).ToArray();
        TranslationRunPhase phase = TranslationRunPhase.Started(6);
        TranslationRunStatus status = phase.Project(Guid.NewGuid(), DocumentInstanceId.New(), 6, outcomes, true);

        status.CompletedPages.Should().Be(5);
        status.TotalPages.Should().Be(6);
        status.Succeeded.Should().Be(1);
        status.Partial.Should().Be(1);
        status.Failed.Should().Be(1);
        status.Skipped.Should().Be(1);
        status.CancelledPages.Should().Be(1);
        status.IsPaused.Should().BeTrue();
        phase.Cancelled().Project(status.RunId, status.DocumentInstanceId, 6, outcomes, true)
            .IsPaused.Should().BeFalse();
    }

    [Fact]
    public void Translation_tracker_ignores_foreign_progress_and_merges_final_outcomes()
    {
        using CancellationTokenSource cancellation = new();
        DocumentInstanceId document = DocumentInstanceId.New();
        TranslationPageTarget first = new(PageId.New(), 1);
        TranslationPageTarget second = new(PageId.New(), 2);
        TranslationRunTracker tracker = new(Guid.NewGuid(), document, 1, [first, second], cancellation,
            new TranslationRunStartRequest(document, [1, 2], "en"));
        tracker.RecordProgress(new TranslationRunProgress(1, 2,
            new TranslationPageOutcome(PageId.New(), 99, TranslationPageRunState.Failed, 0)));
        tracker.Snapshot().CompletedPages.Should().Be(0);
        tracker.RecordProgress(new TranslationRunProgress(1, 2,
            new TranslationPageOutcome(first.PageId, 1, TranslationPageRunState.Partial, 1)));
        tracker.TryCancel().Should().BeTrue();
        cancellation.IsCancellationRequested.Should().BeTrue();
        tracker.TryCancel().Should().BeFalse();
        tracker.Complete(new TranslationRunResult([
            new TranslationPageOutcome(first.PageId, 1, TranslationPageRunState.Committed, 5),
            new TranslationPageOutcome(second.PageId, 2, TranslationPageRunState.Skipped, 0)
        ], false));

        TranslationRunStatus status = tracker.Snapshot();
        status.State.Should().Be(TranslationRunLifecycle.Completed);
        status.Pages.Select(page => page.PageIndex).Should().Equal(1, 2);
        status.Pages[0].CommittedBoxCount.Should().Be(5);
        status.Succeeded.Should().Be(1);
        status.Skipped.Should().Be(1);
    }

    [Fact]
    public void Metadata_cancellation_projects_pending_items_without_counting_them_as_settled()
    {
        using CancellationTokenSource cancellation = new();
        ItemId first = ItemId.New();
        ItemId second = ItemId.New();
        MetadataBatchTracker tracker = new(Guid.NewGuid(), [first, second], 1, cancellation);
        tracker.RecordItem(0, new MetadataBatchItemResult(first, true, "source", null, null));
        tracker.Snapshot().Items[1].ErrorCode.Should().BeNull();
        tracker.TryMarkCancelled().Should().BeTrue();
        tracker.TryMarkCancelled().Should().BeFalse();
        cancellation.IsCancellationRequested.Should().BeFalse("the service owns cancellation signaling");

        MetadataBatchRunStatus marked = tracker.Snapshot();
        marked.Completed.Should().Be(1);
        marked.Succeeded.Should().Be(1);
        marked.Failed.Should().Be(0);
        marked.Items[1].ErrorCode.Should().Be("cancelled");
        tracker.CompleteCancelled();
        tracker.Snapshot().Completed.Should().Be(2);
        tracker.Snapshot().Failed.Should().Be(0);
    }

    [Fact]
    public void Metadata_failure_fills_only_unsettled_slots()
    {
        using CancellationTokenSource cancellation = new();
        ItemId first = ItemId.New();
        ItemId second = ItemId.New();
        MetadataBatchTracker tracker = new(Guid.NewGuid(), [first, second], 1, cancellation);
        tracker.RecordItem(0, new MetadataBatchItemResult(first, true, "source", null, null));
        tracker.Fail("lookup_failed", "No candidate");

        MetadataBatchRunStatus failed = tracker.Snapshot();
        failed.State.Should().Be(MetadataRunState.Failed);
        failed.Completed.Should().Be(2);
        failed.Succeeded.Should().Be(1);
        failed.Failed.Should().Be(1);
        failed.Items[0].SourceId.Should().Be("source");
        failed.Items[1].ErrorCode.Should().Be("lookup_failed");
    }

    [Theory]
    [InlineData(TranslationPageRunState.Committed, "Completed")]
    [InlineData(TranslationPageRunState.Partial, "Completed")]
    [InlineData(TranslationPageRunState.Skipped, "Completed")]
    [InlineData(TranslationPageRunState.Failed, "Failed")]
    [InlineData(TranslationPageRunState.Cancelled, "Cancelled")]
    [InlineData((TranslationPageRunState)42, "Completed")]
    public void Page_step_labels_preserve_existing_mapping(TranslationPageRunState state, string expected)
    {
        HostTranslationJob.StepStateFor(state).Should().Be(expected);
    }
}
