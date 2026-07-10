using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Infrastructure.Bibliography;

namespace Patchouli.Tests;

public sealed class ItemEditorProfileFieldTests
{
    private readonly ICslItemTypeProfileService _profiles = new CslItemTypeProfileService();

    [Theory]
    [InlineData("article-journal", "期刊名")]
    [InlineData("chapter", "文献出处")]
    public async Task Profile_drives_container_title_label_in_editor(string itemType, string expectedLabel)
    {
        var profile = await _profiles.GetProfileAsync(itemType);
        var fields = Patchouli.UI.ViewModels.Editor.CslItemTypeProfileService.GetProfile(profile.Value);

        fields.Single(field => field.Key == "PublicationTitle").Label.Should().Be(expectedLabel);
    }

    [Fact]
    public async Task Profile_keeps_common_editor_labels_localized()
    {
        var profile = await _profiles.GetProfileAsync("book");
        var fields = Patchouli.UI.ViewModels.Editor.CslItemTypeProfileService.GetProfile(profile.Value);

        fields.Single(field => field.Key == "Title").Label.Should().Be("标题");
        fields.Single(field => field.Key == "Creators").Label.Should().Be("作者/贡献者");
    }
}
