using FluentAssertions;

namespace Patchouli.Tests;

public sealed class TerminalCjkRegressionTests
{
    [Theory]
    [InlineData("甲乙丙", "甲乙丙", 5, "  ", "甲乙")]
    [InlineData("\u001b]66;w=2;甲\u001b\\\u001b]66;w=2;乙\u001b\\\u001b]66;w=2;丙\u001b\\", "甲乙丙", 5, "  ", "甲乙")]
    [InlineData("\u001b]66;w=1;A\u001b\\\u001b]66;w=1;B\u001b\\\u001b]66;w=1;C\u001b\\", "ABC", 3, " ", "AB")]
    public void Overwriting_wide_glyphs_leaves_earlier_cells_intact(string input, string initialExpected, int column,
        string replacement, string expected)
    {
        using XTerm.Terminal terminal = new(new XTerm.Options.TerminalOptions { Cols = 40, Rows = 4 });

        terminal.Write(input);
        terminal.GetVisibleLines()[0].TrimEnd().Should().Be(initialExpected);

        terminal.Write($"\u001b[1;{column}H{replacement}");
        terminal.GetVisibleLines()[0].TrimEnd().Should().Be(expected);
    }
}
