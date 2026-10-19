using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Core.Translation;
using Patchouli.Infrastructure.Translation;

namespace Patchouli.Tests;

/// <summary>
/// Issue #20: contract tests for <see cref="ITranslationResultProcessor"/>. Results are aligned strictly by
/// segment id, never by the order in which a provider returned them.
/// </summary>
public sealed class TranslationResultProcessorTests
{
    private static readonly DefaultTranslationResultProcessor Processor = new();

    [Fact]
    public void Merge_matches_by_id_when_results_and_translations_are_out_of_order()
    {
        DocumentBoxId firstBox = DocumentBoxId.New();
        DocumentBoxId secondBox = DocumentBoxId.New();
        TranslationSegmentTranslation first = Translation(firstBox, 1, "uno");
        TranslationSegmentTranslation second = Translation(secondBox, 1, "dos");

        Result<IReadOnlyDictionary<DocumentBoxId, string>> merged = Processor.Merge(
        [
            BatchResult(1, [second.SegmentId], second),
            BatchResult(0, [first.SegmentId], first)
        ]);

        merged.IsSuccess.Should().BeTrue(merged.ErrorMessage);
        merged.Value.Should().HaveCount(2);
        merged.Value[firstBox].Should().Be("uno");
        merged.Value[secondBox].Should().Be("dos");
    }

    [Fact]
    public void Merge_orders_the_parts_of_one_box_by_part_index_before_concatenating()
    {
        DocumentBoxId boxId = DocumentBoxId.New();
        TranslationSegmentTranslation partOne = Translation(boxId, 1, "A");
        TranslationSegmentTranslation partTwo = Translation(boxId, 2, "B");
        TranslationSegmentTranslation partThree = Translation(boxId, 3, "C");

        Result<IReadOnlyDictionary<DocumentBoxId, string>> merged = Processor.Merge(
        [
            BatchResult(
                0,
                [partOne.SegmentId, partTwo.SegmentId, partThree.SegmentId],
                partThree,
                partOne,
                partTwo)
        ]);

        merged.IsSuccess.Should().BeTrue(merged.ErrorMessage);
        merged.Value.Should().ContainSingle();
        merged.Value[boxId].Should().Be("ABC");
    }

    [Fact]
    public void Missing_ids_fail_with_batch_mismatch_details()
    {
        DocumentBoxId boxId = DocumentBoxId.New();
        TranslationSegmentTranslation partOne = Translation(boxId, 1, "A");
        TranslationSegmentTranslation partTwo = Translation(boxId, 2, "B");

        Result<IReadOnlyDictionary<DocumentBoxId, string>> merged = Processor.Merge(
            [BatchResult(0, [partOne.SegmentId, partTwo.SegmentId], partOne)]);

        merged.IsFailure.Should().BeTrue();
        merged.ErrorCode.Should().Be(TranslationErrorCodes.BatchMismatch);
        TranslationBatchMismatchDetails details = merged.Details
            .Should().BeOfType<TranslationBatchMismatchDetails>().Subject;
        details.MissingSegmentIds.Should().Equal(partTwo.SegmentId);
        details.DuplicateSegmentIds.Should().BeEmpty();
        details.UnknownSegmentIds.Should().BeEmpty();
    }

    [Fact]
    public void Duplicate_ids_fail_with_batch_mismatch_details()
    {
        DocumentBoxId boxId = DocumentBoxId.New();
        TranslationSegmentTranslation translation = Translation(boxId, 1, "A");

        Result<IReadOnlyDictionary<DocumentBoxId, string>> merged = Processor.Merge(
            [BatchResult(0, [translation.SegmentId], translation, translation)]);

        merged.IsFailure.Should().BeTrue();
        merged.ErrorCode.Should().Be(TranslationErrorCodes.BatchMismatch);
        TranslationBatchMismatchDetails details = merged.Details
            .Should().BeOfType<TranslationBatchMismatchDetails>().Subject;
        details.DuplicateSegmentIds.Should().Equal(translation.SegmentId);
        details.MissingSegmentIds.Should().BeEmpty();
        details.UnknownSegmentIds.Should().BeEmpty();
    }

    [Fact]
    public void Unknown_ids_fail_with_batch_mismatch_details()
    {
        DocumentBoxId boxId = DocumentBoxId.New();
        TranslationSegmentTranslation expected = Translation(boxId, 1, "A");
        TranslationSegmentTranslation stranger = Translation(DocumentBoxId.New(), 1, "X");

        Result<IReadOnlyDictionary<DocumentBoxId, string>> merged = Processor.Merge(
            [BatchResult(0, [expected.SegmentId], expected, stranger)]);

        merged.IsFailure.Should().BeTrue();
        merged.ErrorCode.Should().Be(TranslationErrorCodes.BatchMismatch);
        TranslationBatchMismatchDetails details = merged.Details
            .Should().BeOfType<TranslationBatchMismatchDetails>().Subject;
        details.UnknownSegmentIds.Should().Equal(stranger.SegmentId);
        details.MissingSegmentIds.Should().BeEmpty();
        details.DuplicateSegmentIds.Should().BeEmpty();
    }

    [Fact]
    public void A_box_or_part_that_disagrees_with_the_segment_id_is_rejected()
    {
        DocumentBoxId boxId = DocumentBoxId.New();
        string segmentId = TranslationSegmentIds.Create(boxId, 1);
        TranslationSegmentTranslation wrongBox = new(segmentId, DocumentBoxId.New(), 1, "A");
        TranslationSegmentTranslation wrongPart = new(segmentId, boxId, 2, "A");

        foreach (TranslationSegmentTranslation inconsistent in new[] { wrongBox, wrongPart })
        {
            Result<IReadOnlyDictionary<DocumentBoxId, string>> merged = Processor.Merge(
                [BatchResult(0, [segmentId], inconsistent)]);

            merged.IsFailure.Should().BeTrue();
            merged.ErrorCode.Should().Be(TranslationErrorCodes.BatchMismatch);
            merged.ErrorMessage.Should().Contain(segmentId);
        }
    }

    [Fact]
    public void Merging_an_empty_result_list_produces_an_empty_dictionary()
    {
        Result<IReadOnlyDictionary<DocumentBoxId, string>> merged = Processor.Merge([]);

        merged.IsSuccess.Should().BeTrue(merged.ErrorMessage);
        merged.Value.Should().BeEmpty();
    }

    private static TranslationSegmentTranslation Translation(DocumentBoxId boxId, int partIndex, string text)
    {
        return new TranslationSegmentTranslation(
            TranslationSegmentIds.Create(boxId, partIndex),
            boxId,
            partIndex,
            text);
    }

    private static TranslationBatchResult BatchResult(
        int index,
        IReadOnlyList<string> requestedSegmentIds,
        params TranslationSegmentTranslation[] translations)
    {
        return new TranslationBatchResult(index, requestedSegmentIds, translations);
    }
}
