using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Results;
using Patchouli.UI.ViewModels.Editor;
using CslItemTypeProfileService = Patchouli.Infrastructure.Bibliography.CslItemTypeProfileService;

namespace Patchouli.Tests;

public sealed class ItemEditorProfileFieldTests
{
    private readonly ICslItemTypeProfileService _profiles = new CslItemTypeProfileService();

    [Theory]
    [InlineData("article-journal", "期刊名")]
    [InlineData("chapter", "文献出处")]
    public async Task Profile_drives_container_title_label_in_editor(string itemType, string expectedLabel)
    {
        Result<CslItemTypeProfile> profile = await _profiles.GetProfileAsync(itemType);
        IReadOnlyList<ItemFieldDefinition> fields =
            UI.ViewModels.Editor.CslItemTypeProfileService.GetProfile(profile.Value);

        fields.Single(field => field.Key == "PublicationTitle").Label.Should().Be(expectedLabel);
    }

    [Fact]
    public async Task Profile_keeps_common_editor_labels_localized()
    {
        Result<CslItemTypeProfile> profile = await _profiles.GetProfileAsync("book");
        IReadOnlyList<ItemFieldDefinition> fields =
            UI.ViewModels.Editor.CslItemTypeProfileService.GetProfile(profile.Value);

        fields.Single(field => field.Key == "Title").Label.Should().Be("标题");
        fields.Single(field => field.Key == "Creators").Label.Should().Be("作者/贡献者");
    }
}
