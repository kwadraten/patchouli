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
}
