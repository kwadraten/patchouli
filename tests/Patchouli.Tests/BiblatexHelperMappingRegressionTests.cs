using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography.Biblatex;

namespace Patchouli.Tests;

public sealed class BiblatexHelperMappingRegressionTests
{
    [Fact]
    public async Task Editor_groups_do_not_duplicate_creators_after_round_trip()
    {
        BiblatexMappedItem mapped = await ParseAndMap(
            "@book{k,title={T},editor={Doe, Jane},editora={Smith, John},editorb={Lee, Ann},editorc={Li, Bo}}");
        mapped.Creators.Select(person => person.Family).Should().Equal("Doe", "Smith", "Lee", "Li");
        mapped.Creators.Should().OnlyContain(person => person.Role == ItemCreatorRoles.Editor);

        BiblatexMappedItem roundTripped = await RoundTrip(mapped);
        roundTripped.Creators.Should().BeEquivalentTo(mapped.Creators, options => options.WithStrictOrdering());
    }

    [Theory]
    [InlineData("movie", "motion_picture")]
    [InlineData("artwork", "graphic")]
    [InlineData("letter", "personal_communication")]
    public async Task Extension_types_survive_real_helper_round_trip(string type, string itemType)
    {
        BiblatexMappedItem mapped = await ParseAndMap($"@{type}{{k,title={{T}},director={{Doe, Jane}}}}");
        mapped.ItemType.Should().Be(itemType);
        mapped.Creators.Should().ContainSingle(person => person.Role == ItemCreatorRoles.Director);

        BiblatexMappedItem roundTripped = await RoundTrip(mapped);
        roundTripped.ItemType.Should().Be(itemType);
        roundTripped.Creators.Should().BeEquivalentTo(mapped.Creators);
    }

    [Theory]
    [InlineData("unpublish")]
    [InlineData("custom-type")]
    public async Task Unknown_types_retain_original_name(string type)
    {
        BiblatexMappedItem mapped = await ParseAndMap($"@{type}{{k,title={{T}}}}");
        mapped.ItemType.Should().Be("general");
        mapped.OriginalBiblatexEntryType.Should().Be(type);
    }

    [Fact]
    public async Task Original_author_maps_as_creator_and_round_trips()
    {
        BiblatexMappedItem mapped = await ParseAndMap("@software{k,title={T},origauthor={Doe, Jane}}");
        mapped.Creators.Should().ContainSingle(person =>
            person.Role == ItemCreatorRoles.OriginalAuthor && person.Family == "Doe" && person.Given == "Jane");
        mapped.CustomFields.Should().NotContainKey("origauthor");

        BiblatexMappedItem roundTripped = await RoundTrip(mapped);
        roundTripped.Creators.Should().BeEquivalentTo(mapped.Creators);
    }

    private static async Task<BiblatexMappedItem> ParseAndMap(string text)
    {
        Result<IReadOnlyList<BiblatexEntryDto>> parsed = await new BiblatexHelperClient().ParseAsync(text);
        parsed.IsSuccess.Should().BeTrue(parsed.ErrorMessage);
        Result<BiblatexMappedItem> mapped = BiblatexFieldMapper.MapVisibleEntry(parsed.Value.Single());
        mapped.IsSuccess.Should().BeTrue(mapped.ErrorMessage);
        return mapped.Value;
    }

    private static async Task<BiblatexMappedItem> RoundTrip(BiblatexMappedItem mapped)
    {
        ItemId itemId = ItemId.New();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ItemCreator[] creators = mapped.Creators.Select((person, index) => new ItemCreator(
            Guid.NewGuid().ToString(), itemId, person.Role, person.Family, person.Given, person.Literal,
            person.Suffix, person.Particles, index, now)).ToArray();
        ItemMetadata item = new(itemId, LibraryId.New(), mapped.ItemType, "k", mapped.Title,
            null, null, "[]", creators, null, [], [], null, null, null, null, null, null, null,
            null, null, null, null, null, null, null, null, null, null, "[]", "[]", "{}", now, now);
        Result<BiblatexWriteEntryDto> exported = BiblatexExportMapper.MapItem(item);
        exported.IsSuccess.Should().BeTrue(exported.ErrorMessage);
        Result<string> written = await new BiblatexHelperClient().WriteAsync([exported.Value]);
        written.IsSuccess.Should().BeTrue(written.ErrorMessage);
        return await ParseAndMap(written.Value);
    }
}
