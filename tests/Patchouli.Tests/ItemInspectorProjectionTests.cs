using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Ids;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

public sealed class ItemInspectorProjectionTests
{
    [Fact]
    public void Project_orders_basic_identifiers_extended_then_other()
    {
        ItemMetadata metadata = CreateMetadata() with
        {
            CustomFieldsJson = "{\"howpublished\":\"Online\",\"archive\":\"BL\"}",
            TagsJson = "[\"zeta\",\"alpha\"]",
            Subtitle = "A subtitle"
        };

        ItemInspectorProjectionModel projection = ItemInspectorProjection.Project(metadata, null);

        projection.Groups.Select(group => group.Title)
            .Should().Equal("基本信息", "标识符", "扩展信息", "其他");
        projection.Groups[0].Fields.Select(field => field.Label).Should().Contain("条目类型");
        projection.Groups[1].Fields.Should().ContainSingle()
            .Which.Label.Should().Be("ISBN");
        projection.Groups[2].Fields.Should().ContainSingle(field => field.Value == "Online");
        projection.Groups[3].Fields.Should().Contain(field => field.Label == "副标题");
        projection.Tags.Should().Equal("alpha", "zeta");
    }

    [Fact]
    public void Tags_are_deduplicated_and_blank_entries_skipped()
    {
        ItemMetadata metadata = CreateMetadata() with
        {
            TagsJson = "[\"beta\",\"alpha\",\"beta\",\"  \",\"gamma\"]"
        };

        ItemInspectorProjectionModel projection = ItemInspectorProjection.Project(metadata, null);

        projection.Tags.Should().Equal("alpha", "beta", "gamma");
    }

    [Fact]
    public void Malformed_tags_json_yields_no_tags()
    {
        ItemMetadata metadata = CreateMetadata() with { TagsJson = "{not an array" };

        ItemInspectorProjection.Project(metadata, null).Tags.Should().BeEmpty();
    }

    [Fact]
    public void Field_labels_override_fallback_labels()
    {
        ItemMetadata metadata = CreateMetadata() with { PublicationTitle = "Nature", Publisher = "Springer" };
        IReadOnlyDictionary<string, string> labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["container-title"] = "期刊名",
            ["publisher"] = "出版机构"
        };

        ItemInspectorProjectionModel projection = ItemInspectorProjection.Project(metadata, labels);

        InspectorGroupProjection basic = projection.Groups.Single(group => group.Title == "基本信息");
        basic.Fields.Should().Contain(field => field.Label == "期刊名" && field.Value == "Nature");
        basic.Fields.Should().Contain(field => field.Label == "出版机构" && field.Value == "Springer");
    }

    [Fact]
    public void Call_number_label_falls_back_by_item_type()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ItemIdentifier callNumber = new(IdentifierId.New(), ItemId.New(), BuiltInIdentifierSchemes.CallNumber,
            "MS 1", null, now);

        ItemInspectorProjectionModel book = ItemInspectorProjection.Project(
            CreateMetadata() with { Identifiers = [callNumber] }, null);
        ItemInspectorProjectionModel manuscript = ItemInspectorProjection.Project(
            CreateMetadata() with { ItemType = "manuscript", Identifiers = [callNumber] }, null);

        book.Groups.Single(group => group.Title == "标识符").Fields.Single().Label.Should().Be("索书号");
        manuscript.Groups.Single(group => group.Title == "标识符").Fields.Single().Label.Should().Be("档案号");
    }

    [Fact]
    public void Custom_field_label_overrides_call_number_fallback()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ItemIdentifier callNumber = new(IdentifierId.New(), ItemId.New(), BuiltInIdentifierSchemes.CallNumber,
            "MS 1", null, now);
        IReadOnlyDictionary<string, string> labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["call-number"] = "架位号"
        };

        ItemInspectorProjectionModel projection = ItemInspectorProjection.Project(
            CreateMetadata() with { Identifiers = [callNumber] }, labels);

        projection.Groups.Single(group => group.Title == "标识符").Fields.Single().Label.Should().Be("架位号");
    }

    [Fact]
    public void Manuscript_suppresses_place_but_book_keeps_it()
    {
        ItemInspectorProjectionModel book = ItemInspectorProjection.Project(
            CreateMetadata() with { Place = "London" }, null);
        ItemInspectorProjectionModel manuscript = ItemInspectorProjection.Project(
            CreateMetadata() with { ItemType = "manuscript", Place = "London" }, null);

        book.Groups.Single(group => group.Title == "基本信息").Fields
            .Should().Contain(field => field.Label == "出版地" && field.Value == "London");
        manuscript.Groups.Single(group => group.Title == "基本信息").Fields
            .Should().NotContain(field => field.Label == "出版地");
    }

    [Fact]
    public void Basic_custom_keys_stay_out_of_the_extended_group()
    {
        ItemMetadata metadata = CreateMetadata() with
        {
            CustomFieldsJson =
            "{\"archive\":\"BL\",\"event-place\":\"Kyoto\",\"note_custom\":\"else\",\"howpublished\":\"Web\"}"
        };

        ItemInspectorProjectionModel projection = ItemInspectorProjection.Project(metadata, null);

        projection.Groups.Single(group => group.Title == "基本信息").Fields
            .Should().Contain(field => field.Label == "档案馆" && field.Value == "BL")
            .And.Contain(field => field.Label == "会议地点" && field.Value == "Kyoto");
        InspectorGroupProjection extended = projection.Groups.Single(group => group.Title == "扩展信息");
        extended.Fields.Should().NotContain(field => field.Value == "BL" || field.Value == "Kyoto");
        extended.Fields.Select(field => field.Value).Should().Equal(new[] { "Web", "else" },
            "extended fields are ordered by custom-field key: howpublished then note_custom");
    }

    [Fact]
    public void Non_object_or_blank_custom_fields_yield_no_extended_group()
    {
        ItemInspectorProjection.Project(CreateMetadata() with { CustomFieldsJson = "[1,2]" }, null)
            .Groups.Should().NotContain(group => group.Title == "扩展信息");
        ItemInspectorProjection.Project(CreateMetadata() with { CustomFieldsJson = "  " }, null)
            .Groups.Should().NotContain(group => group.Title == "扩展信息");
    }

    [Fact]
    public void Note_and_abstract_wrap_while_scalar_fields_do_not()
    {
        ItemMetadata metadata = CreateMetadata() with { Note = "n", Abstract = "a" };

        ItemInspectorProjectionModel projection = ItemInspectorProjection.Project(metadata, null);

        projection.Groups.SelectMany(group => group.Fields)
            .Where(field => field.Label is "附注" or "摘要")
            .Should().OnlyContain(field => field.WrapText);
        projection.Groups.SelectMany(group => group.Fields)
            .Where(field => field.Label == "标题")
            .Should().OnlyContain(field => !field.WrapText);
    }

    [Fact]
    public void Creators_are_ordered_by_sequence_and_blank_names_dropped()
    {
        ItemId itemId = ItemId.New();
        ItemMetadata metadata = CreateMetadata() with
        {
            Creators =
            [
                new ItemCreator(Guid.NewGuid().ToString("D"), itemId, "author", "Doe", "Jane", null, null,
                    null, 1, DateTimeOffset.UtcNow),
                new ItemCreator(Guid.NewGuid().ToString("D"), itemId, "author", "Smith", "John", null, null,
                    null, 0, DateTimeOffset.UtcNow),
                new ItemCreator(Guid.NewGuid().ToString("D"), itemId, "author", " ", null, null, null,
                    null, 2, DateTimeOffset.UtcNow)
            ]
        };

        ItemInspectorProjectionModel projection = ItemInspectorProjection.Project(metadata, null);

        InspectorFieldProjection authors = projection.Groups.Single(group => group.Title == "基本信息")
            .Fields.Single(field => field.Label == "作者");
        authors.Value.Should().Be("John Smith, Jane Doe");
    }

    private static ItemMetadata CreateMetadata()
    {
        ItemId itemId = ItemId.New();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new ItemMetadata(
            itemId,
            LibraryId.New(),
            "book",
            "doe2020",
            "The Example Book",
            null,
            null,
            "[]",
            [],
            "2020",
            [],
            [new ItemIdentifier(IdentifierId.New(), itemId, "isbn", "978-1", null, now)],
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
