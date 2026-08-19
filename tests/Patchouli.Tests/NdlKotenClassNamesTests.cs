using FluentAssertions;
using Patchouli.Infrastructure.Ocr.NdlKoten;

namespace Patchouli.Tests;

public sealed class NdlKotenClassNamesTests
{
    [Fact]
    public void Parse_reads_names_mapping()
    {
        string yaml =
            "names:\n" +
            "  0: text_block\n" +
            "  1: line_main\n" +
            "  2: line_caption\n";

        IReadOnlyDictionary<int, string> names = NdlKotenClassNames.Parse(yaml);

        names.Should().Contain(0, "text_block")
            .And.Contain(1, "line_main")
            .And.Contain(2, "line_caption");
    }
}
