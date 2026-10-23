using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.MetadataLookup;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Host.Metadata;

namespace Patchouli.Tests;

public sealed class MetadataRunServiceTests
{
    [Fact]
    public async Task Batch_records_per_item_results_and_completes()
    {
        ItemId first = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        ItemId second = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        StubLookup lookup = new();
        lookup.EnrichResults[first] = Result<MetadataLookupOutcome>.Success(Outcome(first, "crossref"));
        lookup.EnrichResults[second] = Result<MetadataLookupOutcome>.Failure(
            MetadataLookupErrorCodes.NotFound, "No metadata source found a matching record.");
        await using MetadataRunService runs = new(lookup);

        Result<MetadataBatchRunStatus> started = await runs.StartBatchAsync([first, second]);

        started.IsSuccess.Should().BeTrue();
        started.Value.State.Should().Be(MetadataRunState.Running);
        started.Value.Total.Should().Be(2);
        MetadataBatchRunStatus final = await WaitForTerminalAsync(runs, started.Value.TaskId);
        final.State.Should().Be(MetadataRunState.Completed);
        final.Succeeded.Should().Be(1);
        final.Failed.Should().Be(1);
        final.Items.Should().Equal(new MetadataBatchItemResult(first, true, "crossref", null, null),
            new MetadataBatchItemResult(second, false, null, MetadataLookupErrorCodes.NotFound,
                "No metadata source found a matching record."));
    }

    [Fact]
    public async Task Same_request_id_replays_the_receipt_and_a_different_message_conflicts()
    {
        ItemId item = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        StubLookup lookup = new();
        lookup.EnrichResults[item] = Result<MetadataLookupOutcome>.Success(Outcome(item, "crossref"));
        await using MetadataRunService runs = new(lookup);

        Result<MetadataBatchRunStatus> first = await runs.StartBatchAsync([item], "batch-1");
        Result<MetadataBatchRunStatus> replay = await runs.StartBatchAsync([item], "batch-1");

        replay.IsSuccess.Should().BeTrue();
        replay.Value.Reused.Should().BeTrue();
        replay.Value.TaskId.Should().Be(first.Value.TaskId);

        Result<MetadataBatchRunStatus> conflict = await runs.StartBatchAsync(
            [new ItemId(Guid.Parse("22222222-2222-2222-2222-222222222222"))], "batch-1");
        conflict.IsFailure.Should().BeTrue();
        conflict.ErrorCode.Should().Be(AppErrorCodes.Conflict);
    }

    [Fact]
    public async Task Cancel_keeps_committed_items_and_marks_unfinished_cancelled()
    {
        ItemId fast = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        ItemId slow = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        StubLookup lookup = new();
        lookup.EnrichResults[fast] = Result<MetadataLookupOutcome>.Success(Outcome(fast, "crossref"));
        lookup.EnrichDelays[slow] = TimeSpan.FromSeconds(30);
        lookup.EnrichResults[slow] = Result<MetadataLookupOutcome>.Success(Outcome(slow, "crossref"));
        await using MetadataRunService runs = new(lookup);

        Result<MetadataBatchRunStatus> started = await runs.StartBatchAsync([fast, slow]);
        await WaitForPredicateAsync(() =>
            runs.GetAsync(started.Value.TaskId).GetAwaiter().GetResult().Value.Completed >= 1);

        Result<MetadataBatchRunStatus> cancelled = await runs.CancelAsync(started.Value.TaskId);

        cancelled.IsSuccess.Should().BeTrue();
        MetadataBatchRunStatus status = cancelled.Value;
        status.State.Should().Be(MetadataRunState.Cancelled);
        status.Items.Should().Contain(item => item.ItemId == fast && item.IsSuccess);
        status.Items.Should().Contain(item => item.ItemId == slow && item.ErrorCode == "cancelled");
    }

    [Fact]
    public async Task Retry_reruns_only_failed_items_as_a_new_task()
    {
        ItemId succeeded = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        ItemId failed = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        StubLookup lookup = new();
        lookup.EnrichResults[succeeded] = Result<MetadataLookupOutcome>.Success(Outcome(succeeded, "crossref"));
        lookup.EnrichResults[failed] = Result<MetadataLookupOutcome>.Failure(
            MetadataLookupErrorCodes.NotFound, "missing");
        await using MetadataRunService runs = new(lookup);

        Result<MetadataBatchRunStatus> started = await runs.StartBatchAsync([succeeded, failed]);
        MetadataBatchRunStatus finished = await WaitForTerminalAsync(runs, started.Value.TaskId);
        finished.State.Should().Be(MetadataRunState.Completed);
        lookup.Enriched.Should().BeEquivalentTo([succeeded, failed]);

        Result<MetadataBatchRunStatus> retried = await runs.RetryAsync(started.Value.TaskId);

        retried.IsSuccess.Should().BeTrue();
        retried.Value.TaskId.Should().NotBe(started.Value.TaskId);
        retried.Value.Total.Should().Be(1);
        retried.Value.State.Should().Be(MetadataRunState.Running);
        await WaitForTerminalAsync(runs, retried.Value.TaskId);
        lookup.Enriched.Should().BeEquivalentTo([succeeded, failed, failed]);
    }

    [Fact]
    public async Task Unknown_task_returns_not_found()
    {
        await using MetadataRunService runs = new(new StubLookup());
        Result<MetadataBatchRunStatus> missing = await runs.GetAsync(Guid.NewGuid());
        missing.IsFailure.Should().BeTrue();
        missing.ErrorCode.Should().Be(AppErrorCodes.NotFound);
    }

    [Fact]
    public async Task Running_batch_does_not_mark_unprocessed_items_as_cancelled_or_failed()
    {
        ItemId fast = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        ItemId slow = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        StubLookup lookup = new();
        lookup.EnrichResults[fast] = Result<MetadataLookupOutcome>.Success(Outcome(fast, "crossref"));
        lookup.EnrichDelays[slow] = TimeSpan.FromSeconds(30);
        lookup.EnrichResults[slow] = Result<MetadataLookupOutcome>.Success(Outcome(slow, "crossref"));
        await using MetadataRunService runs = new(lookup);

        Result<MetadataBatchRunStatus> started = await runs.StartBatchAsync([fast, slow]);
        started.IsSuccess.Should().BeTrue();

        // While running and slow has not completed:
        MetadataBatchRunStatus runningStatus = (await runs.GetAsync(started.Value.TaskId)).Value;
        runningStatus.State.Should().Be(MetadataRunState.Running);
        MetadataBatchItemResult slowItem = runningStatus.Items.Single(i => i.ItemId == slow);
        slowItem.IsSuccess.Should().BeFalse();
        slowItem.ErrorCode.Should().BeNull("unprocessed items while running must not be marked as cancelled or failed");
        slowItem.ErrorMessage.Should().BeNull();
        runningStatus.Failed.Should().Be(0, "unprocessed items must not be counted as failed while running");

        // Now cancel and verify it settles as cancelled:
        Result<MetadataBatchRunStatus> cancelled = await runs.CancelAsync(started.Value.TaskId);
        cancelled.IsSuccess.Should().BeTrue();
        cancelled.Value.State.Should().Be(MetadataRunState.Cancelled);
        MetadataBatchItemResult cancelledSlow = cancelled.Value.Items.Single(i => i.ItemId == slow);
        cancelledSlow.ErrorCode.Should().Be("cancelled");
    }

    private static async Task<MetadataBatchRunStatus> WaitForTerminalAsync(MetadataRunService runs, Guid taskId)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        while (!timeout.IsCancellationRequested)
        {
            MetadataBatchRunStatus status = runs.GetAsync(taskId).GetAwaiter().GetResult().Value;
            if (status.State is not MetadataRunState.Running)
            {
                return status;
            }

            await Task.Delay(20);
        }

        throw new InvalidOperationException("The metadata task did not finish.");
    }

    private static async Task WaitForPredicateAsync(Func<bool> predicate)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        while (!timeout.IsCancellationRequested)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new InvalidOperationException("The predicate was not satisfied in time.");
    }

    private static MetadataLookupOutcome Outcome(ItemId itemId, string sourceId)
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
            DateTimeOffset.Parse("2026-10-22T00:00:00Z"),
            DateTimeOffset.Parse("2026-10-22T00:00:00Z"));
        MetadataCandidate candidate = new(sourceId, "A Study");
        return new MetadataLookupOutcome(item, candidate,
            [new MetadataLookupAttempt(sourceId, null, null)], []);
    }

    private sealed class StubLookup : IMetadataLookupService
    {
        public readonly Dictionary<ItemId, Result<MetadataLookupOutcome>> EnrichResults = new();
        public readonly Dictionary<ItemId, TimeSpan> EnrichDelays = new();
        public readonly System.Collections.Concurrent.ConcurrentQueue<ItemId> Enriched = new();

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
            if (EnrichDelays.TryGetValue(itemId, out TimeSpan delay))
            {
                return Task.Run(async () =>
                {
                    await Task.Delay(delay, cancellationToken);
                    return EnrichResults[itemId];
                }, cancellationToken);
            }

            return Task.FromResult(EnrichResults.TryGetValue(itemId, out Result<MetadataLookupOutcome>? result)
                ? result
                : Result<MetadataLookupOutcome>.Failure(MetadataLookupErrorCodes.NotFound, "missing"));
        }
    }
}
