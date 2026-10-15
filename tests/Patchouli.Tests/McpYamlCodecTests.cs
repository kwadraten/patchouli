using System.Text.Json.Nodes;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Mcp;

namespace Patchouli.Tests;

public sealed class McpYamlCodecTests
{
    [Fact]
    public void Generic_yaml_round_trip_keeps_json_scalar_types_and_lf()
    {
        JsonObject value = new()
        {
            ["integer"] = 2,
            ["decimal"] = 2.5,
            ["boolean"] = true,
            ["null"] = null,
            ["string"] = "true"
        };

        string yaml = McpYamlCodec.Encode(value);
        JsonObject decoded = (JsonObject)McpYamlCodec.Decode(yaml);

        decoded["integer"]!.GetValue<int>().Should().Be(2);
        decoded["decimal"]!.GetValue<double>().Should().Be(2.5);
        decoded["boolean"]!.GetValue<bool>().Should().BeTrue();
        decoded["null"].Should().BeNull();
        decoded["string"]!.GetValue<string>().Should().Be("true");
        yaml.Should().NotContain("\r");
    }

    [Theory]
    [InlineData("a: 1\na: 2\n")]
    [InlineData("a: 1\n---\nb: 2\n")]
    [InlineData("a: !custom value\n")]
    [InlineData("a: &anchor value\nb: *anchor\n")]
    public void Generic_yaml_decoder_rejects_ambiguous_or_extended_documents(string yaml)
    {
        Action decode = () => McpYamlCodec.Decode(yaml);

        decode.Should().Throw<FormatException>();
    }

    [Fact]
    public void Item_yaml_round_trip_preserves_general_metadata_and_keeps_collections_read_only()
    {
        ItemId itemId = ItemId.New();
        LibraryId libraryId = LibraryId.New();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ItemMetadata existing = new(itemId, libraryId, "general", "unchanged-key", "A title", "Subtitle",
            "Short", "[]", [
                new ItemCreator("creator-1", itemId, "author", "Family", "Given", null,
                    "Jr.", "de", 0, now)
            ], "2024", [],
            [new ItemIdentifier(IdentifierId.New(), itemId, "doi", "10.1234/example", "source note", now)],
            "Journal", "J. Short", "Series", "Press", "Place", "2", "Article", "N 4", "Chapter 3", "9",
            "v1", "4", "20-29", "en", "published", "An item note", "An abstract", "[\"existing-tag\"]",
            "[\"collection-id\"]", "{\"nested\":{\"enabled\":true,\"count\":3}}", now, now);
        McpItemMetadataResponse projection = new(itemId, existing.ItemType, existing.CitationKey, existing.Title,
            existing.Subtitle, existing.TitleShort, existing.CreatorsJson, existing.Date, existing.PublicationTitle,
            existing.ContainerTitleShort, existing.CollectionTitle, existing.Publisher, existing.Place,
            existing.Edition,
            existing.Genre, existing.Number, existing.ChapterNumber, existing.Volume, existing.Version, existing.Issue,
            existing.Pages, existing.Language, existing.Status, existing.Note, existing.Abstract, existing.TagsJson,
            existing.CollectionsJson, existing.CustomFieldsJson, now,
            [new McpItemCreator("author", "Family", "Given", null, "Jr.", "de", 0, "Given de Family Jr.")],
            [new McpItemDate("issued", "[2024,3,2]", true, "spring", null)],
            [
                new McpItemIdentifier("doi", "10.1234/example", "source note"),
                new McpItemIdentifier("isbn", "978-1", null)
            ]);

        string yaml = McpItemYamlCodec.Encode(projection, true);
        Result<(UpdateItemRequest Request, IReadOnlyList<ItemIdentifierInput> Identifiers)> decoded =
            McpItemYamlCodec.Decode(yaml, existing, false);
        JsonObject yamlRoot = (JsonObject)McpYamlCodec.Decode(yaml);

        decoded.IsSuccess.Should().BeTrue(decoded.ErrorMessage);
        yamlRoot["profile"]!.GetValue<string>().Should().Be("patchouli-item-1");
        ((JsonObject)yamlRoot["csl"]!).TryGetPropertyValue("type", out JsonNode? nativeType).Should().BeFalse();
        decoded.Value.Request.ItemType.Should().Be("general");
        decoded.Value.Request.Title.Should().Be("A title");
        decoded.Value.Request.Creators.Should().ContainSingle().Which.Particles.Should().Be("de");
        decoded.Value.Request.Dates.Should().ContainSingle().Which.DatePartsJson.Should().Be("[2024,3,2]");
        decoded.Value.Identifiers.Should().HaveCount(2);
        decoded.Value.Identifiers[0].Note.Should().Be("source note");
        decoded.Value.Request.CustomFieldsJson.Should().Contain("\"enabled\":true");
        decoded.Value.Request.TagsJson.Should().Be("[\"existing-tag\"]");
        decoded.Value.Request.CollectionsJson.Should().Be(existing.CollectionsJson);
        decoded.Value.Request.Collections.Should().BeNull();
    }

    [Fact]
    public void Item_yaml_native_csl_edits_win_and_null_optional_fields_clear()
    {
        ItemId itemId = ItemId.New();
        LibraryId libraryId = LibraryId.New();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ItemMetadata existing = new(itemId, libraryId, "book", "keep-key", "Old title", "Remove me", null,
            "[]", [], "2020", [], [], null, null, null, null, null, null, null, null, null, null, null, null,
            null, null, null, null, null, "[]", "[]", "{}", now, now);
        McpItemMetadataResponse projection = new(itemId, "book", "keep-key", "Old title", "Remove me", null,
            "[]", "2020", null, null, null, null, null, null, null, null, null, null, null, null, null, null,
            null, null, null, "[]", "[]", "{}", now, [], [], []);
        JsonObject root = (JsonObject)McpYamlCodec.Decode(McpItemYamlCodec.Encode(projection, true));
        JsonObject csl = (JsonObject)root["csl"]!;
        csl["type"] = "article-journal";
        csl["author"] = new JsonArray(new JsonObject { ["family"] = "Edited" });
        csl["issued"] = new JsonObject { ["date-parts"] = new JsonArray(new JsonArray(2032, 4, 5)) };
        csl["subtitle"] = null;
        string editedYaml = McpYamlCodec.Encode(root);

        Result<(UpdateItemRequest Request, IReadOnlyList<ItemIdentifierInput> Identifiers)> decoded =
            McpItemYamlCodec.Decode(editedYaml, existing, false);

        decoded.IsSuccess.Should().BeTrue(decoded.ErrorMessage);
        decoded.Value.Request.ItemType.Should().Be("article-journal");
        decoded.Value.Request.Creators.Should().ContainSingle().Which.Family.Should().Be("Edited");
        decoded.Value.Request.Dates.Should().ContainSingle().Which.DatePartsJson.Should().Be("[2032,4,5]");
        decoded.Value.Request.Subtitle.Should().BeNull();
    }

    [Fact]
    public void Item_yaml_preserves_same_scheme_identifier_siblings_and_edits_only_projected_value()
    {
        ItemId itemId = ItemId.New();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ItemMetadata existing = EmptyItem(itemId, now);
        McpItemMetadataResponse projection = new(itemId, "book", "key", "Title", null, null, "[]", null,
            null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null,
            "[]", "[]", "{}", now, [], [],
            [
                new McpItemIdentifier("isbn", "978-first", "catalogue A"),
                new McpItemIdentifier("isbn", "978-second", "catalogue B"),
                new McpItemIdentifier("doi", "10.1/keep", "doi note")
            ]);
        JsonObject root = (JsonObject)McpYamlCodec.Decode(McpItemYamlCodec.Encode(projection, true));
        string original = McpYamlCodec.Encode(root);

        Result<(UpdateItemRequest Request, IReadOnlyList<ItemIdentifierInput> Identifiers)> unchanged =
            McpItemYamlCodec.Decode(original, existing, false);
        unchanged.IsSuccess.Should().BeTrue(unchanged.ErrorMessage);
        unchanged.Value.Identifiers.Should().ContainInOrder(
            new ItemIdentifierInput("isbn", "978-first", "catalogue A"),
            new ItemIdentifierInput("isbn", "978-second", "catalogue B"),
            new ItemIdentifierInput("doi", "10.1/keep", "doi note"));

        ((JsonObject)root["csl"]!)["ISBN"] = "978-edited";
        Result<(UpdateItemRequest Request, IReadOnlyList<ItemIdentifierInput> Identifiers)> edited =
            McpItemYamlCodec.Decode(McpYamlCodec.Encode(root), existing, false);
        edited.IsSuccess.Should().BeTrue(edited.ErrorMessage);
        edited.Value.Identifiers.Should().ContainInOrder(
            new ItemIdentifierInput("isbn", "978-edited", "catalogue A"),
            new ItemIdentifierInput("isbn", "978-second", "catalogue B"),
            new ItemIdentifierInput("doi", "10.1/keep", "doi note"));

        ((JsonObject)root["csl"]!)["ISBN"] = null;
        Result<(UpdateItemRequest Request, IReadOnlyList<ItemIdentifierInput> Identifiers)> cleared =
            McpItemYamlCodec.Decode(McpYamlCodec.Encode(root), existing, false);
        cleared.IsSuccess.Should().BeTrue(cleared.ErrorMessage);
        cleared.Value.Identifiers.Should().ContainSingle()
            .Which.Should().Be(new ItemIdentifierInput("doi", "10.1/keep", "doi note"));
    }

    [Theory]
    [InlineData("profile: patchouli-item-1\ncsl: {type: book}\nextensions: nope\n")]
    [InlineData("profile: patchouli-item-1\ncsl: {type: book}\nextensions:\n  patchouli:\n    custom_fields: []\n")]
    [InlineData("profile: patchouli-item-1\ncsl: {type: book}\nextensions:\n  patchouli:\n    tags: [a, 3]\n")]
    [InlineData("profile: patchouli-item-1\ncsl: {type: book}\nextensions:\n  patchouli: null\n")]
    [InlineData("profile: patchouli-item-1\ncsl: {type: book}\nextensions:\n  patchouli:\n    tags: null\n")]
    [InlineData("profile: patchouli-item-1\ncsl:\n  type: book\n  issued: {circa: perhaps}\n")]
    [InlineData("profile: patchouli-item-1\ncsl:\n  type: book\n  issued: {circa: null}\n")]
    [InlineData("profile: patchouli-item-1\ncsl:\n  type: book\n  author: one\n")]
    [InlineData("profile: patchouli-item-1\ncsl:\n  type: book\n  issued: 2030\n")]
    [InlineData("profile: patchouli-item-1\ncsl: {type: book, custom-field: ignored}\n")]
    public void Item_yaml_rejects_malformed_native_and_extension_shapes(string yaml)
    {
        ItemId itemId = ItemId.New();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ItemMetadata existing = EmptyItem(itemId, now);
        Result<(UpdateItemRequest Request, IReadOnlyList<ItemIdentifierInput> Identifiers)> decoded =
            McpItemYamlCodec.Decode(yaml, existing, false);

        decoded.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Item_yaml_syntax_errors_are_returned_as_invalid_content()
    {
        ItemId itemId = ItemId.New();
        ItemMetadata existing = EmptyItem(itemId, DateTimeOffset.UtcNow);

        Result<(UpdateItemRequest Request, IReadOnlyList<ItemIdentifierInput> Identifiers)> decoded =
            McpItemYamlCodec.Decode("@misc{broken,", existing, false);

        decoded.IsFailure.Should().BeTrue();
        decoded.ErrorCode.Should().Be("validation_failed");
    }

    private static ItemMetadata EmptyItem(ItemId itemId, DateTimeOffset now)
    {
        return new ItemMetadata(
            itemId,
            LibraryId.New(),
            "book",
            "key",
            "Title",
            null,
            null,
            "[]",
            [],
            null,
            [],
            [],
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            "[]",
            "[]",
            "{}",
            now,
            now);
    }
}
