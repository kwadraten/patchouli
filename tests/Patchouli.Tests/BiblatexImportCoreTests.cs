using System.Text;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Conflicts;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography.Biblatex;
using Patchouli.Infrastructure.Conflicts;

namespace Patchouli.Tests;

public sealed class BiblatexImportCoreTests
{
    [Fact]
    public void Conflict_codes_include_cf07_and_cf08()
    {
        ConflictCode.IsKnown(ConflictCode.BiblatexItemFieldConflict).Should().BeTrue();
        ConflictCode.IsKnown(ConflictCode.BiblatexBatchLinkCandidates).Should().BeTrue();
        ConflictDomain.BibliographyImport.Should().Be("bibliography_import");
    }

    [Fact]
    public void Entry_type_map_follows_citation_js_equivalences()
    {
        BiblatexEntryTypeMap.ResolvePatchouliItemType("booklet", out string? retained)
            .Should().Be("book");
        retained.Should().BeNull();

        BiblatexEntryTypeMap.ResolvePatchouliItemType("incollection", out retained)
            .Should().Be("chapter");

        BiblatexEntryTypeMap.ResolvePatchouliItemType("misc", out retained)
            .Should().Be("document");
        retained.Should().BeNull();

        BiblatexEntryTypeMap.ResolvePatchouliItemType("totally-unknown", out retained)
            .Should().Be("general");
        retained.Should().Be("totally-unknown");

        BiblatexEntryTypeMap.TryMapExportEntryType("article-journal", out string export)
            .Should().BeTrue();
        export.Should().Be("article");

        BiblatexEntryTypeMap.TryMapExportEntryType("general", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("movie", "motion_picture")]
    [InlineData("video", "motion_picture")]
    [InlineData("jurisdiction", "legal_case")]
    [InlineData("legislation", "legislation")]
    [InlineData("music", "musical_score")]
    [InlineData("artwork", "graphic")]
    [InlineData("letter", "personal_communication")]
    [InlineData("performance", "performance")]
    [InlineData("audio", "song")]
    [InlineData("dataset", "dataset")]
    [InlineData("periodical", "periodical")]
    public void Expanded_patchouli_types_no_longer_degrade_to_general(string biblatexType, string expectedType)
    {
        BiblatexEntryTypeMap.ResolvePatchouliItemType(biblatexType, out string? retained)
            .Should().Be(expectedType);
        retained.Should().BeNull();
    }

    [Fact]
    public void Candidate_match_requires_three_of_four_exact_fields()
    {
        BiblatexMappedItem source = SampleMapped(
            "Exact Title",
            ["Author A", "Author B"],
            "Journal X",
            null,
            [2020]);

        BiblatexMatchCandidateSeed full = new(
            ItemId.New().ToString(),
            "Exact Title",
            "Journal X",
            null,
            ["Author A"],
            new HashSet<int> { 2020 });

        BiblatexCandidateMatcher.FindCandidates(source, [full]).Should().ContainSingle();

        BiblatexMatchCandidateSeed weak = full with
        {
            ItemId = ItemId.New().ToString(),
            IssuedYears = new HashSet<int> { 2019 }
        };
        BiblatexCandidateMatcher.FindCandidates(source, [weak]).Should().BeEmpty();
    }

    [Fact]
    public void Author_match_is_order_independent_subset_and_ignores_empty()
    {
        BiblatexFieldMapper.AuthorsMatch(["A", "B"], ["B", "A", "C"]).Should().BeTrue();
        BiblatexFieldMapper.AuthorsMatch(["A", "B"], ["A"]).Should().BeTrue();
        BiblatexFieldMapper.AuthorsMatch([], ["A"]).Should().BeFalse();
        BiblatexFieldMapper.AuthorsMatch(["A"], []).Should().BeFalse();
    }

    [Fact]
    public void Year_match_uses_issued_year_set_overlap_only()
    {
        BiblatexFieldMapper.YearsMatch(new HashSet<int> { 2020, 2021 }, new HashSet<int> { 2021 })
            .Should().BeTrue();
        BiblatexFieldMapper.YearsMatch(new HashSet<int>(), new HashSet<int> { 2020 }).Should().BeFalse();
    }

    [Fact]
    public void Source_match_prefers_journal_over_publisher()
    {
        BiblatexMappedItem withJournal = SampleMapped("T", ["A"], "Journal", "Publisher", [2020]);
        BiblatexMatchCandidateSeed byJournal = new(
            ItemId.New().ToString(),
            "T",
            "Journal",
            "Other",
            ["A"],
            new HashSet<int> { 2020 });
        BiblatexCandidateMatcher.FindCandidates(withJournal, [byJournal]).Should().ContainSingle();

        BiblatexMappedItem publisherOnly = SampleMapped("T", ["A"], null, "Publisher", [2020]);
        BiblatexMatchCandidateSeed byPublisher = byJournal with
        {
            ItemId = ItemId.New().ToString(),
            PublicationTitle = null,
            Publisher = "Publisher"
        };
        BiblatexCandidateMatcher.FindCandidates(publisherOnly, [byPublisher]).Should().ContainSingle();
    }

    [Fact]
    public void Exact_trim_comparison_does_not_fold_case_or_inner_space()
    {
        BiblatexFieldMapper.ExactTrimEquals(" Title ", "Title").Should().BeTrue();
        BiblatexFieldMapper.ExactTrimEquals("Title", "title").Should().BeFalse();
        BiblatexFieldMapper.ExactTrimEquals("A  B", "A B").Should().BeFalse();
    }

    [Fact]
    public void Field_conflicts_ignore_missing_incoming_and_keep_local()
    {
        ItemMetadata local = SampleItem("Local Title", "book", "Old Pub");
        BiblatexMappedItem incoming = SampleMapped("Local Title", [], null, null, []) with
        {
            ItemType = "book",
            Publisher = "New Pub"
        };

        IReadOnlyList<BiblatexFieldConflict> conflicts =
            BiblatexFieldConflictAnalyzer.FindConflicts(local, incoming);

        conflicts.Should().ContainSingle(conflict => conflict.FieldKey == "publisher");
        conflicts.Should().NotContain(conflict => conflict.FieldKey == "publication_title");
    }

    [Fact]
    public void Tag_merge_is_literal_and_case_sensitive()
    {
        IReadOnlyList<string> merged = BiblatexFieldConflictAnalyzer.MergeTags(
            ["Alpha", "beta"],
            ["beta", "Beta", "gamma"]);

        merged.Should().Equal("Alpha", "beta", "Beta", "gamma");
    }

    [Fact]
    public void Identifier_merge_lowercases_scheme_and_trims_value()
    {
        ItemIdentifier local = new(
            IdentifierId.New(),
            ItemId.New(),
            "DOI",
            " 10.1/abc ",
            null,
            DateTimeOffset.UtcNow);

        IReadOnlyList<ItemIdentifierInput> merged = BiblatexFieldConflictAnalyzer.MergeIdentifiers(
            [local],
            [
                new ItemIdentifierInput("doi", "10.1/abc"),
                new ItemIdentifierInput("ISBN", " 978-1 ")
            ]);

        merged.Should().BeEquivalentTo(
        [
            new ItemIdentifierInput("doi", "10.1/abc"),
            new ItemIdentifierInput("isbn", "978-1")
        ]);
    }

    [Fact]
    public void Batch_plan_emits_cf08_only_when_candidates_exist()
    {
        BiblatexEntryDto entry = new(
            "k1",
            "book",
            false,
            new Dictionary<string, string> { ["title"] = "Exact Title", ["publisher"] = "Pub" },
            new Dictionary<string, IReadOnlyList<BiblatexPersonDto>>
            {
                ["author"] = [new BiblatexPersonDto("Doe", "Jane")]
            },
            new Dictionary<string, BiblatexDateDto>
            {
                ["date"] = new([2020], [[2020]])
            },
            [],
            null,
            true,
            new BiblatexVerifyDto([], [], []));

        Result<BiblatexBatchImportPlan> noMatch = BiblatexImportPlanner.PlanBatchImport([entry], []);
        noMatch.IsSuccess.Should().BeTrue();
        noMatch.Value.HasCandidates.Should().BeFalse();
        noMatch.Value.LinkConflictDescriptor.Should().BeNull();

        BiblatexMatchCandidateSeed seed = new(
            ItemId.New().ToString(),
            "Exact Title",
            null,
            "Pub",
            [BiblatexFieldMapper.CreatorMatchKey(new ItemCreatorInput("author", "Doe", "Jane"))],
            new HashSet<int> { 2020 });

        Result<BiblatexBatchImportPlan> withMatch =
            BiblatexImportPlanner.PlanBatchImport([entry], [seed], "batch-1");
        withMatch.IsSuccess.Should().BeTrue();
        withMatch.Value.HasCandidates.Should().BeTrue();
        withMatch.Value.LinkConflictDescriptor!.ConflictCode.Should().Be(ConflictCode.BiblatexBatchLinkCandidates);
        withMatch.Value.LinkConflictDescriptor.Domain.Should().Be(ConflictDomain.BibliographyImport);
    }

    [Fact]
    public void Utf8_strict_reader_rejects_invalid_bytes()
    {
        Result ok = BiblatexImportPlanner.ReadUtf8Strict("中文"u8.ToArray(), out string text);
        ok.IsSuccess.Should().BeTrue();
        text.Should().Be("中文");

        Result bad = BiblatexImportPlanner.ReadUtf8Strict([0xFF, 0xFE, 0xFD], out _);
        bad.IsFailure.Should().BeTrue();
        bad.ErrorCode.Should().Be(AppErrorCodes.BiblatexEncodingError);
    }

    [Fact]
    public void General_export_is_forbidden()
    {
        ItemMetadata general = SampleItem("G", "general", null);
        BiblatexExportMapper.MapItem(general).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Submitted_date_maps_in_both_import_and_export_directions()
    {
        BiblatexEntryDto entry = new(
            "submitted-1",
            "article",
            false,
            new Dictionary<string, string>
            {
                ["title"] = "Submitted paper",
                ["submitted"] = "2025-03-04"
            },
            new Dictionary<string, IReadOnlyList<BiblatexPersonDto>>(),
            new Dictionary<string, BiblatexDateDto>(),
            [],
            null,
            true,
            new BiblatexVerifyDto([], [], []));

        Result<BiblatexMappedItem> imported = BiblatexFieldMapper.MapVisibleEntry(entry);
        imported.IsSuccess.Should().BeTrue(imported.ErrorMessage);
        imported.Value.Dates.Should().ContainSingle(date =>
            date.Role == ItemDateRoles.Submitted && date.Literal == "2025-03-04");

        ItemMetadata source = SampleItem("Submitted paper", "article-journal", null) with
        {
            Dates =
            [
                new ItemDate(
                    Guid.NewGuid().ToString(),
                    ItemId.New(),
                    ItemDateRoles.Submitted,
                    "[[2025,3,4]]",
                    false,
                    null,
                    null,
                    DateTimeOffset.UtcNow)
            ]
        };
        Result<BiblatexWriteEntryDto> exported = BiblatexExportMapper.MapItem(source);
        exported.IsSuccess.Should().BeTrue(exported.ErrorMessage);
        exported.Value.Fields["submitted"].Should().Be("2025-03-04");
    }

    [Fact]
    public async Task Helper_roundtrips_cjk_title_and_note()
    {
        string helper = BiblatexHelperClient.ResolveDefaultHelperPath();
        if (!File.Exists(helper))
        {
            return;
        }

        BiblatexHelperClient client = new(helper);
        const string source =
            """
            @book{cjk1,
              author = {张三},
              title = {中文标题},
              note = {日本語ノートと한글},
              publisher = {テスト社},
              date = {2024}
            }
            """;

        Result<IReadOnlyList<BiblatexEntryDto>> parsed = await client.ParseAsync(source);
        parsed.IsSuccess.Should().BeTrue(parsed.ErrorMessage);
        BiblatexEntryDto entry = parsed.Value.Should().ContainSingle().Subject;
        entry.Fields["title"].Should().Be("中文标题");
        entry.Fields["note"].Should().Be("日本語ノートと한글");

        Result<BiblatexMappedItem> mapped = BiblatexFieldMapper.MapVisibleEntry(entry);
        mapped.IsSuccess.Should().BeTrue(mapped.ErrorMessage);
        mapped.Value.Title.Should().Be("中文标题");
        mapped.Value.Note.Should().Be("日本語ノートと한글");

        Result<BiblatexWriteEntryDto> export = BiblatexExportMapper.MapItem(SampleItem(
            mapped.Value.Title,
            "book",
            mapped.Value.Publisher,
            mapped.Value.Note));
        export.IsSuccess.Should().BeTrue(export.ErrorMessage);

        Result<string> written = await client.WriteAsync([export.Value]);
        written.IsSuccess.Should().BeTrue(written.ErrorMessage);
        written.Value.Should().Contain("中文标题");
        written.Value.Should().Contain("日本語ノートと한글");

        Result<IReadOnlyList<BiblatexEntryDto>> reparsed = await client.ParseAsync(written.Value);
        reparsed.IsSuccess.Should().BeTrue(reparsed.ErrorMessage);
        reparsed.Value.Single().Fields["title"].Should().Be("中文标题");
    }

    [Fact]
    public async Task Helper_reports_parse_failure()
    {
        string helper = BiblatexHelperClient.ResolveDefaultHelperPath();
        if (!File.Exists(helper))
        {
            return;
        }

        BiblatexHelperClient client = new(helper);
        Result<IReadOnlyList<BiblatexEntryDto>> parsed = await client.ParseAsync("@book{broken,");
        parsed.IsFailure.Should().BeTrue();
        parsed.ErrorCode.Should().Be(AppErrorCodes.BiblatexParseFailed);
    }

    [Fact]
    public void Cf07_descriptor_lists_all_conflicting_fields()
    {
        ConflictDescriptor descriptor = ConflictDescriptorMapper.BiblatexItemFieldConflict(
            ItemId.New().ToString(),
            "src1",
            [
                ("title", "标题", "Old", "New"),
                ("publisher", "出版社", "A", "B")
            ]);

        descriptor.ConflictCode.Should().Be(ConflictCode.BiblatexItemFieldConflict);
        descriptor.Domain.Should().Be(ConflictDomain.BibliographyImport);
        descriptor.AvailableOptions.Select(static option => option.OptionId)
            .Should().Equal("title", "publisher");
    }

    [Fact]
    public void Archival_fields_and_callnumber_mapped_properly()
    {
        BiblatexEntryDto entry = new(
            "ms1",
            "unpublished",
            false,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["title"] = "Archival Letter",
                ["archive"] = "British Library",
                ["archive_location"] = "Box 4, Folder 2",
                ["archive-place"] = "London",
                ["callnumber"] = "Add MS 12345"
            },
            new Dictionary<string, IReadOnlyList<BiblatexPersonDto>>(),
            new Dictionary<string, BiblatexDateDto>(),
            [],
            null,
            true,
            new BiblatexVerifyDto([], [], []));

        Result<BiblatexMappedItem> result = BiblatexFieldMapper.MapVisibleEntry(entry);
        result.IsSuccess.Should().BeTrue();
        BiblatexMappedItem item = result.Value;

        item.ItemType.Should().Be("manuscript");
        item.Place.Should().BeNull();
        item.Identifiers.Should()
            .ContainSingle(i => i.Scheme == BuiltInIdentifierSchemes.CallNumber && i.Value == "Add MS 12345");
        item.CustomFields.Should().NotBeNull();
        item.CustomFields!["archive"].Should().Be("British Library");
        item.CustomFields["archive_location"].Should().Be("Box 4, Folder 2");
        item.CustomFields["archive-place"].Should().Be("London");
    }

    [Fact]
    public void Archival_mla_style_library_location_and_number_mapped_to_archive_and_callnumber()
    {
        BiblatexEntryDto entry = new(
            "ms2",
            "unpublished",
            false,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["title"] = "Bodleian Manuscript",
                ["library"] = "Bodleian Library",
                ["location"] = "Oxford",
                ["number"] = "MS. Bodl. 34"
            },
            new Dictionary<string, IReadOnlyList<BiblatexPersonDto>>(),
            new Dictionary<string, BiblatexDateDto>(),
            [],
            null,
            true,
            new BiblatexVerifyDto([], [], []));

        Result<BiblatexMappedItem> result = BiblatexFieldMapper.MapVisibleEntry(entry);
        result.IsSuccess.Should().BeTrue();
        BiblatexMappedItem item = result.Value;

        item.ItemType.Should().Be("manuscript");
        item.Place.Should().BeNull();
        item.Number.Should().BeNull();
        item.Identifiers.Should()
            .ContainSingle(i => i.Scheme == BuiltInIdentifierSchemes.CallNumber && i.Value == "MS. Bodl. 34");
        item.CustomFields.Should().NotBeNull();
        item.CustomFields!["archive"].Should().Be("Bodleian Library");
        item.CustomFields["archive-place"].Should().Be("Oxford");
    }

    [Fact]
    public void Export_preserves_custom_fields_callnumber_and_container_title()
    {
        ItemId itemId = ItemId.New();
        ItemMetadata metadata = new(
            itemId,
            LibraryId.New(),
            "manuscript",
            "key1",
            "Test MS",
            null,
            null,
            "[]",
            [],
            null,
            [],
            [new ItemIdentifier(IdentifierId.New(), itemId, "call_number", "Add MS 999", null, DateTimeOffset.UtcNow)],
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
            System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["archive"] = "British Library",
                ["archive_location"] = "Shelf 1",
                ["archive-place"] = "London"
            }),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        Result<BiblatexWriteEntryDto> exportResult = BiblatexExportMapper.MapItem(metadata);
        exportResult.IsSuccess.Should().BeTrue();
        BiblatexWriteEntryDto dto = exportResult.Value;

        dto.Fields["callnumber"].Should().Be("Add MS 999");
        dto.Fields["archive"].Should().Be("British Library");
        dto.Fields["archive_location"].Should().Be("Shelf 1");
        dto.Fields["archive-place"].Should().Be("London");
    }

    [Fact]
    public void Article_eid_and_number_roundtrip_correctly()
    {
        BiblatexEntryDto entry = new(
            "art1",
            "article",
            false,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["title"] = "Nature Article",
                ["journaltitle"] = "Nature",
                ["eid"] = "e12345",
                ["number"] = "4"
            },
            new Dictionary<string, IReadOnlyList<BiblatexPersonDto>>(),
            new Dictionary<string, BiblatexDateDto>(),
            [],
            null,
            true,
            new BiblatexVerifyDto([], [], []));

        Result<BiblatexMappedItem> mapped = BiblatexFieldMapper.MapVisibleEntry(entry);
        mapped.IsSuccess.Should().BeTrue();
        mapped.Value.Number.Should().Be("e12345");
        mapped.Value.Issue.Should().Be("4");

        ItemMetadata item = SampleItem("Nature Article", "article-journal", "Springer") with
        {
            Number = mapped.Value.Number,
            Issue = mapped.Value.Issue,
            PublicationTitle = mapped.Value.PublicationTitle
        };

        Result<BiblatexWriteEntryDto> exportResult = BiblatexExportMapper.MapItem(item);
        exportResult.IsSuccess.Should().BeTrue();
        exportResult.Value.Fields["eid"].Should().Be("e12345");
        exportResult.Value.Fields["number"].Should().Be("4");
        exportResult.Value.Fields["journaltitle"].Should().Be("Nature");
    }

    [Fact]
    public void Conference_event_fields_and_structured_eventdate_mapped_and_exported()
    {
        BiblatexDateDto eventDate = new(
            [2026],
            [[2026, 7, 12], [2026, 7, 18]],
            null,
            false);

        BiblatexEntryDto entry = new(
            "conf1",
            "inproceedings",
            false,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["title"] = "Conference Paper",
                ["booktitle"] = "ICML Proceedings",
                ["eventtitle"] = "ICML 2026",
                ["venue"] = "Vienna"
            },
            new Dictionary<string, IReadOnlyList<BiblatexPersonDto>>(),
            new Dictionary<string, BiblatexDateDto>
            {
                ["eventdate"] = eventDate
            },
            [],
            null,
            true,
            new BiblatexVerifyDto([], [], []));

        Result<BiblatexMappedItem> mapped = BiblatexFieldMapper.MapVisibleEntry(entry);
        mapped.IsSuccess.Should().BeTrue();
        mapped.Value.CustomFields.Should().NotBeNull();
        mapped.Value.CustomFields!["event-title"].Should().Be("ICML 2026");
        mapped.Value.CustomFields["event-place"].Should().Be("Vienna");
        mapped.Value.Dates.Should().ContainSingle(d => d.Role == ItemDateRoles.EventDate);

        CreateItemRequest createReq = BiblatexMappedItemMerge.ToCreateRequest(mapped.Value);
        createReq.CustomFieldsJson.Should().Contain("ICML 2026");
        createReq.CustomFieldsJson.Should().Contain("Vienna");
    }

    [Fact]
    public void Note_and_addendum_combined_when_both_present()
    {
        BiblatexEntryDto entry = new(
            "note1",
            "article",
            false,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["title"] = "Article With Notes",
                ["note"] = "Primary note.",
                ["addendum"] = "Supplementary addendum."
            },
            new Dictionary<string, IReadOnlyList<BiblatexPersonDto>>(),
            new Dictionary<string, BiblatexDateDto>(),
            [],
            null,
            true,
            new BiblatexVerifyDto([], [], []));

        Result<BiblatexMappedItem> mapped = BiblatexFieldMapper.MapVisibleEntry(entry);
        mapped.IsSuccess.Should().BeTrue();
        mapped.Value.Note.Should().Be("Primary note.\n\nSupplementary addendum.");
    }

    [Fact]
    public void Extended_person_roles_mapped_and_exported()
    {
        BiblatexEntryDto entry = new(
            "movie1",
            "misc",
            false,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["title"] = "Film Title"
            },
            new Dictionary<string, IReadOnlyList<BiblatexPersonDto>>
            {
                ["director"] = [new BiblatexPersonDto("Nolan", "Christopher")],
                ["composer"] = [new BiblatexPersonDto("Zimmer", "Hans")],
                ["holder"] = [new BiblatexPersonDto("Acme Corp", null)]
            },
            new Dictionary<string, BiblatexDateDto>(),
            [],
            null,
            true,
            new BiblatexVerifyDto([], [], []));

        Result<BiblatexMappedItem> mapped = BiblatexFieldMapper.MapVisibleEntry(entry);
        mapped.IsSuccess.Should().BeTrue();
        mapped.Value.Creators.Should().Contain(c => c.Role == ItemCreatorRoles.Director && c.Family == "Nolan");
        mapped.Value.Creators.Should().Contain(c => c.Role == ItemCreatorRoles.Composer && c.Family == "Zimmer");
        mapped.Value.Creators.Should().Contain(c => c.Role == ItemCreatorRoles.Holder && c.Family == "Acme Corp");

        CreateItemRequest createReq = BiblatexMappedItemMerge.ToCreateRequest(mapped.Value);
        createReq.Creators.Should().NotBeNull();
        createReq.Creators!.Should().Contain(c => c.Role == ItemCreatorRoles.Holder);

        ItemId itemId = ItemId.New();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ItemMetadata item = new(
            itemId,
            LibraryId.New(),
            "motion_picture",
            "movie1",
            "Film Title",
            null,
            null,
            "[]",
            [
                new ItemCreator(Guid.NewGuid().ToString(), itemId, ItemCreatorRoles.Director, "Nolan", "Christopher",
                    null, null, null, 0, now),
                new ItemCreator(Guid.NewGuid().ToString(), itemId, ItemCreatorRoles.Composer, "Zimmer", "Hans", null,
                    null, null, 1, now),
                new ItemCreator(Guid.NewGuid().ToString(), itemId, ItemCreatorRoles.Holder, "Acme Corp", null, null,
                    null, null, 2, now)
            ],
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

        Result<BiblatexWriteEntryDto> exportResult = BiblatexExportMapper.MapItem(item);
        exportResult.IsSuccess.Should().BeTrue();
        exportResult.Value.Persons.Should().ContainKey("director");
        exportResult.Value.Persons["director"][0].Family.Should().Be("Nolan");
        exportResult.Value.Persons.Should().ContainKey("composer");
        exportResult.Value.Persons["composer"][0].Family.Should().Be("Zimmer");
        exportResult.Value.Persons.Should().ContainKey("holder");
        exportResult.Value.Persons["holder"][0].Family.Should().Be("Acme Corp");
    }

    [Fact]
    public void Container_title_export_rules_applied()
    {
        // 1. Chapter writes booktitle
        ItemMetadata chapter = SampleItem("A Chapter", "chapter", "Publisher") with
        {
            PublicationTitle = "The Book Title"
        };
        Result<BiblatexWriteEntryDto> chapterExport = BiblatexExportMapper.MapItem(chapter);
        chapterExport.IsSuccess.Should().BeTrue();
        chapterExport.Value.Fields.Should().ContainKey("booktitle");
        chapterExport.Value.Fields["booktitle"].Should().Be("The Book Title");

        // 2. Article-journal writes journaltitle and shortjournal
        ItemMetadata article = SampleItem("An Article", "article-journal", "Publisher") with
        {
            PublicationTitle = "Journal of Science",
            ContainerTitleShort = "J. Sci."
        };
        Result<BiblatexWriteEntryDto> articleExport = BiblatexExportMapper.MapItem(article);
        articleExport.IsSuccess.Should().BeTrue();
        articleExport.Value.Fields.Should().ContainKey("journaltitle");
        articleExport.Value.Fields["journaltitle"].Should().Be("Journal of Science");
        articleExport.Value.Fields.Should().ContainKey("shortjournal");
        articleExport.Value.Fields["shortjournal"].Should().Be("J. Sci.");

        // 3. Other type (e.g. book or manuscript) with PublicationTitle writes maintitle
        ItemMetadata book = SampleItem("A Book", "book", "Publisher") with
        {
            PublicationTitle = "Collected Works of Author"
        };
        Result<BiblatexWriteEntryDto> bookExport = BiblatexExportMapper.MapItem(book);
        bookExport.IsSuccess.Should().BeTrue();
        bookExport.Value.Fields.Should().ContainKey("maintitle");
        bookExport.Value.Fields["maintitle"].Should().Be("Collected Works of Author");
    }

    [Fact]
    public void Date_range_and_circa_export_serialized_correctly()
    {
        ItemId itemId = ItemId.New();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ItemDate rangeDate = new(
            Guid.NewGuid().ToString(),
            itemId,
            ItemDateRoles.Issued,
            "[[1914, 7, 28], [1918, 11, 11]]",
            true, // circa
            null,
            null,
            now);

        ItemMetadata item = SampleItem("WWI History", "book", "Publisher") with
        {
            Dates = [rangeDate]
        };

        Result<BiblatexWriteEntryDto> exportResult = BiblatexExportMapper.MapItem(item);
        exportResult.IsSuccess.Should().BeTrue();
        exportResult.Value.Fields.Should().ContainKey("date");
        exportResult.Value.Fields["date"].Should().Be("1914-07-28~/1918-11-11~");
    }

    private static BiblatexMappedItem SampleMapped(
        string title,
        IReadOnlyList<string> authorKeys,
        string? publicationTitle,
        string? publisher,
        IReadOnlyList<int> years)
    {
        List<ItemCreatorInput> creators = authorKeys
            .Select(static key =>
            {
                string[] parts = key.Split(' ', 2, StringSplitOptions.TrimEntries);
                return parts.Length == 2
                    ? new ItemCreatorInput(ItemCreatorRoles.Author, parts[1], parts[0])
                    : new ItemCreatorInput(ItemCreatorRoles.Author, Literal: key);
            })
            .ToList();

        // Rebuild author keys through mapper for consistency when callers pass family-like tokens.
        if (authorKeys.Count > 0 && authorKeys.All(static key => key.Contains(' ', StringComparison.Ordinal)))
        {
            // already split above
        }
        else if (authorKeys.Count > 0)
        {
            creators = authorKeys
                .Select(static key => new ItemCreatorInput(ItemCreatorRoles.Author, Literal: key))
                .ToList();
        }

        List<ItemDateInput> dates = [];
        if (years.Count > 0)
        {
            int[][] parts = years.Select(static year => new[] { year }).ToArray();
            dates.Add(new ItemDateInput(ItemDateRoles.Issued, System.Text.Json.JsonSerializer.Serialize(parts)));
        }

        return new BiblatexMappedItem(
            "book",
            null,
            title,
            null,
            null,
            creators,
            dates,
            [],
            publicationTitle,
            null,
            null,
            publisher,
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
            [],
            null,
            "src",
            "book");
    }

    private static ItemMetadata SampleItem(
        string title,
        string itemType,
        string? publisher,
        string? note = null)
    {
        ItemId itemId = ItemId.New();
        return new ItemMetadata(
            itemId,
            LibraryId.New(),
            itemType,
            "citation-key",
            title,
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
            publisher,
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
            note,
            null,
            "[]",
            "[]",
            "{}",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
    }
}
