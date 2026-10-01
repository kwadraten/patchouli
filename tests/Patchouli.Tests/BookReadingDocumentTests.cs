using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Reading;
using Patchouli.UI.Reading;

namespace Patchouli.Tests;

/// <summary>The whole-book reading document assembly: in compare mode both columns pair blocks by
/// index, so pages without a translation (or with a block-count mismatch) must be padded to keep
/// every later page row-aligned instead of shifting up.</summary>
public sealed class BookReadingDocumentTests
{
    [Fact]
    public void Compare_fills_pages_without_translation_with_placeholders_keeping_later_pages_aligned()
    {
        SortedDictionary<int, ReadingScene> sources = new()
        {
            [0] = Scene(2),
            [1] = Scene(2),
            [2] = Scene(2)
        };
        Dictionary<int, ReadingScene?> translations = new()
        {
            [0] = Scene(2, "译"),
            // Page 1 has no translation at all (null scene, like the stream delivers).
            [2] = Scene(2, "译")
        };

        (ReadingScene source, ReadingScene? translation) =
            BookReadingDocument.Assemble(sources, translations, true);

        translation.Should().NotBeNull();
        source.Blocks.Should().HaveCount(6);
        translation!.Blocks.Should().HaveCount(6, "each page spans the same rows in both columns");

        // Page 1's rows are muted placeholders stamped with the owning page.
        translation.Blocks[2].Kind.Should().Be(ReadingBlock.UntranslatedKind);
        translation.Blocks[3].Kind.Should().Be(ReadingBlock.UntranslatedKind);
        translation.Blocks[2].PageIndex.Should().Be(1);
        translation.Blocks[3].PageIndex.Should().Be(1);

        // Page 2's content starts at the same row in both columns — no shift-up.
        source.Blocks[4].PageIndex.Should().Be(2);
        translation.Blocks[4].PageIndex.Should().Be(2);
        translation.Blocks[4].Kind.Should().NotBe(ReadingBlock.UntranslatedKind);
    }

    [Fact]
    public void Compare_placeholders_carry_the_source_block_box_linkage()
    {
        DocumentBoxId boxId = DocumentBoxId.New();
        SortedDictionary<int, ReadingScene> sources = new()
        {
            [0] = new ReadingScene([new ReadingBlock(boxId, "paragraph", 0, "原文")]),
            [1] = Scene(1)
        };
        Dictionary<int, ReadingScene?> translations = new()
        {
            [1] = Scene(1, "译")
        };

        (_, ReadingScene? translation) = BookReadingDocument.Assemble(sources, translations, true);

        translation.Should().NotBeNull();
        translation!.Blocks[0].Kind.Should().Be(ReadingBlock.UntranslatedKind);
        translation.Blocks[0].BoxId.Should().Be(boxId,
            "the placeholder keeps the source box linkage like a per-block untranslated marker");
    }

    [Fact]
    public void Compare_pads_the_shorter_side_when_block_counts_differ_within_a_page()
    {
        SortedDictionary<int, ReadingScene> sources = new()
        {
            [0] = Scene(3),
            [1] = Scene(1)
        };
        Dictionary<int, ReadingScene?> translations = new()
        {
            [0] = Scene(1, "译"),
            [1] = Scene(2, "译")
        };

        (ReadingScene source, ReadingScene? translation) =
            BookReadingDocument.Assemble(sources, translations, true);

        source.Blocks.Should().HaveCount(5);
        translation!.Blocks.Should().HaveCount(5);
        translation.Blocks[1].Kind.Should().Be(ReadingBlock.UntranslatedKind,
            "page 0's missing translation rows are placeholders");
        translation.Blocks[2].Kind.Should().Be(ReadingBlock.UntranslatedKind);
        source.Blocks[3].PageIndex.Should().Be(1, "page 1 starts at the same row in both columns");
        translation.Blocks[3].PageIndex.Should().Be(1);
        source.Blocks[4].Text.Should().BeEmpty("page 1's extra translation row pads the source side");
        source.Blocks[4].PageIndex.Should().Be(1);
    }

    [Fact]
    public void Outside_compare_the_source_passes_through_and_no_translation_scene_is_produced()
    {
        SortedDictionary<int, ReadingScene> sources = new()
        {
            [0] = Scene(2),
            [1] = Scene(1)
        };
        Dictionary<int, ReadingScene?> translations = new()
        {
            [0] = Scene(2, "译")
        };

        (ReadingScene source, ReadingScene? translation) =
            BookReadingDocument.Assemble(sources, translations, false);

        translation.Should().BeNull();
        source.Blocks.Should().HaveCount(3, "single-column reading never gets padding rows");
        source.Blocks.Select(block => block.PageIndex).Should().Equal(0, 0, 1);
    }

    private static ReadingScene Scene(int blockCount, string textPrefix = "正文")
    {
        List<ReadingBlock> blocks = new(blockCount);
        for (int i = 0; i < blockCount; i++)
        {
            blocks.Add(new ReadingBlock(DocumentBoxId.New(), "paragraph", 0, $"{textPrefix}{i}"));
        }

        return new ReadingScene(blocks);
    }
}
