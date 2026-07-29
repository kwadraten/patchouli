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
