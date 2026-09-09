using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography.Biblatex;

namespace Patchouli.Tests;

public sealed class BiblatexNumberRoundTripTests
{
    [Theory]
    [InlineData("periodical", "42", "Spring")]
    [InlineData("periodical", "42", null)]
    [InlineData("periodical", null, "Spring")]
    [InlineData("article-journal", "e123", "7")]
    public async Task Number_and_issue_survive_real_helper_round_trip(string itemType, string? number, string? issue)
    {
        ItemMetadata item = CreateItem(itemType, number, issue);
        Result<BiblatexWriteEntryDto> exported = BiblatexExportMapper.MapItem(item);
        exported.IsSuccess.Should().BeTrue(exported.ErrorMessage);
        if (itemType == "periodical")
        {
            exported.Value.Fields.Should().NotContainKey("eid");
        }

        BiblatexHelperClient helper = new();
        Result<string> written = await helper.WriteAsync([exported.Value]);
        written.IsSuccess.Should().BeTrue(written.ErrorMessage);
        Result<IReadOnlyList<BiblatexEntryDto>> parsed = await helper.ParseAsync(written.Value);
        parsed.IsSuccess.Should().BeTrue(parsed.ErrorMessage);
        Result<BiblatexMappedItem> mapped = BiblatexFieldMapper.MapVisibleEntry(parsed.Value.Single());
        mapped.IsSuccess.Should().BeTrue(mapped.ErrorMessage);
        mapped.Value.ItemType.Should().Be(itemType);
        mapped.Value.Number.Should().Be(number);
        mapped.Value.Issue.Should().Be(issue);
        mapped.Value.PublicationTitle.Should().Be(item.PublicationTitle);
        mapped.Value.ContainerTitleShort.Should().Be(item.ContainerTitleShort);
    }

    [Fact]
    public void Original_author_uses_standard_biblatex_name_field()
    {
        ItemMetadata item = CreateItem("book", null, null);
        item = item with
        {
            Creators =
            [
                new ItemCreator("creator", item.ItemId, ItemCreatorRoles.OriginalAuthor,
                    "Doe", "Jane", null, null, null, 0, DateTimeOffset.UtcNow)
            ]
        };

        Result<BiblatexWriteEntryDto> exported = BiblatexExportMapper.MapItem(item);
        exported.IsSuccess.Should().BeTrue(exported.ErrorMessage);
        exported.Value.Persons.Should().ContainSingle();
        exported.Value.Persons["origauthor"].Should().ContainSingle(person => person.Family == "Doe");
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    public void Invalid_custom_fields_do_not_silently_export_incomplete_data(string customFieldsJson)
    {
        ItemMetadata item = CreateItem("periodical", "42", null) with { CustomFieldsJson = customFieldsJson };
        Result<BiblatexWriteEntryDto> exported = BiblatexExportMapper.MapItem(item);
        exported.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    private static ItemMetadata CreateItem(string itemType, string? number, string? issue)
    {
        return new ItemMetadata(
            ItemId.New(), LibraryId.New(), itemType, "periodical-key",
            "Collected issue", null, null, "[]", [],
            null, [], [], "Journal", "J",
            null, null, null, null, null, number,
            null, null, null, issue, null, null,
            null, null, null, "[]", "[]", "{}",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }
}
