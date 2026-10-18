using Avalonia.Controls.Documents;
using Avalonia.Media;
using FluentAssertions;
using Patchouli.UI.Controls;
using Patchouli.Infrastructure.Documents;

namespace Patchouli.Tests;

[Collection("RetainedTabUI")]
public sealed class MarkdownTextBlockTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Native_text_runs_show_literal_option_in_direct_and_compiled_block_paths(bool useBlock)
    {
        return _headless.Session.Dispatch(() =>
        {
            const string source = "**Option &lt;option&gt;** &amp; &#x4E2D;";
            MarkdownTextBlock textBlock = useBlock
                ? new MarkdownTextBlock { Block = new MarkdigMarkdownEngine().Parse(source).Blocks.Single() }
                : new MarkdownTextBlock { Markdown = source };

            string text = string.Concat((textBlock.Inlines?.OfType<Run>() ?? []).Select(run => run.Text));
            text.Should().Be("Option <option> & 中");
            text.Should().NotContain("&lt;").And.NotContain("&gt;");
        }, CancellationToken.None);
    }

    private readonly AvaloniaSharedSessionFixture _headless;

    public MarkdownTextBlockTests(AvaloniaSharedSessionFixture headless)
    {
        _headless = headless;
    }

    [Fact]
    public Task MarkdownTextBlock_renders_markdig_superscript_from_ast()
    {
        return _headless.Session.Dispatch(() =>
        {
            MarkdownTextBlock textBlock = new() { Markdown = "A source note^1^." };

            Run[] runs = (textBlock.Inlines?.OfType<Run>() ?? []).ToArray();
            Run superscript = runs.Single(run => run.Text == "1");
            superscript.BaselineAlignment.Should().Be(BaselineAlignment.Superscript);
            runs.Select(run => run.Text).Should().ContainInOrder("A source note", "1", ".");
        }, CancellationToken.None);
    }

    [Fact]
    public Task MarkdownTextBlock_renders_strong_emphasis_and_strikethrough()
    {
        return _headless.Session.Dispatch(() =>
        {
            MarkdownTextBlock textBlock = new() { Markdown = "**bold** *italic* ~~gone~~" };

            Run[] runs = (textBlock.Inlines?.OfType<Run>() ?? []).ToArray();
            runs.Single(run => run.Text == "bold").FontWeight.Should().Be(FontWeight.SemiBold);
            runs.Single(run => run.Text == "italic").FontStyle.Should().Be(FontStyle.Italic);
            runs.Single(run => run.Text == "gone").TextDecorations.Should().Equal(TextDecorations.Strikethrough);
        }, CancellationToken.None);
    }

    [Fact]
    public Task MarkdownTextBlock_renders_inline_code_with_monospace_and_background()
    {
        return _headless.Session.Dispatch(() =>
        {
            MarkdownTextBlock textBlock = new() { Markdown = "use `dotnet build` now" };

            Run code = (textBlock.Inlines?.OfType<Run>() ?? []).Single(run => run.Text == "dotnet build");
            code.FontFamily.Name.Should().Contain("Consolas");
            code.Background.Should().NotBeNull();
        }, CancellationToken.None);
    }

    [Fact]
    public Task MarkdownTextBlock_renders_link_text_underlined()
    {
        return _headless.Session.Dispatch(() =>
        {
            MarkdownTextBlock textBlock = new() { Markdown = "see [the docs](https://example.com)" };

            Run link = (textBlock.Inlines?.OfType<Run>() ?? []).Single(run => run.Text == "the docs");
            link.TextDecorations.Should().Equal(TextDecorations.Underline);
            link.Foreground.Should().NotBeNull();
        }, CancellationToken.None);
    }

    [Fact]
    public Task MarkdownTextBlock_applies_heading_level_font_size()
    {
        return _headless.Session.Dispatch(() =>
        {
            MarkdownTextBlock textBlock = new() { Markdown = "## Section" };

            textBlock.FontWeight.Should().Be(FontWeight.SemiBold);
            textBlock.FontSize.Should().Be(18);
            (textBlock.Inlines?.OfType<Run>() ?? []).Select(run => run.Text).Should().Contain("Section");
        }, CancellationToken.None);
    }

    [Fact]
    public Task MarkdownTextBlock_renders_gfm_table_as_monospace_pipe_lines()
    {
        return _headless.Session.Dispatch(() =>
        {
            MarkdownTextBlock textBlock = new() { Markdown = "| a | b |\n|---|---|\n| 1 | 2 |" };

            textBlock.FontFamily.Name.Should().Contain("Consolas");
            string[] lines = (textBlock.Inlines?.OfType<Run>() ?? []).Select(run => run.Text ?? string.Empty).ToArray();
            lines.Should().Contain("| a | b |");
            lines.Should().Contain("| 1 | 2 |");
        }, CancellationToken.None);
    }
}
