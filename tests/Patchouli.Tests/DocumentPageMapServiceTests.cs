using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Pagination;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

/// <summary>
/// Shared database fixture for the issue-4 page-map tests: one migrated temporary database
/// with a library, an item, and a document whose pages carry committed logical-page boxes.
/// Page 1 holds logical boxes A and B (plus one text box), page 2 holds C, page 3 holds D.
/// </summary>
internal sealed class PageMapTestDocument : IAsyncDisposable
{
    private PageMapTestDocument(
        TemporarySqliteDatabase database,
        FixedClock clock,
        DocumentInstanceId documentId,
        IReadOnlyList<PageId> pageIds,
        DocumentBoxId boxA,
        DocumentBoxId boxB,
        DocumentBoxId boxC,
        DocumentBoxId boxD,
        DocumentBoxId textBox,
        DocumentTreeService trees,
        DocumentInstanceService documents,
        PageService pages,
        LibraryIdentityService libraries,
        ItemService items)
    {
        Database = database;
        Clock = clock;
        DocumentId = documentId;
        PageIds = pageIds;
        BoxA = boxA;
        BoxB = boxB;
        BoxC = boxC;
        BoxD = boxD;
        TextBox = textBox;
        Trees = trees;
        Documents = documents;
        Pages = pages;
        Libraries = libraries;
        Items = items;
    }

    public TemporarySqliteDatabase Database { get; }

    public FixedClock Clock { get; }

    public DocumentInstanceId DocumentId { get; }

    public IReadOnlyList<PageId> PageIds { get; }

    public DocumentBoxId BoxA { get; }

    public DocumentBoxId BoxB { get; }

    public DocumentBoxId BoxC { get; }

    public DocumentBoxId BoxD { get; }

    public DocumentBoxId TextBox { get; }

    public DocumentTreeService Trees { get; }

    public DocumentInstanceService Documents { get; }

    public PageService Pages { get; }

    public LibraryIdentityService Libraries { get; }

    public ItemService Items { get; }

    public SqliteConnectionFactory ConnectionFactory => Database.ConnectionFactory;

    public ValueTask DisposeAsync()
    {
        return Database.DisposeAsync();
    }

    public static DocumentBoxSeed LogicalSeed(DocumentBoxId id, int order)
    {
        return new DocumentBoxSeed(id, null, order, DocumentBoxType.LogicalPage, null, null,
            new NormalizedBBox(0.1, 0.1 + order * 0.2, 0.7, 0.1), null);
    }

    public static DocumentBoxSeed TextSeed(
        DocumentBoxId id, int order, string text = "paragraph", DocumentBoxId? parent = null)
    {
        return new DocumentBoxSeed(id, parent, order, DocumentBoxType.Text, null, null,
            new NormalizedBBox(0.1, 0.55, 0.7, 0.1), new TextBoxPayload(text));
    }

    public async Task CommitPageAsync(int pageIndex, params DocumentBoxSeed[] seeds)
    {
        Result<DocumentTreeRevision> working = await Trees.BeginWorkingRevisionAsync(
            DocumentId, PageIds[pageIndex], seeds, DocumentTreeRevisionSource.Import);
        working.IsSuccess.Should().BeTrue(working.ErrorMessage);
        Result<DocumentTreeRevision> committed =
            await Trees.CommitWorkingRevisionAsync(working.Value.TreeRevisionId);
        committed.IsSuccess.Should().BeTrue(committed.ErrorMessage);
    }

    public static async Task<PageMapTestDocument> CreateAsync(int pageCount = 3)
    {
        TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        FixedClock clock = new(DateTimeOffset.Parse("2026-07-13T00:00:00Z"));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();

        LibraryIdentityService libraries = new(database.ConnectionFactory, clock);
        await libraries.CreateLibraryAsync("Page map tests");
        ItemService items = new(database.ConnectionFactory, libraries, clock);
        ItemMetadata item = (await items.CreateItemAsync("document", "Map source")).Value;
        DocumentInstanceService documents = new(database.ConnectionFactory, clock);
        DocumentInstance document = (await documents.AttachDocumentInstanceAsync(
            item.ItemId, null, DocumentInstanceType.PrimaryScan)).Value;

        PageService pages = new(database.ConnectionFactory, clock);
        List<PageId> pageIds = [];
        for (int i = 0; i < pageCount; i++)
        {
            Page page = (await pages.CreatePageAsync(document.DocumentInstanceId, i, (i + 1).ToString(),
                null, null, 0, CoordinateBasis.NormalizedPage, null, null, "test", null)).Value;
            pageIds.Add(page.PageId);
        }

        MarkdigMarkdownEngine markdown = new();
        DocumentTreeService trees = new(database.ConnectionFactory, clock, markdown);

        DocumentBoxId boxA = DocumentBoxId.New();
        DocumentBoxId boxB = DocumentBoxId.New();
        DocumentBoxId boxC = DocumentBoxId.New();
        DocumentBoxId boxD = DocumentBoxId.New();
        DocumentBoxId textBox = DocumentBoxId.New();

        PageMapTestDocument fixture = new(database, clock, document.DocumentInstanceId, pageIds,
            boxA, boxB, boxC, boxD, textBox, trees, documents, pages, libraries, items);

        if (pageCount > 0)
        {
            // A physical page cannot mix direct leaf boxes with logical-page roots, so the
            // text box lives inside logical page A.
            await fixture.CommitPageAsync(0,
                LogicalSeed(boxA, 0),
                TextSeed(textBox, 0, "paragraph", boxA),
                LogicalSeed(boxB, 1));
        }

        if (pageCount > 1)
        {
            await fixture.CommitPageAsync(1, LogicalSeed(boxC, 0));
        }

        if (pageCount > 2)
        {
            await fixture.CommitPageAsync(2, LogicalSeed(boxD, 0));
        }

        return fixture;
    }

    /// <summary>Creates a second, independent document in the same database for cross-document tests.</summary>
    public async Task<(DocumentInstanceId DocumentId, DocumentBoxId LogicalBox)> AddSecondDocumentAsync()
    {
        ItemMetadata item = (await Items.CreateItemAsync("document", "Other document")).Value;
        DocumentInstance document = (await Documents.AttachDocumentInstanceAsync(
            item.ItemId, null, DocumentInstanceType.PrimaryScan)).Value;
        Page page = (await Pages.CreatePageAsync(document.DocumentInstanceId, 0, "1",
            null, null, 0, CoordinateBasis.NormalizedPage, null, null, "test", null)).Value;

        DocumentBoxId otherBox = DocumentBoxId.New();
        Result<DocumentTreeRevision> working = await Trees.BeginWorkingRevisionAsync(
            document.DocumentInstanceId, page.PageId, [LogicalSeed(otherBox, 0)],
            DocumentTreeRevisionSource.Import);
        working.IsSuccess.Should().BeTrue(working.ErrorMessage);
        Result<DocumentTreeRevision> committed =
            await Trees.CommitWorkingRevisionAsync(working.Value.TreeRevisionId);
        committed.IsSuccess.Should().BeTrue(committed.ErrorMessage);

        return (document.DocumentInstanceId, otherBox);
    }

    public DocumentPageMapService CreateMapService()
    {
        return new DocumentPageMapService(ConnectionFactory, Clock, Documents, Pages, Trees);
    }
}

/// <summary>
/// Service-level tests for <see cref="DocumentPageMapService"/>: migration 048, whole-YAML
/// parse/validate/atomic-replace, empty-definition clearing, and the rule that any write
/// validation failure leaves the previously stored map untouched.
/// </summary>
public sealed class DocumentPageMapServiceTests
{
    private static string ValidMapYaml(PageMapTestDocument fixture)
    {
        return ValidMapYamlFor(fixture, fixture.DocumentId);
    }

    private static string ValidMapYamlFor(PageMapTestDocument fixture, DocumentInstanceId documentId)
    {
        DocumentPageMap map = new(documentId,
            [new PhysicalPaginationRule(3, 3, 100)],
            [new LogicalPaginationRule(1, 2, 1,
                [
                    new LogicalPageReadingEntry(fixture.BoxA, 1),
                    new LogicalPageReadingEntry(fixture.BoxB, 1),
                    new LogicalPageReadingEntry(fixture.BoxC, 2)
                ])],
            [new PageLabelOverride(new PageLabelTarget(3, null), "100")],
            [ChapterNode.Leaf("ch1", "第一章", new NavigationPosition(1, fixture.BoxA))]);
        return DocumentPageMapYamlCodec.Encode(map, true);
    }

    private static async Task<Result<ResolvedDocumentPageMap?>> GetResolvedAsync(PageMapTestDocument fixture)
    {
        return await fixture.CreateMapService().GetResolvedAsync(fixture.DocumentId);
    }

    [Fact]
    public async Task Migration_048_creates_document_page_maps_with_document_cascade()
    {
        await using PageMapTestDocument fixture = await PageMapTestDocument.CreateAsync();

        await using SqliteConnection connection = fixture.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();

        string? table = await connection.ExecuteScalarAsync<string>(
            "select name from sqlite_master where type = 'table' and name = 'document_page_maps';");
        table.Should().Be("document_page_maps");

        var foreignKeys = (await connection.QueryAsync<FkRow>(
            "pragma foreign_key_list(document_page_maps);")).ToList();
        FkRow cascade = foreignKeys.Should().ContainSingle().Subject;
        cascade.Table.Should().Be("document_instances");
        cascade.OnDelete.Should().Be("CASCADE");
    }

    [Fact]
    public async Task Migration_runner_is_idempotent_on_a_migrated_database()
    {
        await using PageMapTestDocument fixture = await PageMapTestDocument.CreateAsync();

        Func<Task> rerun = () => new MigrationRunner(
            fixture.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();

        await rerun.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Put_valid_map_round_trips_and_resolves_printed_numbers()
    {
        await using PageMapTestDocument fixture = await PageMapTestDocument.CreateAsync();
        DocumentPageMapService maps = fixture.CreateMapService();

        Result<DocumentPageMap> replaced =
            await maps.ReplaceFromYamlAsync(fixture.DocumentId, ValidMapYaml(fixture));
        replaced.IsSuccess.Should().BeTrue(replaced.ErrorMessage);

        Result<ResolvedDocumentPageMap?> resolved = await maps.GetResolvedAsync(fixture.DocumentId);
        resolved.IsSuccess.Should().BeTrue(resolved.ErrorMessage);
        resolved.Value.Should().NotBeNull();
        resolved.Value!.HasMap.Should().BeTrue();
        resolved.Value.RuleHealth.Should().ContainSingle().Which.NeedsRepair.Should().BeFalse();

        IReadOnlyList<LogicalRuleHealth> health = resolved.Value.RuleHealth;
        DocumentPageMap definition = resolved.Value.Definition;
        PageMapResolver.ResolveForward(definition, health, new NavigationPosition(1, fixture.BoxA))
            .PrintedNumber.Should().Be(1);
        PageMapResolver.ResolveForward(definition, health, new NavigationPosition(1, fixture.BoxB))
            .PrintedNumber.Should().Be(2);
        PageMapResolver.ResolveForward(definition, health, new NavigationPosition(2, fixture.BoxC))
            .PrintedNumber.Should().Be(3);
        PageMapResolver.ResolveForward(definition, health, new NavigationPosition(3))
            .PrintedNumber.Should().Be(100);

        ResolvedChapterNode chapter = resolved.Value.Chapters.Should().ContainSingle().Subject;
        chapter.ChapterId.Should().Be("ch1");
        chapter.PrintedNumber.Should().Be(1);
        chapter.DisplayLabel.Should().Be("1");
        chapter.Target.Should().Be(new NavigationPosition(1, fixture.BoxA));

        // The map.yml projection decodes back to the stored definition.
        Result<string> yaml = await maps.GetMapYamlAsync(fixture.DocumentId);
        yaml.IsSuccess.Should().BeTrue(yaml.ErrorMessage);
        Result<DocumentPageMap> decoded = DocumentPageMapYamlCodec.Decode(yaml.Value, fixture.DocumentId);
        decoded.IsSuccess.Should().BeTrue(decoded.ErrorMessage);
        decoded.Value.PhysicalRules.Should().BeEquivalentTo(definition.PhysicalRules);
        decoded.Value.LogicalRules.Should().BeEquivalentTo(definition.LogicalRules);
        decoded.Value.PageLabels.Should().BeEquivalentTo(definition.PageLabels);
        decoded.Value.Chapters.Should().BeEquivalentTo(definition.Chapters);
    }

    [Fact]
    public async Task Put_empty_definition_clears_the_map()
    {
        await using PageMapTestDocument fixture = await PageMapTestDocument.CreateAsync();
        DocumentPageMapService maps = fixture.CreateMapService();

        (await maps.ReplaceFromYamlAsync(fixture.DocumentId, ValidMapYaml(fixture))).IsSuccess.Should().BeTrue();
        (await maps.GetResolvedAsync(fixture.DocumentId)).Value.Should().NotBeNull();

        Result<DocumentPageMap> cleared = await maps.ReplaceFromYamlAsync(
            fixture.DocumentId, DocumentPageMapYamlCodec.EncodeEmpty(fixture.DocumentId));
        cleared.IsSuccess.Should().BeTrue(cleared.ErrorMessage);
        cleared.Value.IsEmpty.Should().BeTrue();

        Result<ResolvedDocumentPageMap?> resolved = await maps.GetResolvedAsync(fixture.DocumentId);
        resolved.IsSuccess.Should().BeTrue();
        resolved.Value.Should().BeNull();

        // The empty-map projection stays discoverable and first-put-able.
        Result<string> yaml = await maps.GetMapYamlAsync(fixture.DocumentId);
        yaml.IsSuccess.Should().BeTrue();
        Result<DocumentPageMap> decoded = DocumentPageMapYamlCodec.Decode(yaml.Value, fixture.DocumentId);
        decoded.IsSuccess.Should().BeTrue(decoded.ErrorMessage);
        decoded.Value.IsEmpty.Should().BeTrue();
        yaml.Value.Should().Contain("\"map_present\": false");
    }

    [Fact]
    public async Task Get_returns_null_and_empty_projection_for_document_without_map()
    {
        await using PageMapTestDocument fixture = await PageMapTestDocument.CreateAsync();
        DocumentPageMapService maps = fixture.CreateMapService();

        Result<ResolvedDocumentPageMap?> resolved = await maps.GetResolvedAsync(fixture.DocumentId);
        resolved.IsSuccess.Should().BeTrue();
        resolved.Value.Should().BeNull();

        Result<string> yaml = await maps.GetMapYamlAsync(fixture.DocumentId);
        yaml.IsSuccess.Should().BeTrue(yaml.ErrorMessage);
        yaml.Value.Should().Contain("\"profile\": \"patchouli-page-map-1\"");
    }

    [Fact]
    public async Task Put_with_illegal_range_shape_fails_and_keeps_original_map()
    {
        await using PageMapTestDocument fixture = await PageMapTestDocument.CreateAsync();
        DocumentPageMapService maps = fixture.CreateMapService();
        (await maps.ReplaceFromYamlAsync(fixture.DocumentId, ValidMapYaml(fixture))).IsSuccess.Should().BeTrue();

        string badYaml =
            "profile: patchouli-page-map-1\n" +
            $"document_id: {fixture.DocumentId}\n" +
            "map_present: true\n" +
            "pagination:\n" +
            "  rules:\n" +
            "    - kind: physical_page\n" +
            "      start: 2\n" +
            "      end: 1\n" +
            "      printed_start: 5\n" +
            "  page_labels: []\n" +
            "chapters: []\n";

        Result<DocumentPageMap> replaced = await maps.ReplaceFromYamlAsync(fixture.DocumentId, badYaml);
        replaced.IsFailure.Should().BeTrue();
        replaced.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);

        Result<ResolvedDocumentPageMap?> resolved = await GetResolvedAsync(fixture);
        resolved.Value.Should().NotBeNull();
        resolved.Value!.Definition.PhysicalRules.Should()
            .BeEquivalentTo([new PhysicalPaginationRule(3, 3, 100)]);
        resolved.Value.Definition.LogicalRules.Should().HaveCount(1);
    }

    [Fact]
    public async Task Put_with_duplicate_chapter_id_fails_and_keeps_original_map()
    {
        await using PageMapTestDocument fixture = await PageMapTestDocument.CreateAsync();
        DocumentPageMapService maps = fixture.CreateMapService();
        (await maps.ReplaceFromYamlAsync(fixture.DocumentId, ValidMapYaml(fixture))).IsSuccess.Should().BeTrue();

        string badYaml =
            "profile: patchouli-page-map-1\n" +
            $"document_id: {fixture.DocumentId}\n" +
            "map_present: true\n" +
            "pagination:\n" +
            "  rules: []\n" +
            "  page_labels: []\n" +
            "chapters:\n" +
            "  - id: dup\n" +
            "    title: A\n" +
            "    target: {pdf_page: 1}\n" +
            "    children: []\n" +
            "  - id: dup\n" +
            "    title: B\n" +
            "    target: {pdf_page: 2}\n" +
            "    children: []\n";

        Result<DocumentPageMap> replaced = await maps.ReplaceFromYamlAsync(fixture.DocumentId, badYaml);
        replaced.IsFailure.Should().BeTrue();
        replaced.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
        replaced.ErrorMessage.Should().Contain("Duplicate chapter id");

        (await GetResolvedAsync(fixture)).Value!.Definition.Chapters.Should()
            .BeEquivalentTo([ChapterNode.Leaf("ch1", "第一章", new NavigationPosition(1, fixture.BoxA))]);
    }

    [Fact]
    public async Task Put_with_range_beyond_page_count_fails_and_keeps_original_map()
    {
        await using PageMapTestDocument fixture = await PageMapTestDocument.CreateAsync();
        DocumentPageMapService maps = fixture.CreateMapService();
        (await maps.ReplaceFromYamlAsync(fixture.DocumentId, ValidMapYaml(fixture))).IsSuccess.Should().BeTrue();

        string badYaml =
            "profile: patchouli-page-map-1\n" +
            $"document_id: {fixture.DocumentId}\n" +
            "map_present: true\n" +
            "pagination:\n" +
            "  rules:\n" +
            "    - kind: physical_page\n" +
            "      start: 1\n" +
            "      end: 99\n" +
            "      printed_start: 1\n" +
            "  page_labels: []\n" +
            "chapters: []\n";

        Result<DocumentPageMap> replaced = await maps.ReplaceFromYamlAsync(fixture.DocumentId, badYaml);
        replaced.IsFailure.Should().BeTrue();
        replaced.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
        replaced.ErrorMessage.Should().Contain("page count");

        (await GetResolvedAsync(fixture)).Value!.Definition.PhysicalRules.Should()
            .BeEquivalentTo([new PhysicalPaginationRule(3, 3, 100)]);
    }

    [Fact]
    public async Task Put_with_cross_document_box_fails_and_keeps_original_map()
    {
        await using PageMapTestDocument fixture = await PageMapTestDocument.CreateAsync();
        DocumentPageMapService maps = fixture.CreateMapService();
        (await maps.ReplaceFromYamlAsync(fixture.DocumentId, ValidMapYaml(fixture))).IsSuccess.Should().BeTrue();

        (_, DocumentBoxId otherBox) = await fixture.AddSecondDocumentAsync();
        string badYaml =
            "profile: patchouli-page-map-1\n" +
            $"document_id: {fixture.DocumentId}\n" +
            "map_present: true\n" +
            "pagination:\n" +
            "  rules:\n" +
            "    - kind: logical_page\n" +
            "      start: 1\n" +
            "      end: 1\n" +
            "      printed_start: 1\n" +
            "      reading_order:\n" +
            $"        - box: {otherBox}\n" +
            "          pdf_page: 1\n" +
            "  page_labels: []\n" +
            "chapters: []\n";

        Result<DocumentPageMap> replaced = await maps.ReplaceFromYamlAsync(fixture.DocumentId, badYaml);
        replaced.IsFailure.Should().BeTrue();
        replaced.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
        replaced.ErrorMessage.Should().Contain("was not found in the current committed revisions");

        (await GetResolvedAsync(fixture)).Value!.Definition.LogicalRules.Should().HaveCount(1);
    }

    [Fact]
    public async Task Put_with_non_logical_box_fails_and_keeps_original_map()
    {
        await using PageMapTestDocument fixture = await PageMapTestDocument.CreateAsync();
        DocumentPageMapService maps = fixture.CreateMapService();
        (await maps.ReplaceFromYamlAsync(fixture.DocumentId, ValidMapYaml(fixture))).IsSuccess.Should().BeTrue();

        string badYaml =
            "profile: patchouli-page-map-1\n" +
            $"document_id: {fixture.DocumentId}\n" +
            "map_present: true\n" +
            "pagination:\n" +
            "  rules:\n" +
            "    - kind: logical_page\n" +
            "      start: 1\n" +
            "      end: 1\n" +
            "      printed_start: 1\n" +
            "      reading_order:\n" +
            $"        - box: {fixture.TextBox}\n" +
            "          pdf_page: 1\n" +
            "  page_labels: []\n" +
            "chapters: []\n";

        Result<DocumentPageMap> replaced = await maps.ReplaceFromYamlAsync(fixture.DocumentId, badYaml);
        replaced.IsFailure.Should().BeTrue();
        replaced.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
        replaced.ErrorMessage.Should().Contain("not a logical_page box");

        (await GetResolvedAsync(fixture)).Value!.Definition.LogicalRules.Should().HaveCount(1);
    }

    [Fact]
    public async Task Put_with_malformed_yaml_fails_gracefully_and_keeps_original_map()
    {
        await using PageMapTestDocument fixture = await PageMapTestDocument.CreateAsync();
        DocumentPageMapService maps = fixture.CreateMapService();
        (await maps.ReplaceFromYamlAsync(fixture.DocumentId, ValidMapYaml(fixture))).IsSuccess.Should().BeTrue();

        // Syntactically broken YAML: the parse failure must surface as a failed Result,
        // never as an escaped exception, and must not touch the stored map.
        string malformed =
            "profile: patchouli-page-map-1\n" +
            $"document_id: {fixture.DocumentId}\n" +
            "pagination: [unclosed\n";

        Result<DocumentPageMap> replaced = await maps.ReplaceFromYamlAsync(fixture.DocumentId, malformed);
        replaced.IsFailure.Should().BeTrue("malformed YAML must fail as a Result, not throw");

        (await GetResolvedAsync(fixture)).Value.Should().NotBeNull();
    }

    [Fact]
    public async Task Put_for_unknown_document_fails()
    {
        await using PageMapTestDocument fixture = await PageMapTestDocument.CreateAsync();
        DocumentPageMapService maps = fixture.CreateMapService();
        DocumentInstanceId unknown = DocumentInstanceId.New();

        Result<DocumentPageMap> replaced = await maps.ReplaceFromYamlAsync(
            unknown, ValidMapYamlFor(fixture, unknown));

        replaced.IsFailure.Should().BeTrue();
    }

    private sealed class FkRow
    {
        public string Table { get; set; } = "";
        public string OnDelete { get; set; } = "";
    }
}
