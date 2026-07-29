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
            CslItemTypeDisplayNames.Names.Keys);
    }

    [Fact]
    public async Task Profile_display_names_come_from_the_shared_display_name_table()
    {
        Result<IReadOnlyList<CslItemTypeProfile>> profiles = await _service.ListProfilesAsync();

        profiles.IsSuccess.Should().BeTrue();
        foreach (CslItemTypeProfile profile in profiles.Value)
        {
            profile.DisplayName.Should().Be(CslItemTypeDisplayNames.For(profile.ItemType));
        }
    }

    [Fact]
    public async Task General_profile_is_present_but_not_renderable_for_csl()
    {
        Result<CslItemTypeProfile> profile = await _service.GetProfileAsync("general");

        profile.IsSuccess.Should().BeTrue();
        profile.Value.IsRenderableInCsl.Should().BeFalse();
        profile.Value.Description.Should().Contain("catch-all");
    }

    [Fact]
    public async Task Every_concrete_type_recommends_the_url_identifier()
    {
        Result<IReadOnlyList<CslItemTypeProfile>> profiles = await _service.ListProfilesAsync();

        profiles.IsSuccess.Should().BeTrue();
        foreach (CslItemTypeProfile profile in profiles.Value.Where(profile => profile.ItemType != "general"))
        {
            profile.IdentifierSchemes.Should().Contain(BuiltInIdentifierSchemes.URL,
                $"type '{profile.ItemType}' should recommend a URL");
        }
    }
}
