using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Infrastructure.Documents;

namespace Patchouli.Tests;

public sealed class ContinuationMarkdownProjectionTests
{
    private readonly MarkdigMarkdownEngine _markdown = new();

    [Fact]
    public void Explicit_nonadjacent_text_chain_is_joined_once_and_maps_each_physical_box()
    {
        DocumentBox first = Box(DocumentBoxType.Text, new TextBoxPayload("first"));
        DocumentBox unrelated = Box(DocumentBoxType.Text, new TextBoxPayload("unrelated"));
        DocumentBox middle = Box(DocumentBoxType.Text, new TextBoxPayload("middle"), first.BoxId);
        DocumentBox last = Box(DocumentBoxType.Text, new TextBoxPayload("last"), middle.BoxId);

        CompiledMarkdown result = Compile([first, unrelated, middle, last]);

        result.Markdown.Should().Be("first middle last\n\nunrelated");
        result.Markdown.IndexOf("first middle last", StringComparison.Ordinal).Should().BeLessThan(
            result.Markdown.IndexOf("unrelated", StringComparison.Ordinal));
        result.SourceMap.Select(map => map.BoxId).Should()
            .Contain([first.BoxId, unrelated.BoxId, middle.BoxId, last.BoxId]);
        result.SourceMap.Should().Contain(map =>
            map.BoxId == first.BoxId && result.Markdown.Substring(map.Start, map.Length) == "first");
        result.SourceMap.Should().Contain(map =>
            map.BoxId == last.BoxId && result.Markdown.Substring(map.Start, map.Length) == "last");
        MarkdownSourceMapEntry firstMap = result.SourceMap.Single(map => map.BoxId == first.BoxId);
        MarkdownSourceMapEntry middleMap = result.SourceMap.Single(map => map.BoxId == middle.BoxId);
        MarkdownSourceMapEntry lastMap = result.SourceMap.Single(map => map.BoxId == last.BoxId);
        firstMap.PreviewNodeStart.Should().Be(middleMap.PreviewNodeStart).And.Be(lastMap.PreviewNodeStart);
        result.SourceMap.Single(map => map.BoxId == unrelated.BoxId).PreviewNodeStart.Should()
            .NotBe(firstMap.PreviewNodeStart);
        result.Document!.Blocks.Should().HaveCount(2);
    }

    [Fact]
    public void Unlinked_content_stays_separate_and_legacy_empty_follower_does_not_repeat_source()
    {
        DocumentBox first = Box(DocumentBoxType.Text, new TextBoxPayload("source text"));
        DocumentBox empty = Box(DocumentBoxType.Text, new TextBoxPayload(""), first.BoxId,
            DocumentContinuationState.LegacyUnrecovered);
        DocumentBox independent = Box(DocumentBoxType.Text, new TextBoxPayload("other"));

        CompiledMarkdown result = Compile([first, empty, independent]);

        result.Markdown.Should().Be("source text\n\nother");
        result.Markdown.Split("source text").Should().HaveCount(2);
        result.Continuations.Should().ContainSingle().Which.Should()
            .Be(new MarkdownContinuation(empty.BoxId, first.BoxId));
        result.SourceMap.Should().Contain(map => map.BoxId == first.BoxId);
        result.SourceMap.Should().NotContain(map => map.BoxId == empty.BoxId);
    }

    [Fact]
    public void External_page_link_is_reported_without_importing_other_page_payload()
    {
        DocumentBox otherPageSource = Box(DocumentBoxType.Text, new TextBoxPayload("never copy"));
        DocumentBox local = Box(DocumentBoxType.Text, new TextBoxPayload("local only"), otherPageSource.BoxId);

        CompiledMarkdown result = Compile([local]);

        result.Markdown.Should().Be("local only");
        result.Markdown.Should().NotContain("never copy");
        result.Continuations.Should().ContainSingle().Which.FromBoxId.Should().Be(otherPageSource.BoxId);
    }

    [Fact]
    public void List_chain_preserves_list_structure_and_matching_gfm_tables_join_body_rows()
    {
        DocumentBox firstList = Box(DocumentBoxType.List, new ListBoxPayload("- one"));
        DocumentBox nextList = Box(DocumentBoxType.List, new ListBoxPayload("- two"), firstList.BoxId);
        CompiledMarkdown list = Compile([firstList, nextList]);
        list.Markdown.Should().Be("- one\n- two");
        list.Diagnostics.Should().BeEmpty();

        DocumentBox firstTable = Box(DocumentBoxType.Table,
            new TableBoxPayload("| A | B |\n|---|---|\n| 1 | 2 |"));
        DocumentBox nextTable = Box(DocumentBoxType.Table,
            new TableBoxPayload("| A | B |\n|---|---|\n| 3 | 4 |"), firstTable.BoxId);
        CompiledMarkdown table = Compile([firstTable, nextTable]);
        table.Markdown.Should().Be("| A | B |\n|---|---|\n| 1 | 2 |\n| 3 | 4 |");
        table.Diagnostics.Should().BeEmpty();
        table.SourceMap.Select(map => map.BoxId).Should().Contain([firstTable.BoxId, nextTable.BoxId]);
        table.Markdown.Substring(table.SourceMap.Single(map => map.BoxId == firstTable.BoxId).Start,
                table.SourceMap.Single(map => map.BoxId == firstTable.BoxId).Length)
            .Should().Be("| A | B |\n|---|---|\n| 1 | 2 |");
        table.Markdown.Substring(table.SourceMap.Single(map => map.BoxId == nextTable.BoxId).Start,
            table.SourceMap.Single(map => map.BoxId == nextTable.BoxId).Length).Should().Be("| 3 | 4 |");
    }

    [Fact]
    public void Incompatible_gfm_tables_remain_separate_with_diagnostic()
    {
        DocumentBox first = Box(DocumentBoxType.Table, new TableBoxPayload("| A | B |\n|---|---|\n| 1 | 2 |"));
        DocumentBox next = Box(DocumentBoxType.Table,
            new TableBoxPayload("| X |\n|---|\n| 3 |"), first.BoxId);

        CompiledMarkdown result = Compile([first, next]);

        result.Markdown.Should().Contain("| A | B |\n|---|---|\n| 1 | 2 |").And.Contain("| X |\n|---|\n| 3 |");
        result.Diagnostics.Should().ContainSingle(diagnostic => diagnostic.Code == "continuation_structure_preserved");
    }

    [Theory]
    [InlineData("| A | B |\n|:---|---:|\n| 3 | 4 |")]
    [InlineData("| A\\|X | B |\n|---|---|\n| 3 | 4 |")]
    public void Ambiguous_gfm_delimiters_or_escaped_pipes_are_not_joined(string followerTable)
    {
        DocumentBox first = Box(DocumentBoxType.Table,
            new TableBoxPayload("| A | B |\n|---|---|\n| 1 | 2 |"));
        DocumentBox follower = Box(DocumentBoxType.Table, new TableBoxPayload(followerTable), first.BoxId);

        CompiledMarkdown result = Compile([first, follower]);

        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "continuation_structure_preserved");
        result.SourceMap.Select(map => map.BoxId).Should().Contain([first.BoxId, follower.BoxId]);
    }

    [Theory]
    [InlineData("before\n\nafter")]
    [InlineData("# heading")]
    [InlineData("```text\ncode\n```")]
    [InlineData("- a\n- b")]
    public void Multiblock_or_structural_text_fragments_are_not_inline_joined(string source)
    {
        DocumentBox first = Box(DocumentBoxType.Text, new TextBoxPayload(source));
        DocumentBox next = Box(DocumentBoxType.Text, new TextBoxPayload("continued"), first.BoxId);

        CompiledMarkdown result = Compile([first, next]);

        result.Markdown.Should().Contain(source).And.Contain("continued");
        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "continuation_structure_preserved");
    }

    [Fact]
    public void Annotated_text_is_not_joined_into_the_annotation_structure()
    {
        DocumentBox first = Box(DocumentBoxType.Text, new TextBoxPayload("body")
        {
            Annotations = [new DocumentAnnotation("note", "note text")]
        });
        DocumentBox next = Box(DocumentBoxType.Text, new TextBoxPayload("next"), first.BoxId);

        CompiledMarkdown result = Compile([first, next]);

        result.Markdown.Should().Be("body\n\nnote text\n\nnext");
        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "continuation_structure_preserved");
    }

    [Fact]
    public void Branching_chain_preserves_every_physical_fragment_and_reports_diagnostic()
    {
        DocumentBox source = Box(DocumentBoxType.Text, new TextBoxPayload("source"));
        DocumentBox first = Box(DocumentBoxType.Text, new TextBoxPayload("branch one"), source.BoxId);
        DocumentBox second = Box(DocumentBoxType.Text, new TextBoxPayload("branch two"), source.BoxId);

        CompiledMarkdown result = Compile([source, first, second]);

        result.Markdown.Should().Contain("source").And.Contain("branch one").And.Contain("branch two");
        result.SourceMap.Select(map => map.BoxId).Should().Contain([source.BoxId, first.BoxId, second.BoxId]);
        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "continuation_structure_preserved");
    }

    [Fact]
    public void Follower_before_source_is_preserved_separately_with_diagnostic()
    {
        DocumentBox source = Box(DocumentBoxType.Text, new TextBoxPayload("source"));
        DocumentBox follower = Box(DocumentBoxType.Text, new TextBoxPayload("follower"), source.BoxId);

        CompiledMarkdown result = Compile([follower, source]);

        result.Markdown.Should().Be("follower\n\nsource");
        result.SourceMap.Select(map => map.BoxId).Should().Contain([source.BoxId, follower.BoxId]);
        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "continuation_structure_preserved");
    }

    [Fact]
    public void Cyclic_chain_is_preserved_separately_with_diagnostic()
    {
        DocumentBox first = Box(DocumentBoxType.Text, new TextBoxPayload("first"));
        DocumentBox second = Box(DocumentBoxType.Text, new TextBoxPayload("second"), first.BoxId);
        first = first with { ContinuesFromBoxId = second.BoxId };

        CompiledMarkdown result = Compile([first, second]);

        result.Markdown.Should().Be("first\n\nsecond");
        result.SourceMap.Select(map => map.BoxId).Should().Contain([first.BoxId, second.BoxId]);
        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "continuation_structure_preserved");
    }

    [Fact]
    public void Visible_follower_of_suppressed_source_is_projected_on_its_own()
    {
        DocumentBox source = Box(DocumentBoxType.Text, new TextBoxPayload("hidden")) with { Suppressed = true };
        DocumentBox follower = Box(DocumentBoxType.Text, new TextBoxPayload("visible"), source.BoxId);

        CompiledMarkdown result = Compile([source, follower]);

        result.Markdown.Should().Be("visible");
        result.Markdown.Should().NotContain("hidden");
        result.SourceMap.Should().ContainSingle().Which.BoxId.Should().Be(follower.BoxId);
    }

    [Fact]
    public void Complex_html_table_chain_is_preserved_with_diagnostic()
    {
        DocumentBox first = Box(DocumentBoxType.Table, new TableBoxPayload("<table><tr><td>a</td></tr></table>"));
        DocumentBox next = Box(DocumentBoxType.Table,
            new TableBoxPayload("<table><tr><td>b</td></tr></table>"), first.BoxId);

        CompiledMarkdown result = Compile([first, next]);

        result.Markdown.Should().Contain("<table>").And.Contain("</table>");
        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "continuation_structure_preserved");
        result.SourceMap.Select(map => map.BoxId).Should().Contain([first.BoxId, next.BoxId]);
    }

    private CompiledMarkdown Compile(IReadOnlyList<DocumentBox> boxes)
    {
        DocumentBox[] ordered = boxes.Select((box, index) => box with
        {
            NextSiblingBoxId = index + 1 < boxes.Count ? boxes[index + 1].BoxId : null
        }).ToArray();
        return DocumentMarkdownRenderer.Render(ordered, _markdown, false, false, null);
    }

    private static DocumentBox Box(string type, DocumentBoxPayload payload, DocumentBoxId? continuesFrom = null,
        string? continuationState = null)
    {
        return new DocumentBox(
            DocumentTreeRevisionId.New(), DocumentBoxId.New(), DocumentInstanceId.New(), PageId.New(), null, null,
            type, null, new NormalizedBBox(0, 0, 1, 1), payload, null, null, null, false, continuesFrom,
            continuationState);
    }
}
