using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Ids;
using Patchouli.UI.ViewModels.Editor;

namespace Patchouli.Tests;

public sealed class CreatorItemViewModelTests
{
    [Fact]
    public void Entering_personal_name_auto_populates_parts_and_keeps_manual_corrections()
    {
        CreatorItemViewModel creator = new(_ => { });

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
        CreatorItemViewModel creator = new(_ => { }) { IsLiteral = true };

        creator.Name = "Royal Society";

        creator.Family.Should().BeEmpty();
        creator.Given.Should().BeEmpty();
        creator.Literal.Should().Be("Royal Society");
    }

    [Fact]
    public void Details_are_collapsed_by_default_and_toggle_via_command()
    {
        CreatorItemViewModel creator = new(_ => { });

        creator.IsExpanded.Should().BeFalse();
        creator.ToggleDetailsCommand.Execute(null);
        creator.IsExpanded.Should().BeTrue();
        creator.ToggleDetailsCommand.Execute(null);
        creator.IsExpanded.Should().BeFalse();
    }

    [Fact]
    public void Suffix_and_particles_round_trip_through_load_and_edit()
    {
        CreatorItemViewModel creator = new(_ => { });

        creator.LoadFrom(new ItemCreator(
            "creator-1",
            ItemId.New(),
            ItemCreatorRoles.Author,
            "Lovelace",
            "Ada",
            null,
            "III",
            "van",
            0,
            DateTimeOffset.UtcNow));

        creator.Suffix.Should().Be("III");
        creator.Particles.Should().Be("van");

        creator.Suffix = "Jr.";
        creator.Particles = "de";
        creator.Suffix.Should().Be("Jr.");
        creator.Particles.Should().Be("de");
    }
}
