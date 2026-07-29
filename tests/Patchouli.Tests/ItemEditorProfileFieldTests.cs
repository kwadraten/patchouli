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
    [InlineData("paper-conference", "会议录名")]
    public async Task Profile_drives_container_title_label_in_editor(string itemType, string expectedLabel)
    {
        Result<CslItemTypeProfile> profile = await _profiles.GetProfileAsync(itemType);
        ItemEditorFieldSet fields =
            UI.ViewModels.Editor.CslItemTypeProfileService.GetProfile(profile.Value);

        fields.VisibleFields.Single(field => field.Key == "PublicationTitle").Label.Should().Be(expectedLabel);
    }

    [Fact]
    public async Task Profile_keeps_common_editor_labels_localized()
    {
        Result<CslItemTypeProfile> profile = await _profiles.GetProfileAsync("book");
        ItemEditorFieldSet fields =
            UI.ViewModels.Editor.CslItemTypeProfileService.GetProfile(profile.Value);

        fields.VisibleFields.Single(field => field.Key == "Title").Label.Should().Be("标题");
        fields.VisibleFields.Single(field => field.Key == "Creators").Label.Should().Be("作者/贡献者");
    }

    [Fact]
    public async Task Profile_uses_identifier_schemes_for_shortcuts_not_editor_fields()
    {
        Result<CslItemTypeProfile> profile = await _profiles.GetProfileAsync("book");
        ItemEditorFieldSet fields =
            UI.ViewModels.Editor.CslItemTypeProfileService.GetProfile(profile.Value);

        profile.Value.IdentifierSchemes.Should().Equal(
            BuiltInIdentifierSchemes.ISBN, BuiltInIdentifierSchemes.DOI, BuiltInIdentifierSchemes.URL);
        fields.VisibleFields.Should().NotContain(field => field.Key == "IdentifierInput");
        fields.VisibleFields.Should().Contain(field => field.Key == "CollectionTitle");
    }

    [Fact]
    public async Task Book_profile_shows_no_journal_or_call_number_fields()
    {
        Result<CslItemTypeProfile> profile = await _profiles.GetProfileAsync("book");
        ItemEditorFieldSet fields =
            UI.ViewModels.Editor.CslItemTypeProfileService.GetProfile(profile.Value);

        fields.VisibleFields.Should().NotContain(field => field.Key == "PublicationTitle");
        fields.VisibleFields.Select(field => field.Key).Should().Contain(
            ["Publisher", "Place", "Edition", "CollectionTitle"]);
        fields.VisibleFields.Concat(fields.MoreFields).Should().NotContain(field =>
            field.Type == UI.ViewModels.Editor.CslItemTypeProfileService.IdentifierBackedFieldType &&
            field.IdentifierScheme == BuiltInIdentifierSchemes.CallNumber);
    }

    [Fact]
    public async Task Article_journal_shows_journal_fields_in_the_visible_area()
    {
        Result<CslItemTypeProfile> profile = await _profiles.GetProfileAsync("article-journal");
        ItemEditorFieldSet fields =
            UI.ViewModels.Editor.CslItemTypeProfileService.GetProfile(profile.Value);

        fields.VisibleFields.Should().Contain(field => field.Key == "PublicationTitle" && field.Label == "期刊名");
        fields.VisibleFields.Select(field => field.Key).Should().Contain(["Volume", "Issue", "Pages"]);
    }

    [Fact]
    public async Task Webpage_url_projection_takes_the_first_position()
    {
        Result<CslItemTypeProfile> profile = await _profiles.GetProfileAsync("webpage");
        ItemEditorFieldSet fields =
            UI.ViewModels.Editor.CslItemTypeProfileService.GetProfile(profile.Value);

        ItemFieldDefinition projection = fields.VisibleFields[0];
        projection.Key.Should().Be("Identifier:url");
        projection.Type.Should().Be(UI.ViewModels.Editor.CslItemTypeProfileService.IdentifierBackedFieldType);
        projection.IdentifierScheme.Should().Be(BuiltInIdentifierSchemes.URL);
        projection.Label.Should().Be("链接 URL");
    }

    [Theory]
    [InlineData("patent", "专利号")]
    [InlineData("manuscript", "档案号")]
    public async Task Call_number_projection_label_depends_on_item_type(string itemType, string expectedLabel)
    {
        Result<CslItemTypeProfile> profile = await _profiles.GetProfileAsync(itemType);
        ItemEditorFieldSet fields =
            UI.ViewModels.Editor.CslItemTypeProfileService.GetProfile(profile.Value);

        fields.VisibleFields.Should().Contain(field =>
            field.Type == UI.ViewModels.Editor.CslItemTypeProfileService.IdentifierBackedFieldType &&
            field.IdentifierScheme == BuiltInIdentifierSchemes.CallNumber &&
            field.Label == expectedLabel);
        UI.ViewModels.Editor.CslItemTypeProfileService
            .GetIdentifierSchemeLabel(profile.Value, BuiltInIdentifierSchemes.CallNumber)
            .Should().Be(expectedLabel);
    }
}
