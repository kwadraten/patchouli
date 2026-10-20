using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Ids;
using Patchouli.UI.ViewModels.Editor;

namespace Patchouli.Tests;

public sealed class CreatorNameDisplayTests
{
    [Theory]
    [InlineData("日野", "清三郎", "日野清三郎")]
    [InlineData("福田", "千鶴", "福田千鶴")]
    [InlineData("統", "長正", "統長正")]
    [InlineData("欧阳", "修", "欧阳修")]
    [InlineData("やまだ", "たろう", "やまだたろう")]
    [InlineData("金", "민수", "金민수")]
    [InlineData("김", "민수", "김민수")]
    [InlineData("𠮷野", "太郎", "𠮷野太郎")]
    [InlineData("佐々木", "太郎", "佐々木太郎")]
    [InlineData(" Hino ", " Seizaburo ", "Seizaburo Hino")]
    [InlineData("Nguyễn", "Văn An", "Văn An Nguyễn")]
    [InlineData("王", "John", "John 王")]
    [InlineData("日野", null, "日野")]
    [InlineData(null, "清三郎", "清三郎")]
    [InlineData(null, null, "")]
    public void Creator_and_editor_share_name_order_without_reparsing_stored_parts(
        string? family, string? given, string expected)
    {
        ItemCreator creator = new("creator", ItemId.New(), ItemCreatorRoles.Author, family, given,
            null, null, null, 0, DateTimeOffset.UtcNow);
        creator.DisplayName.Should().Be(expected);
        CreatorItemViewModel editor = new(_ => { });
        editor.LoadFrom(creator);
        editor.Name.Should().Be(expected);
        editor.Family.Should().Be(family ?? "");
        editor.Given.Should().Be(given ?? "");
        editor.Literal.Should().BeEmpty();
        editor.IsLiteral.Should().BeFalse();
    }

    [Fact]
    public void Literal_institution_names_and_western_particles_and_suffixes_are_preserved()
    {
        const string institution = "九州大学大学院地球社会統合科学府";
        ItemCreatorNameFormatter.Format("ignored", "ignored", " " + institution + " ").Should().Be(institution);
        ItemCreatorNameFormatter.Format("Beethoven", "Ludwig", suffix: "III", particles: "van")
            .Should().Be("Ludwig van Beethoven III");
        ItemCreatorNameFormatter.Format("日野", "清三郎", suffix: "Jr.").Should().Be("日野清三郎 Jr.");
    }
}
