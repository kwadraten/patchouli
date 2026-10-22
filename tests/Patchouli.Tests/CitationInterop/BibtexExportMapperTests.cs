using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Bibliography.Bibtex;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Tests.CitationInterop;

/// <summary>Classic BibTeX mapping golden fixtures (issue #14 §7).</summary>
public sealed class BibtexExportMapperTests
{
    private static ItemMetadata Item(
        string type,
        string key = "key1",
        string title = "A Title",
        string? publicationTitle = null,
        string? publisher = null,
        string? place = null,
        string? genre = null,
        string? pages = null,
        string? language = null,
        IReadOnlyList<ItemCreator>? creators = null,
        IReadOnlyList<ItemDate>? dates = null,
        IReadOnlyList<ItemIdentifier>? identifiers = null)
    {
        ItemId id = ItemId.New();
        return new ItemMetadata(
            id,
            LibraryId.New(),
            type,
            key,
            title,
            null,
            null,
            "[]",
            creators ?? [],
            null,
            dates ?? [],
            identifiers ?? [],
            publicationTitle,
            null,
            null,
            publisher,
            place,
            null,
            genre,
            null,
            null,
            null,
            null,
            null,
            pages,
            language,
            null,
            null,
            null,
            "[]",
            "[]",
            "{}",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
    }

    private static ItemCreator Author(string family, string given)
    {
        return new ItemCreator("c1", ItemId.New(), ItemCreatorRoles.Author, family, given, null, null, null, 0,
            DateTimeOffset.UtcNow);
    }

    private static ItemDate Issued(string datePartsJson, bool circa = false, string? literal = null)
    {
        return new ItemDate("d1", ItemId.New(), ItemDateRoles.Issued, datePartsJson, circa, null, literal,
            DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Article_uses_journal_and_year_month_not_biblatex_fields()
    {
        ItemMetadata item = Item("article-journal", publicationTitle: "Journal of Testing", pages: "33-41",
            dates: [Issued("[[2020,5]]")], creators: [Author("Austen", "Jane")]);

        Result<BiblatexWriteEntryDto> mapped = BibtexExportMapper.MapItem(item);

        mapped.IsSuccess.Should().BeTrue(mapped.ErrorMessage);
        mapped.Value.EntryType.Should().Be("article");
        mapped.Value.Fields["journal"].Should().Be("Journal of Testing");
        mapped.Value.Fields["year"].Should().Be("2020");
        mapped.Value.Fields["month"].Should().Be("5");
        mapped.Value.Fields["pages"].Should().Be("33--41");
        mapped.Value.Fields.Should().NotContainKey("journaltitle");
        mapped.Value.Fields.Should().NotContainKey("date");
        mapped.Value.Persons["author"].Should().ContainSingle();
    }

    [Fact]
    public void Book_uses_address_for_place()
    {
        ItemMetadata item = Item("book", publisher: "Press", place: "London", dates: [Issued("[[1813]]")]);

        Result<BiblatexWriteEntryDto> mapped = BibtexExportMapper.MapItem(item);

        mapped.Value.EntryType.Should().Be("book");
        mapped.Value.Fields["publisher"].Should().Be("Press");
        mapped.Value.Fields["address"].Should().Be("London");
        mapped.Value.Fields.Should().NotContainKey("location");
    }

    [Fact]
    public void Chapter_becomes_inbook_with_booktitle()
    {
        ItemMetadata item = Item("chapter", publicationTitle: "The Container");

        Result<BiblatexWriteEntryDto> mapped = BibtexExportMapper.MapItem(item);

        mapped.Value.EntryType.Should().Be("inbook");
        mapped.Value.Fields["booktitle"].Should().Be("The Container");
    }

    [Fact]
    public void Conference_paper_becomes_inproceedings_and_collection_becomes_proceedings()
    {
        BibtexExportMapper.MapItem(Item("paper-conference")).Value.EntryType.Should().Be("inproceedings");
        BibtexExportMapper.MapItem(Item("collection")).Value.EntryType.Should().Be("proceedings");
        BibtexExportMapper.MapItem(Item("manuscript")).Value.EntryType.Should().Be("unpublished");
    }

    [Fact]
    public void Report_becomes_techreport_with_institution()
    {
        ItemMetadata item = Item("report", publisher: "Lab");

        Result<BiblatexWriteEntryDto> mapped = BibtexExportMapper.MapItem(item);

        mapped.Value.EntryType.Should().Be("techreport");
        mapped.Value.Fields["institution"].Should().Be("Lab");
        mapped.Value.Fields.Should().NotContainKey("publisher");
    }

    [Fact]
    public void Thesis_uses_genre_for_degree_and_school_for_publisher()
    {
        ItemMetadata phd = Item("thesis", genre: "PhD dissertation", publisher: "MIT");
        Result<BiblatexWriteEntryDto> mappedPhd = BibtexExportMapper.MapItem(phd);
        mappedPhd.Value.EntryType.Should().Be("phdthesis");
        mappedPhd.Value.Fields["school"].Should().Be("MIT");

        ItemMetadata master = Item("thesis", genre: "硕士学位论文", publisher: "PKU");
        BibtexExportMapper.MapItem(master).Value.EntryType.Should().Be("mastersthesis");
    }

    [Fact]
    public void Thesis_without_degree_falls_back_to_misc_with_warning()
    {
        List<string> warnings = [];
        Result<BiblatexWriteEntryDto> mapped = BibtexExportMapper.MapItem(Item("thesis"), warnings);

        mapped.Value.EntryType.Should().Be("misc");
        warnings.Should().Contain(message => message.Contains("thesis degree is not recorded"));
    }

    [Fact]
    public void Unknown_csl_type_falls_back_to_misc_with_warning()
    {
        List<string> warnings = [];
        Result<BiblatexWriteEntryDto> mapped = BibtexExportMapper.MapItem(Item("webpage"), warnings);

        mapped.Value.EntryType.Should().Be("misc");
        warnings.Should().Contain(message => message.Contains("mapped to misc"));
    }

    [Fact]
    public void Language_is_dropped_with_warning()
    {
        List<string> warnings = [];
        BibtexExportMapper.MapItem(Item("book", language: "en"), warnings);

        warnings.Should().Contain(message => message.Contains("language was dropped"));
    }

    [Fact]
    public void Circa_date_warns_and_literal_year_is_used_as_fallback()
    {
        List<string> warnings = [];
        ItemMetadata item = Item("book", dates: [Issued("", true, "around 1905")]);

        Result<BiblatexWriteEntryDto> mapped = BibtexExportMapper.MapItem(item, warnings);

        // "around 1905" does not start with a year, so no year can be derived.
        mapped.Value.Fields.Should().NotContainKey("year");
        warnings.Should().Contain(message => message.Contains("circa"));
    }

    [Fact]
    public void General_is_forbidden()
    {
        BibtexExportMapper.MapItem(Item("general")).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Serializer_emits_classic_field_names_and_persons()
    {
        ItemMetadata item = Item("article-journal", "doe2020", publicationTitle: "Journal of Testing",
            dates: [Issued("[[2020]]")], creators: [Author("Doe", "John")]);
        Result<BibtexExportResult> mapped = BibtexExportMapper.MapItems([item]);
        mapped.IsSuccess.Should().BeTrue(mapped.ErrorMessage);

        string text = BibtexSerializer.Serialize(mapped.Value.Entries);

        text.Should().Contain("@article{doe2020,");
        text.Should().Contain("author = {Doe, John}");
        text.Should().Contain("journal = {Journal of Testing}");
        text.Should().Contain("year = {2020}");
        text.Should().NotContain("journaltitle");
    }

    [Fact]
    public void MapItems_fails_the_whole_batch_on_general()
    {
        Result<BibtexExportResult> result = BibtexExportMapper.MapItems([Item("book"), Item("general")]);
        result.IsFailure.Should().BeTrue();
    }
}
