using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Translation;
using Patchouli.Infrastructure.Translation;

namespace Patchouli.Tests;

/// <summary>
/// Issue #20: contract tests for <see cref="ITranslationSegmenter"/>. The segmenter is the only stage
/// allowed to divide a box, and the parts of one box must always reconstruct the source text exactly.
/// </summary>
public sealed class TranslationSegmenterTests
{
    [Fact]
    public void Segment_skips_non_translatable_and_blank_boxes_and_keeps_source_order()
    {
        DocumentBoxId firstId = DocumentBoxId.New();
        DocumentBoxId hiddenId = DocumentBoxId.New();
        DocumentBoxId blankId = DocumentBoxId.New();
        DocumentBoxId secondId = DocumentBoxId.New();

        IReadOnlyList<TranslationSegment> segments = new DefaultTranslationSegmenter().Segment(
            [
                new TranslationSourceBox(firstId, DocumentBoxType.Text, "first source"),
                new TranslationSourceBox(hiddenId, DocumentBoxType.Text, "not translatable", false),
                new TranslationSourceBox(blankId, DocumentBoxType.Text, " \r\n\t "),
                new TranslationSourceBox(secondId, DocumentBoxType.Title, "second source")
            ],
            new TranslationSettings());

        segments.Should().HaveCount(2);
        segments.Select(segment => segment.SegmentId).Should().Equal(
            TranslationSegmentIds.Create(firstId, 1),
            TranslationSegmentIds.Create(secondId, 1));
        segments.Select(segment => segment.Text).Should().Equal("first source", "second source");
        segments.Should().OnlyContain(segment => segment.PartIndex == 1);
        segments.Select(segment => segment.BoxId).Should().Equal(firstId, secondId);
    }

    [Fact]
    public void Oversized_html_table_remains_one_complete_segment_and_one_batch()
    {
        string html = "<table><tr><td>" + new string('a', 1500) + "</td></tr></table>";
        TranslationSettings settings = new(MaxBatchCharacters: 1000);
        IReadOnlyList<TranslationSegment> segments = new DefaultTranslationSegmenter().Segment(
            [new TranslationSourceBox(DocumentBoxId.New(), DocumentBoxType.Table, html)], settings);
        segments.Should().ContainSingle().Which.Text.Should().Be(html);
        new DefaultTranslationBatcher().CreateBatches(segments, settings).Should().ContainSingle()
            .Which.Segments.Single().Text.Should().Be(html);
    }

    [Fact]
    public void Oversized_text_is_split_into_bounded_ordered_parts_that_reconstruct_the_source()
    {
        const int limit = 10;
        string text = new('a', 25);
        DocumentBoxId boxId = DocumentBoxId.New();

        IReadOnlyList<TranslationSegment> segments = new DefaultTranslationSegmenter().Segment(
            [new TranslationSourceBox(boxId, DocumentBoxType.Text, text)],
            new TranslationSettings { MaxBatchCharacters = limit });

        segments.Should().HaveCount(3);
        segments.Select(segment => segment.PartIndex).Should().Equal(1, 2, 3);
        segments.Select(segment => segment.SegmentId).Should().Equal(
            TranslationSegmentIds.Create(boxId, 1),
            TranslationSegmentIds.Create(boxId, 2),
            TranslationSegmentIds.Create(boxId, 3));
        segments.Select(segment => segment.Text.Length).Should().Equal(10, 10, 5);
        segments.Should().OnlyContain(segment => segment.BoxId == boxId && segment.Text.Length <= limit);
        string.Concat(segments.Select(segment => segment.Text)).Should().Be(text);
    }

    [Fact]
    public void Splitting_at_a_natural_boundary_still_reconstructs_the_source()
    {
        const int limit = 20;
        string text = "First sentence. Second sentence. Third sentence.";
        DocumentBoxId boxId = DocumentBoxId.New();

        IReadOnlyList<TranslationSegment> segments = new DefaultTranslationSegmenter().Segment(
            [new TranslationSourceBox(boxId, DocumentBoxType.Text, text)],
            new TranslationSettings { MaxBatchCharacters = limit });

        segments.Should().HaveCountGreaterThan(1);
        segments.Should().OnlyContain(segment => segment.Text.Length <= limit);
        segments.Select(segment => segment.PartIndex).Should().Equal(
            Enumerable.Range(1, segments.Count));
        string.Concat(segments.Select(segment => segment.Text)).Should().Be(text);
    }

    [Fact]
    public void A_crlf_pair_is_never_split_across_two_parts()
    {
        const int limit = 8;
        string text = "abcdefg\r\n" + new string('x', 20);
        DocumentBoxId boxId = DocumentBoxId.New();

        IReadOnlyList<TranslationSegment> segments = new DefaultTranslationSegmenter().Segment(
            [new TranslationSourceBox(boxId, DocumentBoxType.Text, text)],
            new TranslationSettings { MaxBatchCharacters = limit });

        segments.Should().HaveCountGreaterThan(1);
        segments.Should().OnlyContain(segment => segment.Text.Length <= limit);
        string.Concat(segments.Select(segment => segment.Text)).Should().Be(text);
        for (int index = 0; index + 1 < segments.Count; index++)
        {
            bool splitsCrlf = segments[index].Text.EndsWith('\r') && segments[index + 1].Text.StartsWith('\n');
            splitsCrlf.Should().BeFalse(
                $"the boundary between part {index + 1} and part {index + 2} must not fall inside a CRLF pair");
        }
    }

    [Fact]
    public void A_surrogate_pair_is_never_split_across_two_parts()
    {
        const int limit = 5;
        string text = "abcd" + char.ConvertFromUtf32(0x1F600) + new string('e', 15);
        DocumentBoxId boxId = DocumentBoxId.New();

        IReadOnlyList<TranslationSegment> segments = new DefaultTranslationSegmenter().Segment(
            [new TranslationSourceBox(boxId, DocumentBoxType.Text, text)],
            new TranslationSettings { MaxBatchCharacters = limit });

        segments.Should().HaveCountGreaterThan(1);
        string.Concat(segments.Select(segment => segment.Text)).Should().Be(text);
        segments.Should().OnlyContain(segment => !HasLoneSurrogate(segment.Text));
    }

    private static bool HasLoneSurrogate(string value)
    {
        for (int index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    return true;
                }

                index++;
            }
            else if (char.IsLowSurrogate(value[index]))
            {
                return true;
            }
        }

        return false;
    }
}
