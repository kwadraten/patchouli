using System.Xml.Linq;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Bibliography.Biblatex;
using Patchouli.Infrastructure.Csl;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Documents.Translations;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Infrastructure.Search;
using Patchouli.Mcp;

namespace Patchouli.Tests;

public sealed class ContinuationSourcePageResolutionTests
{
    [Fact]
    public async Task Translation_xml_keeps_local_fallback_and_translated_payloads_with_cross_page_identity()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        DocumentBoxId source = DocumentBoxId.New();
        DocumentBoxId pageOneFallback = DocumentBoxId.New();
        DocumentBoxId samePageSource = DocumentBoxId.New();
        DocumentBoxId samePageFollower = DocumentBoxId.New();
        DocumentBoxId crossPageFollower = DocumentBoxId.New();
        await fixture.CommitAsync(fixture.Pages[0], Seed(source, 0, "Page one source"),
            Seed(pageOneFallback, 1, "Page one fallback"));
        await fixture.CommitAsync(fixture.Pages[1], Seed(samePageSource, 0, "Page two source"),
            Seed(samePageFollower, 1, "Page two same-page follower", samePageSource),
            Seed(crossPageFollower, 2, "Page two cross-page follower"));

        Result<PageEditSession> editResult =
            await fixture.Trees.BeginPageEditAsync(fixture.DocumentId, fixture.Pages[1]);
        editResult.IsSuccess.Should().BeTrue(editResult.ErrorMessage);
        Result link = await fixture.Trees.SetContinuationAsync(editResult.Value.SessionId, crossPageFollower, source);
        link.IsSuccess.Should().BeTrue(link.ErrorMessage);
        Result<DocumentTreeRevision> commit = await fixture.Trees.CommitPageEditAsync(editResult.Value.SessionId);
        commit.IsSuccess.Should().BeTrue(commit.ErrorMessage);

        await fixture.TranslateBoxAsync(fixture.Pages[0], 1, source, "Translated page one");
        await fixture.TranslateBoxAsync(fixture.Pages[1], 2, crossPageFollower, "Translated page two");
        Result<McpPageXmlResponse> response = await fixture.Read.GetPageXmlAsync(
            new McpPageXmlRequest(fixture.DocumentId, fixture.Pages[1], 2, true));
        response.IsSuccess.Should().BeTrue(response.ErrorMessage);
        response.Value.TranslationVersion.Should().BeGreaterThan(0);
        XElement page = XDocument.Parse(response.Value.Xml).Root!;
        XElement translated = PageXml(response.Value.Xml, crossPageFollower);
        translated.Attribute("translated")!.Value.Should().Be("true");
        translated.Element("payload")!.Value.Should().Be("Translated page two");
        translated.Attribute("continues_from_page")!.Value.Should().Be("1");
        PageXml(response.Value.Xml, samePageFollower).Attribute("continues_from_page")!.Value.Should().Be("2");
        XElement fallback = PageXml(response.Value.Xml, samePageFollower);
        fallback.Attribute("translated")!.Value.Should().Be("false");
        fallback.Element("payload")!.Value.Should().Be("Page two same-page follower");
        page.Descendants("box").Should().NotContain(box => (string?)box.Attribute("id") == pageOneFallback.ToString());
        Result<McpPageXmlResponse> pageOneResponse = await fixture.Read.GetPageXmlAsync(
            new McpPageXmlRequest(fixture.DocumentId, fixture.Pages[0], 1, true));
        pageOneResponse.IsSuccess.Should().BeTrue(pageOneResponse.ErrorMessage);
        PageXml(pageOneResponse.Value.Xml, source).Element("payload")!.Value.Should().Be("Translated page one");
        XElement pageOneSourceFallback = PageXml(pageOneResponse.Value.Xml, pageOneFallback);
        pageOneSourceFallback.Attribute("translated")!.Value.Should().Be("false");
        pageOneSourceFallback.Element("payload")!.Value.Should().Be("Page one fallback");

        McpCommandResult<McpFetchMeta, McpFetchResult> ranged = await fixture.Commands.FetchAsync(
            new McpFetchRequest([McpResourceUris.TranslationPageUri(fixture.DocumentId, 2)], "boxes:3-3", null));
        ranged.IsSuccess.Should().BeTrue(ranged.Error?.Detail);
        XElement rangedFollower =
            XDocument.Parse(ranged.Envelope!.Entries.Single().Content!).Descendants("box").Single();
        rangedFollower.Attribute("id")!.Value.Should().Be(crossPageFollower.ToString());
        rangedFollower.Attribute("continues_from_page")!.Value.Should().Be("1");
        rangedFollower.Attribute("translated")!.Value.Should().Be("true");
        rangedFollower.Element("payload")!.Value.Should().Be("Translated page two");
    }

    [Fact]
    public async Task Complete_commit_ancestry_resolves_unchanged_source_page()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        DocumentBoxId source = DocumentBoxId.New();
        DocumentBoxId follower = DocumentBoxId.New();
        await fixture.CommitAsync(fixture.Pages[0], Seed(source, 0, "source"));
        await fixture.CommitAsync(fixture.Pages[1], Seed(follower, 0, "follower"));

        await fixture.EditTextAsync(fixture.Pages[0], source, "source in commit history");
        await fixture.EditTextAsync(fixture.Pages[1], follower, "follower in commit history");
        Result<PageEditSession> editResult =
            await fixture.Trees.BeginPageEditAsync(fixture.DocumentId, fixture.Pages[1]);
        editResult.IsSuccess.Should().BeTrue(editResult.ErrorMessage);
        Result link = await fixture.Trees.SetContinuationAsync(editResult.Value.SessionId, follower, source);
        link.IsSuccess.Should().BeTrue(link.ErrorMessage);
        Result<DocumentTreeRevision> commit = await fixture.Trees.CommitPageEditAsync(editResult.Value.SessionId);
        commit.IsSuccess.Should().BeTrue(commit.ErrorMessage);

        XElement followerXml = PageXml(await fixture.XmlAsync(fixture.Pages[1], 1), follower);
        followerXml.Attribute("continues_from_page")!.Value.Should().Be("1");
    }

    [Fact]
    public async Task Reused_box_id_on_another_page_does_not_guess_continuation_page()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        DocumentBoxId reusedId = DocumentBoxId.New();
        DocumentBoxId follower = DocumentBoxId.New();
        await fixture.CommitAsync(fixture.Pages[0], Seed(reusedId, 0, "original source"));
        await fixture.CommitAsync(fixture.Pages[1], Seed(follower, 0, "follower"));
        Result<PageEditSession> editResult =
            await fixture.Trees.BeginPageEditAsync(fixture.DocumentId, fixture.Pages[1]);
        editResult.IsSuccess.Should().BeTrue(editResult.ErrorMessage);
        Result link = await fixture.Trees.SetContinuationAsync(editResult.Value.SessionId, follower, reusedId);
        link.IsSuccess.Should().BeTrue(link.ErrorMessage);
        Result<DocumentTreeRevision> commit = await fixture.Trees.CommitPageEditAsync(editResult.Value.SessionId);
        commit.IsSuccess.Should().BeTrue(commit.ErrorMessage);
        await fixture.InsertHistoricalBoxAsync(fixture.Pages[1], reusedId);

        XElement followerXml = PageXml(await fixture.XmlAsync(fixture.Pages[1], 1), follower);
        followerXml.Attribute("continues_from").Should().NotBeNull();
        followerXml.Attribute("continues_from_page").Should().BeNull();
    }

    [Fact]
    public async Task Current_and_historical_xml_resolve_source_page_from_partial_commit_snapshot()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        DocumentBoxId source = DocumentBoxId.New();
        DocumentBoxId samePageSource = DocumentBoxId.New();
        DocumentBoxId samePageFollower = DocumentBoxId.New();
        DocumentBoxId crossPageFollower = DocumentBoxId.New();
        await fixture.CommitAsync(fixture.Pages[0], Seed(source, 0, "source"));
        await fixture.CommitAsync(fixture.Pages[1],
            Seed(samePageSource, 0, "same page source"),
            Seed(samePageFollower, 1, "same page follower", samePageSource),
            Seed(crossPageFollower, 2, "cross page follower"));

        Result<PageEditSession> editResult =
            await fixture.Trees.BeginPageEditAsync(fixture.DocumentId, fixture.Pages[1]);
        editResult.IsSuccess.Should().BeTrue(editResult.ErrorMessage);
        PageEditSession edit = editResult.Value;
        Result link = await fixture.Trees.SetContinuationAsync(edit.SessionId, crossPageFollower, source);
        link.IsSuccess.Should().BeTrue(link.ErrorMessage);
        Result<DocumentTreeRevision> linkedCommit = await fixture.Trees.CommitPageEditAsync(edit.SessionId);
        linkedCommit.IsSuccess.Should().BeTrue(linkedCommit.ErrorMessage);
        Result<DocumentTreeRevision> linkedPageResult =
            await fixture.Trees.GetCurrentRevisionAsync(fixture.DocumentId, fixture.Pages[1]);
        linkedPageResult.IsSuccess.Should().BeTrue(linkedPageResult.ErrorMessage);
        DocumentTreeRevision linkedPageTwo = linkedPageResult.Value;

        string currentXml = await fixture.XmlAsync(fixture.Pages[1], 1);
        PageXml(currentXml, samePageFollower).Attribute("continues_from_page")!.Value.Should().Be("2");
        PageXml(currentXml, crossPageFollower).Attribute("continues_from_page")!.Value.Should().Be("1");
        string historicalXml = await fixture.XmlAsync(fixture.Pages[1], 1, linkedPageTwo.TreeRevisionId);
        PageXml(historicalXml, crossPageFollower).Attribute("continues_from_page")!.Value.Should().Be("1");

        // A later page commit changes the source's contents but retains its stable box ID.
        Result<PageEditSession> sourceEditResult =
            await fixture.Trees.BeginPageEditAsync(fixture.DocumentId, fixture.Pages[0]);
        sourceEditResult.IsSuccess.Should().BeTrue(sourceEditResult.ErrorMessage);
        PageEditSession sourceEdit = sourceEditResult.Value;
        Result updateSource = await fixture.Trees.UpdateLeafAsync(sourceEdit.SessionId,
            new UpdateLeafCommand(source, DocumentBoxType.Text, new TextBoxPayload("changed")));
        updateSource.IsSuccess.Should().BeTrue(updateSource.ErrorMessage);
        Result<DocumentTreeRevision> sourceCommit = await fixture.Trees.CommitPageEditAsync(sourceEdit.SessionId);
        sourceCommit.IsSuccess.Should().BeTrue(sourceCommit.ErrorMessage);
        PageXml(await fixture.XmlAsync(fixture.Pages[1], 1), crossPageFollower)
            .Attribute("continues_from_page")!.Value.Should().Be("1");

        // Remove the current relation, then delete the source. The pinned old revision still
        // resolves through its own commit ancestry, not the now-missing HEAD box.
        Result<PageEditSession> unlinkResult =
            await fixture.Trees.BeginPageEditAsync(fixture.DocumentId, fixture.Pages[1]);
        unlinkResult.IsSuccess.Should().BeTrue(unlinkResult.ErrorMessage);
        Result unlinkRelation =
            await fixture.Trees.SetContinuationAsync(unlinkResult.Value.SessionId, crossPageFollower, null);
        unlinkRelation.IsSuccess.Should().BeTrue(unlinkRelation.ErrorMessage);
        Result<DocumentTreeRevision> unlinkCommit =
            await fixture.Trees.CommitPageEditAsync(unlinkResult.Value.SessionId);
        unlinkCommit.IsSuccess.Should().BeTrue(unlinkCommit.ErrorMessage);
        Result<PageEditSession> deleteResult =
            await fixture.Trees.BeginPageEditAsync(fixture.DocumentId, fixture.Pages[0]);
        deleteResult.IsSuccess.Should().BeTrue(deleteResult.ErrorMessage);
        Result deleteSource = await fixture.Trees.DeleteBoxAsync(deleteResult.Value.SessionId, source);
        deleteSource.IsSuccess.Should().BeTrue(deleteSource.ErrorMessage);
        Result<DocumentTreeRevision> deleteCommit =
            await fixture.Trees.CommitPageEditAsync(deleteResult.Value.SessionId);
        deleteCommit.IsSuccess.Should().BeTrue(deleteCommit.ErrorMessage);

        PageXml(await fixture.XmlAsync(fixture.Pages[1], 1, linkedPageTwo.TreeRevisionId), crossPageFollower)
            .Attribute("continues_from_page")!.Value.Should().Be("1");
    }

    private static XElement PageXml(string xml, DocumentBoxId boxId)
    {
        return XDocument.Parse(xml).Descendants("box")
            .Single(element => (string?)element.Attribute("id") == boxId.ToString());
    }

    private static DocumentBoxSeed Seed(DocumentBoxId id, int order, string text, DocumentBoxId? from = null)
    {
        return new DocumentBoxSeed(id, null, order, DocumentBoxType.Text, null,
            new NormalizedBBox(.1, .1 + order * .1, .8, .08),
            new TextBoxPayload(text), ContinuesFromBoxId: from);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(TemporarySqliteDatabase database, DocumentInstanceId documentId, PageId[] pages,
            DocumentTreeService trees, PageTranslationService translations, McpReadApi read,
            McpCommandService commands)
        {
            Database = database;
            DocumentId = documentId;
            Pages = pages;
            Trees = trees;
            Translations = translations;
            Read = read;
            Commands = commands;
        }

        private TemporarySqliteDatabase Database { get; }
        public DocumentInstanceId DocumentId { get; }
        public PageId[] Pages { get; }
        public DocumentTreeService Trees { get; }
        public PageTranslationService Translations { get; }
        public McpReadApi Read { get; }
        public McpCommandService Commands { get; }

        public static async Task<Fixture> CreateAsync()
        {
            TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            FixedClock clock = new(DateTimeOffset.Parse("2026-07-13T00:00:00Z"));
            LibraryIdentityService libraries = new(database.ConnectionFactory, clock);
            Result<LibraryMetadata> library = await libraries.CreateLibraryAsync("Continuation source page tests");
            library.IsSuccess.Should().BeTrue(library.ErrorMessage);
            ItemService items = new(database.ConnectionFactory, libraries, clock);
            Result<ItemMetadata> itemResult = await items.CreateItemAsync("document", "Continuation source page");
            itemResult.IsSuccess.Should().BeTrue(itemResult.ErrorMessage);
            Result<DocumentInstance> documentResult =
                await new DocumentInstanceService(database.ConnectionFactory, clock)
                    .AttachDocumentInstanceAsync(itemResult.Value.ItemId, null, DocumentInstanceType.PrimaryScan);
            documentResult.IsSuccess.Should().BeTrue(documentResult.ErrorMessage);
            DocumentInstance document = documentResult.Value;
            Infrastructure.Layout.PageService pages = new(database.ConnectionFactory, clock);
            Result<Page> firstPage = await pages.CreatePageAsync(document.DocumentInstanceId, 0, "1", null, null, 0,
                CoordinateBasis.NormalizedPage, null, null, "test", null);
            firstPage.IsSuccess.Should().BeTrue(firstPage.ErrorMessage);
            Result<Page> secondPage = await pages.CreatePageAsync(document.DocumentInstanceId, 1, "2", null, null, 0,
                CoordinateBasis.NormalizedPage, null, null, "test", null);
            secondPage.IsSuccess.Should().BeTrue(secondPage.ErrorMessage);
            Page[] created = [firstPage.Value, secondPage.Value];
            MarkdigMarkdownEngine markdown = new();
            DocumentTreeService trees = new(database.ConnectionFactory, clock, markdown);
            DocumentMarkdownCompiler markdownCompiler = new(trees, markdown);
            PageTranslationCache cache = new();
            PageTranslationService translations = new(database.ConnectionFactory, trees, markdownCompiler, markdown,
                new CachedPageTranslationCompiler(
                    new PageTranslationCompiler(database.ConnectionFactory, trees, markdown), cache), cache, clock);
            McpReadApi read = new(database.ConnectionFactory, new SqliteSearchService(database.ConnectionFactory),
                markdown: markdown, markdownCompiler: markdownCompiler, pageTranslations: translations);
            CslStyleStore styles = new(database.ConnectionFactory, clock);
            DocumentInstanceService documentService = new(database.ConnectionFactory, clock);
            BiblatexHelperClient biblatexHelper = new();
            McpWriteApi writes = new(items, biblatexHelper, styles, translations);
            BiblatexImportService biblatex = new(biblatexHelper, items,
                new FileAssetService(database.ConnectionFactory, libraries, clock), documentService);
            VersionedEvidenceReader evidence = new(database.ConnectionFactory, libraries, trees, markdownCompiler);
            McpCommandService commands = new(read, writes, biblatex, items, evidence);
            return new Fixture(database, document.DocumentInstanceId, created.Select(page => page.PageId).ToArray(),
                trees, translations, read, commands);
        }

        public async Task<DocumentTreeRevision> CommitAsync(PageId pageId, params DocumentBoxSeed[] boxes)
        {
            Result<DocumentTreeRevision> workingResult = await Trees.BeginWorkingRevisionAsync(
                DocumentId, pageId, boxes, DocumentTreeRevisionSource.Import);
            workingResult.IsSuccess.Should().BeTrue(workingResult.ErrorMessage);
            Result<DocumentTreeRevision> committed =
                await Trees.CommitWorkingRevisionAsync(workingResult.Value.TreeRevisionId);
            committed.IsSuccess.Should().BeTrue(committed.ErrorMessage);
            return committed.Value;
        }

        public async Task EditTextAsync(PageId pageId, DocumentBoxId boxId, string text)
        {
            Result<PageEditSession> editResult = await Trees.BeginPageEditAsync(DocumentId, pageId);
            editResult.IsSuccess.Should().BeTrue(editResult.ErrorMessage);
            Result update = await Trees.UpdateLeafAsync(editResult.Value.SessionId,
                new UpdateLeafCommand(boxId, DocumentBoxType.Text, new TextBoxPayload(text)));
            update.IsSuccess.Should().BeTrue(update.ErrorMessage);
            Result<DocumentTreeRevision> committed = await Trees.CommitPageEditAsync(editResult.Value.SessionId);
            committed.IsSuccess.Should().BeTrue(committed.ErrorMessage);
        }

        public async Task InsertHistoricalBoxAsync(PageId pageId, DocumentBoxId boxId)
        {
            string revisionId = Guid.NewGuid().ToString();
            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                """
                insert into document_tree_revisions
                    (tree_revision_id, document_instance_id, page_id, source, status, is_current,
                     source_basis_status, created_at, committed_at)
                values (@RevisionId, @DocumentId, @PageId, 'migration', 'committed', 0,
                        'current', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
                insert into document_boxes
                    (tree_revision_id, box_id, document_instance_id, page_id, box_type,
                     bbox_x, bbox_y, bbox_width, bbox_height, payload_json, suppressed)
                values (@RevisionId, @BoxId, @DocumentId, @PageId, 'text',
                        0.1, 0.1, 0.8, 0.1, '{"markdown":"reused identity"}', 0);
                """,
                new
                {
                    RevisionId = revisionId,
                    BoxId = boxId.ToString(),
                    DocumentId = DocumentId.ToString(),
                    PageId = pageId.ToString()
                });
        }

        public async Task TranslateBoxAsync(PageId pageId, int pageIndex, DocumentBoxId boxId, string translation)
        {
            Result<PageXmlTranslation> pageResult = await Translations.GetPageXmlAsync(DocumentId, pageId, pageIndex);
            pageResult.IsSuccess.Should().BeTrue(pageResult.ErrorMessage);
            XDocument xml = XDocument.Parse(pageResult.Value.Xml);
            XElement box = xml.Descendants("box").Single(element =>
                (string?)element.Attribute("id") == boxId.ToString());
            box.SetAttributeValue("translated", "true");
            box.Element("payload")!.Value = translation;
            Result<PageTranslationStatus> put = await Translations.PutPageXmlAsync(
                DocumentId, pageId, pageIndex, xml.ToString(SaveOptions.DisableFormatting));
            put.IsSuccess.Should().BeTrue(put.ErrorMessage);
        }

        public async Task<string> XmlAsync(PageId pageId, int index, DocumentTreeRevisionId? revision = null)
        {
            McpReadApi api = new(Database.ConnectionFactory, new SqliteSearchService(Database.ConnectionFactory));
            Result<McpPageXmlResponse> response = await api.GetPageXmlAsync(new McpPageXmlRequest(DocumentId, pageId,
                index, Revision: revision));
            response.IsSuccess.Should().BeTrue(response.ErrorMessage);
            return response.Value.Xml;
        }

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }
}
