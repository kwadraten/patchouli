using FluentAssertions;
using Patchouli.Infrastructure.Ocr.NdlLite;

namespace Patchouli.Tests;

public sealed class NdlLiteClassNamesTests
{
    private const string OfficialYaml = """
                                        # Classes
                                        names:
                                          0: text_block
                                          1: line_main
                                          2: line_caption
                                          3: line_ad
                                          4: line_note
                                          5: line_note_tochu
                                          6: block_fig
                                          7: block_ad
                                          8: block_pillar
                                          9: block_folio
                                          10: block_rubi
                                          11: block_chart
                                          12: block_eqn
                                          13: block_cfm
                                          14: block_eng
                                          15: block_table
                                          16: line_title
                                        """;

    [Fact]
    public void Parse_returns_names_ordered_by_class_id()
    {
        IReadOnlyList<string> names = NdlLiteClassNames.Parse(OfficialYaml);

        names.Should().HaveCount(17);
        names[NdlLiteClassNames.TextBlockIndex].Should().Be("text_block");
        names[NdlLiteClassNames.LineMainIndex].Should().Be("line_main");
        names[NdlLiteClassNames.BlockAdIndex].Should().Be("block_ad");
        names[NdlLiteClassNames.BlockTableIndex].Should().Be("block_table");
        names[16].Should().Be("line_title");
    }

    [Fact]
    public void Parse_ignores_other_yaml_sections()
    {
        string yaml = """
                      other:
                        99: not_a_class
                      names:
                        0: text_block
                        1: line_main
                      """;

        IReadOnlyList<string> names = NdlLiteClassNames.Parse(yaml);

        names.Should().Equal("text_block", "line_main");
    }

    [Fact]
    public void Parse_throws_when_ids_have_gaps()
    {
        string yaml = """
                      names:
                        0: text_block
                        2: line_main
                      """;

        Action parse = () => NdlLiteClassNames.Parse(yaml);

        parse.Should().Throw<InvalidOperationException>().WithMessage("*id 1*");
    }

    [Fact]
    public void IsTextLine_matches_only_line_classes()
    {
        IReadOnlyList<string> names = NdlLiteClassNames.Parse(OfficialYaml);

        NdlLiteClassNames.IsTextLine(names, 0).Should().BeFalse("text_block is a block");
        NdlLiteClassNames.IsTextLine(names, 1).Should().BeTrue();
        NdlLiteClassNames.IsTextLine(names, 5).Should().BeTrue();
        NdlLiteClassNames.IsTextLine(names, 6).Should().BeFalse("block_fig is a block");
        NdlLiteClassNames.IsTextLine(names, 16).Should().BeTrue();
        NdlLiteClassNames.IsTextLine(names, -1).Should().BeFalse();
        NdlLiteClassNames.IsTextLine(names, 99).Should().BeFalse();
    }

    [Fact]
    public void Block_classifiers_match_the_official_names()
    {
        IReadOnlyList<string> names = NdlLiteClassNames.Parse(OfficialYaml);

        NdlLiteClassNames.IsTextBlock(names, 0).Should().BeTrue();
        NdlLiteClassNames.IsAdBlock(names, 7).Should().BeTrue();
        NdlLiteClassNames.IsTableBlock(names, 15).Should().BeTrue();
        NdlLiteClassNames.IsTextBlock(names, 7).Should().BeFalse();
    }
}
