using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Translation;
using Patchouli.Infrastructure.Translation;

namespace Patchouli.Tests;

/// <summary>
/// Issue #20: contract tests for <see cref="ITranslationBatcher"/>. The batcher groups segments without
/// splitting, dropping, duplicating or reordering them, and honours the size, character and token budgets.
/// </summary>
public sealed class TranslationBatcherTests
{
    private static readonly DefaultTranslationBatcher Batcher = new();

    [Fact]
    public void Batch_size_limit_produces_consecutive_gap_free_batches_without_reordering()
    {
        TranslationSegment[] segments = [.. Enumerable.Range(0, 5).Select(index => Segment($"source {index}"))];

        IReadOnlyList<TranslationBatch> batches = Batcher.CreateBatches(
            segments,
            new TranslationSettings { BatchSize = 2, MaxBatchCharacters = 1000 });

        batches.Should().HaveCount(3);
        batches.Select(batch => batch.Index).Should().Equal(0, 1, 2);
        batches.Select(batch => batch.Segments.Count).Should().Equal(2, 2, 1);
        batches.Should().OnlyContain(batch => batch.Segments.Count <= 2);
        batches.SelectMany(batch => batch.SegmentIds).Should().Equal(segments.Select(segment => segment.SegmentId));
        batches.SelectMany(batch => batch.Segments).Should().Equal(segments);
    }

    [Fact]
    public void Character_budget_starts_a_new_batch_before_it_would_be_exceeded()
    {
        TranslationSegment[] segments =
        [
            Segment("aaaaaa"),
            Segment("bbbbbb"),
            Segment("cccccc")
        ];

        IReadOnlyList<TranslationBatch> batches = Batcher.CreateBatches(
            segments,
            new TranslationSettings { BatchSize = 10, MaxBatchCharacters = 12 });

        batches.Should().HaveCount(2);
        batches[0].Segments.Select(segment => segment.Text).Should().Equal("aaaaaa", "bbbbbb");
        batches[1].Segments.Select(segment => segment.Text).Should().Equal("cccccc");
        batches.Should().OnlyContain(batch => batch.Segments.Sum(segment => segment.Text.Length) <= 12);
        batches.SelectMany(batch => batch.SegmentIds).Should().Equal(segments.Select(segment => segment.SegmentId));
    }

    [Fact]
    public void Token_budget_is_respected_when_it_is_set()
    {
        // Eight characters estimate as two tokens, so a three-token budget admits one segment but not two.
        TranslationSegment[] segments =
        [
            Segment("aaaaaaaa"),
            Segment("bbbbbbbb"),
            Segment("cccccccc")
        ];

        IReadOnlyList<TranslationBatch> batches = Batcher.CreateBatches(
            segments,
            new TranslationSettings { BatchSize = 10, MaxBatchCharacters = 1000, MaxBatchTokens = 3 });

        batches.Should().HaveCount(3);
        batches.Should().OnlyContain(batch =>
            batch.Segments.Sum(segment => DefaultTranslationBatcher.EstimateTokens(segment.Text.Length)) <= 3);
        batches.Select(batch => batch.Segments.Single().Text)
            .Should().Equal("aaaaaaaa", "bbbbbbbb", "cccccccc");
    }

    [Fact]
    public void A_single_segment_over_the_character_budget_keeps_its_own_batch_and_is_never_split()
    {
        TranslationSegment shortFirst = Segment("short one");
        TranslationSegment oversized = Segment(new string('x', 30));
        TranslationSegment shortSecond = Segment("short two");

        IReadOnlyList<TranslationBatch> batches = Batcher.CreateBatches(
            [shortFirst, oversized, shortSecond],
            new TranslationSettings { BatchSize = 10, MaxBatchCharacters = 10 });

        batches.Should().HaveCount(3);
        batches[1].Segments.Should().ContainSingle();
        batches[1].Segments[0].Should().BeSameAs(oversized);
        batches.SelectMany(batch => batch.SegmentIds).Should().Equal(
            shortFirst.SegmentId,
            oversized.SegmentId,
            shortSecond.SegmentId);
    }

    [Fact]
    public void A_single_segment_over_the_token_budget_keeps_its_own_batch()
    {
        TranslationSegment smallFirst = Segment("aaaa");
        TranslationSegment oversized = Segment(new string('x', 20));
        TranslationSegment smallSecond = Segment("bbbb");

        IReadOnlyList<TranslationBatch> batches = Batcher.CreateBatches(
            [smallFirst, oversized, smallSecond],
            new TranslationSettings { BatchSize = 10, MaxBatchCharacters = 1000, MaxBatchTokens = 2 });

        batches.Should().HaveCount(3);
        batches[1].Segments.Should().ContainSingle();
        batches[1].Segments[0].Should().BeSameAs(oversized);
        batches.SelectMany(batch => batch.SegmentIds).Should().Equal(
            smallFirst.SegmentId,
            oversized.SegmentId,
            smallSecond.SegmentId);
    }

    [Fact]
    public void Empty_input_produces_no_batches()
    {
        Batcher.CreateBatches([], new TranslationSettings()).Should().BeEmpty();
    }

    [Fact]
    public void The_page_context_reference_is_copied_to_every_batch()
    {
        TranslationBatchContext context = new(DocumentInstanceId.New(), PageId.New(), 4);
        TranslationSegment[] segments = [Segment("a"), Segment("b"), Segment("c")];

        IReadOnlyList<TranslationBatch> batches = Batcher.CreateBatches(
            segments,
            new TranslationSettings { BatchSize = 1 },
            context);

        batches.Should().HaveCount(3);
        batches.Should().OnlyContain(batch => ReferenceEquals(batch.Context, context));
    }

    private static TranslationSegment Segment(string text)
    {
        DocumentBoxId boxId = DocumentBoxId.New();
        return new TranslationSegment(TranslationSegmentIds.Create(boxId, 1), boxId, 1, text);
    }
}
