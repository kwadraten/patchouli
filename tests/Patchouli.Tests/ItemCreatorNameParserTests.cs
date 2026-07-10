using FluentAssertions;
using Patchouli.Core.Bibliography;

namespace Patchouli.Tests;

public sealed class ItemCreatorNameParserTests
{
    [Fact]
    public void Parse_splits_western_full_name_into_given_and_family()
    {
        var result = ItemCreatorNameParser.Parse("Ada Lovelace");

        result.Family.Should().Be("Lovelace");
        result.Given.Should().Be("Ada");
        result.Literal.Should().BeNull();
    }

    [Fact]
    public void Parse_honors_family_given_comma_order()
    {
        var result = ItemCreatorNameParser.Parse("Lovelace, Ada");

        result.Family.Should().Be("Lovelace");
        result.Given.Should().Be("Ada");
        result.Literal.Should().BeNull();
    }

    [Fact]
    public void Parse_splits_common_chinese_compound_surname()
    {
        var result = ItemCreatorNameParser.Parse("欧阳娜娜");

        result.Family.Should().Be("欧阳");
        result.Given.Should().Be("娜娜");
        result.Literal.Should().BeNull();
    }

    [Fact]
    public void Parse_keeps_organisation_as_literal_when_requested()
    {
        var result = ItemCreatorNameParser.Parse("Royal Society", ItemCreatorNameMode.Literal);

        result.Family.Should().BeNull();
        result.Given.Should().BeNull();
        result.Literal.Should().Be("Royal Society");
    }
}
