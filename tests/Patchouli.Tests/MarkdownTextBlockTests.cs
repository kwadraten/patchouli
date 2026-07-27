using Avalonia.Controls.Documents;
using Avalonia.Media;
using FluentAssertions;
using Patchouli.UI.Controls;

namespace Patchouli.Tests;

public sealed class MarkdownTextBlockTests
{
    [Fact]
    public void MarkdownTextBlock_renders_markdig_superscript_from_ast()
    {
        MarkdownTextBlock textBlock = new() { Markdown = "A source note^1^." };

        Run[] runs = (textBlock.Inlines?.OfType<Run>() ?? []).ToArray();
        Run superscript = runs.Single(run => run.Text == "1");
        superscript.BaselineAlignment.Should().Be(BaselineAlignment.Superscript);
        runs.Select(run => run.Text).Should().ContainInOrder("A source note", "1", ".");
    }

    [Fact]
    public void MarkdownTextBlock_renders_strong_emphasis_and_strikethrough()
    {
        MarkdownTextBlock textBlock = new() { Markdown = "**bold** *italic* ~~gone~~" };

        Run[] runs = (textBlock.Inlines?.OfType<Run>() ?? []).ToArray();
        runs.Single(run => run.Text == "bold").FontWeight.Should().Be(FontWeight.SemiBold);
        runs.Single(run => run.Text == "italic").FontStyle.Should().Be(FontStyle.Italic);
        runs.Single(run => run.Text == "gone").TextDecorations.Should().Equal(TextDecorations.Strikethrough);
    }

    [Fact]
    public void MarkdownTextBlock_renders_inline_code_with_monospace_and_background()
    {
        MarkdownTextBlock textBlock = new() { Markdown = "use `dotnet build` now" };

        Run code = (textBlock.Inlines?.OfType<Run>() ?? []).Single(run => run.Text == "dotnet build");
        code.FontFamily.Name.Should().Contain("Consolas");
        code.Background.Should().NotBeNull();
    }

    [Fact]
    public void MarkdownTextBlock_renders_link_text_underlined()
    {
        MarkdownTextBlock textBlock = new() { Markdown = "see [the docs](https://example.com)" };

        Run link = (textBlock.Inlines?.OfType<Run>() ?? []).Single(run => run.Text == "the docs");
        link.TextDecorations.Should().Equal(TextDecorations.Underline);
        link.Foreground.Should().NotBeNull();
    }

    [Fact]
    public void MarkdownTextBlock_applies_heading_level_font_size()
    {
        MarkdownTextBlock textBlock = new() { Markdown = "## Section" };

        textBlock.FontWeight.Should().Be(FontWeight.SemiBold);
        textBlock.FontSize.Should().Be(18);
        (textBlock.Inlines?.OfType<Run>() ?? []).Select(run => run.Text).Should().Contain("Section");
    }

    [Fact]
    public void MarkdownTextBlock_renders_gfm_table_as_monospace_pipe_lines()
    {
        MarkdownTextBlock textBlock = new() { Markdown = "| a | b |\n|---|---|\n| 1 | 2 |" };

        textBlock.FontFamily.Name.Should().Contain("Consolas");
        string[] lines = (textBlock.Inlines?.OfType<Run>() ?? []).Select(run => run.Text ?? string.Empty).ToArray();
        lines.Should().Contain("| a | b |");
        lines.Should().Contain("| 1 | 2 |");
    }
}
