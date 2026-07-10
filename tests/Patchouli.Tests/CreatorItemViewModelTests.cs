using FluentAssertions;
using Patchouli.UI.ViewModels.Editor;

namespace Patchouli.Tests;

public sealed class CreatorItemViewModelTests
{
    [Fact]
    public void Entering_personal_name_auto_populates_parts_and_keeps_manual_corrections()
    {
        var creator = new CreatorItemViewModel(_ => { });

        creator.Name = "Ada Lovelace";
        creator.Family.Should().Be("Lovelace");
        creator.Given.Should().Be("Ada");
        creator.Literal.Should().BeEmpty();

        creator.Family = "Byron";
        creator.Given = "Augusta";

        creator.Family.Should().Be("Byron");
        creator.Given.Should().Be("Augusta");
    }

    [Fact]
    public void Selecting_literal_mode_preserves_an_organisation_name_without_parts()
    {
        var creator = new CreatorItemViewModel(_ => { }) { IsLiteral = true };

        creator.Name = "Royal Society";

        creator.Family.Should().BeEmpty();
        creator.Given.Should().BeEmpty();
        creator.Literal.Should().Be("Royal Society");
    }
}
