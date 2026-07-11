using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography;

namespace Patchouli.Tests;

public sealed class CslItemTypeProfileTests
{
    private readonly ICslItemTypeProfileService _service = new CslItemTypeProfileService();

    [Fact]
    public async Task Required_builtin_profiles_exist()
    {
        Result<IReadOnlyList<CslItemTypeProfile>> profiles = await _service.ListProfilesAsync();

        profiles.IsSuccess.Should().BeTrue();
        profiles.Value.Select(profile => profile.ItemType).Should().BeEquivalentTo(
            "general",
            "book",
            "article-journal",
            "chapter",
            "thesis",
            "report",
            "webpage",
            "manuscript",
            "paper-conference",
            "patent",
            "standard");
    }

    [Fact]
    public async Task General_profile_is_present_but_not_renderable_for_csl()
    {
        Result<CslItemTypeProfile> profile = await _service.GetProfileAsync("general");

        profile.IsSuccess.Should().BeTrue();
        profile.Value.IsRenderableInCsl.Should().BeFalse();
        profile.Value.Description.Should().Contain("catch-all");
    }
}
