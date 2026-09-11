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
    public void Starts_the_fragment_with_a_one_based_page_anchor()
    {
        CompiledMarkdown compiled = new("本文", [], []);

        string html = BookReadingHtml.CompilePageHtml(compiled, 4, 5);

        html.Should().StartWith("<h2 data-page=\"5\">第 5 页 / 共 5 页</h2>");
        html.Should().Contain("<p>本文</p>");
    }

    [Fact]
    public void Emits_only_the_anchor_for_empty_markdown()
    {
        string html = BookReadingHtml.CompilePageHtml(new CompiledMarkdown("", [], []), 1, 4);

        html.Should().Be("<h2 data-page=\"2\">第 2 页 / 共 4 页</h2>\n");
    }

    [Fact]
    public void Emits_only_the_anchor_for_whitespace_markdown()
    {
        string html = BookReadingHtml.CompilePageHtml(new CompiledMarkdown("   \n\n  ", [], []), 2, 4);

        html.Should().Be("<h2 data-page=\"3\">第 3 页 / 共 4 页</h2>\n");
    }

    [Fact]
    public void Placeholder_keeps_the_page_anchor_and_explains_the_missing_text()
    {
        string html = BookReadingHtml.CompilePlaceholderHtml(2, 5);

        html.Should().Be("<h2 data-page=\"3\">第 3 页 / 共 5 页</h2>\n<p><i>本页尚未识别文字。</i></p>");
    }
}
