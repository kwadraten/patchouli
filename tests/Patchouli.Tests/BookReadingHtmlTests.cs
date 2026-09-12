using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.UI.Reading;

namespace Patchouli.Tests;

public sealed class BookReadingHtmlTests
{
    [Fact]
    public void Compiles_a_pipe_table_to_a_table_element()
    {
        CompiledMarkdown compiled = new(
            """
            | 列一 | 列二 |
            | --- | --- |
            | 甲 | 乙 |
            """,
            [],
            []);

        string html = BookReadingHtml.CompilePageHtml(compiled, 0, 3);

        html.Should().Contain("<table>");
        html.Should().Contain("<th>列一</th>");
        html.Should().Contain("<td>甲</td>");
    }

    [Fact]
    public void Passes_raw_complex_table_html_through_without_escaping()
    {
        CompiledMarkdown compiled = new(
            "<table><tr><td rowspan=\"2\">複雑</td></tr></table>",
            [],
            []);

        string html = BookReadingHtml.CompilePageHtml(compiled, 0, 1);

        html.Should().Contain("<table><tr><td rowspan=\"2\">複雑</td></tr></table>");
    }

    [Fact]
    public void Compiles_the_markdown_without_any_in_flow_page_anchor()
    {
        CompiledMarkdown compiled = new("本文", [], []);

        string html = BookReadingHtml.CompilePageHtml(compiled, 4, 5);

        html.Should().Be("<p>本文</p>\n");
        html.Should().NotContain("data-page",
            "page boundaries are shown as badges in the left rail, not as in-flow headings");
    }

    [Fact]
    public void Emits_an_empty_paragraph_for_empty_markdown_so_the_page_keeps_its_badge_line()
    {
        string html = BookReadingHtml.CompilePageHtml(new CompiledMarkdown("", [], []), 1, 4);

        html.Should().Be("<p></p>\n");
    }

    [Fact]
    public void Emits_an_empty_paragraph_for_whitespace_markdown()
    {
        string html = BookReadingHtml.CompilePageHtml(new CompiledMarkdown("   \n\n  ", [], []), 2, 4);

        html.Should().Be("<p></p>\n");
    }

    [Fact]
    public void Placeholder_explains_the_missing_text_without_a_page_anchor()
    {
        string html = BookReadingHtml.CompilePlaceholderHtml(2, 5);

        html.Should().Be("<p><i>本页尚未识别文字。</i></p>");
    }
}
