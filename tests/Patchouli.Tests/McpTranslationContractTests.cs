using Dapper;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Bibliography.Biblatex;
using Patchouli.Infrastructure.Csl;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Documents.Translations;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Search;
using Patchouli.Mcp;

namespace Patchouli.Tests;

/// <summary>
/// Contract tests for the writable <c>patchouli://translations/</c> resource tree. They compose
/// the real MCP command service over the real translation services so browse projections, fetch
/// status metadata, and structure-validated puts are exercised end to end.
/// </summary>
public sealed class McpTranslationContractTests
{
    [Fact]
    public async Task Translations_scope_browses_documents_with_progress_projection()
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        McpCommandResult<McpFindMeta, object> result = await fixture.Commands.FindAsync(
            new McpFindRequest(null, "patchouli://translations/", null));

        result.IsSuccess.Should().BeTrue($"error: {result.Error?.Code} {result.Error?.Detail}");
        result.Envelope!.Meta.ShownTotal.Should().Be(1);
        McpTranslationDocumentEntry entry = result.Envelope.Entries.OfType<McpTranslationDocumentEntry>()
            .Should().ContainSingle().Subject;
        entry.Uri.Should().Be(McpResourceUris.TranslationDocumentUri(fixture.DocumentId));
        entry.Type.Should().Be("directory");
        entry.PageCount.Should().Be(2);
        entry.UntranslatedPageCount.Should().Be(2);
        entry.TranslatedPageCount.Should().Be(0);
    }

    [Fact]
    public async Task Translation_document_projects_each_page_status()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.PutTranslatedFirstPageAsync();

        McpCommandResult<McpFindMeta, object> result = await fixture.Commands.FindAsync(
            new McpFindRequest(null, McpResourceUris.TranslationDocumentUri(fixture.DocumentId), null));

        result.IsSuccess.Should().BeTrue($"error: {result.Error?.Code} {result.Error?.Detail}");
        McpTranslationPageEntry[] pages = result.Envelope!.Entries.OfType<McpTranslationPageEntry>().ToArray();
        pages.Should().HaveCount(2);
        McpTranslationPageEntry first = pages.Single(page =>
            page.Uri == McpResourceUris.TranslationPageUri(fixture.DocumentId, 1));
        first.TranslationStatus.Should().Be(McpTranslationStatus.Translated);
        first.TranslatedBoxCount.Should().Be(2);
        first.TotalBoxCount.Should().Be(2);
        McpTranslationPageEntry second = pages.Single(page =>
            page.Uri == McpResourceUris.TranslationPageUri(fixture.DocumentId, 2));
        second.TranslationStatus.Should().Be(McpTranslationStatus.Untranslated);
        second.TranslatedBoxCount.Should().Be(0);
    }

    [Fact]
    public async Task Fetch_translation_returns_compiled_markdown_with_status_metadata()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.PutTranslatedFirstPageAsync();

        McpCommandResult<McpFetchMeta, McpFetchResult> result = await fixture.Commands.FetchAsync(
            new McpFetchRequest([McpResourceUris.TranslationPageUri(fixture.DocumentId, 1)], null, null));

        result.IsSuccess.Should().BeTrue($"error: {result.Error?.Code} {result.Error?.Detail}");
        McpFetchResult entry = result.Envelope!.Entries.Should().ContainSingle().Subject;
        entry.ResourceType.Should().Be("translation_page");
        entry.Content.Should().Contain("译文 Alpha").And.Contain("译文 Beta");
        entry.Translation.Should().NotBeNull();
        entry.Translation!.TranslatedBoxCount.Should().Be(2);
        entry.Translation.TotalBoxCount.Should().Be(2);
        entry.Translation.StaleBoxIds.Should().BeEmpty();
        entry.Translation.IsCurrent.Should().BeTrue();
    }

    [Fact]
    public async Task Fetch_untranslated_page_is_not_found_and_points_at_the_source_text()
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        McpCommandResult<McpFetchMeta, McpFetchResult> result = await fixture.Commands.FetchAsync(
            new McpFetchRequest([McpResourceUris.TranslationPageUri(fixture.DocumentId, 2)], null, null));

        result.IsSuccess.Should().BeFalse();
        McpFetchResult entry = result.Envelope!.Entries.Should().ContainSingle().Subject;
        entry.Error.Should().NotBeNull();
        entry.Error.Should().Contain("NOT_FOUND");
        entry.Error.Should().Contain($"patchouli://texts/{fixture.DocumentId}/page-2.md");
        entry.Translation.Should().BeNull();
    }

    [Fact]
    public async Task Put_translation_replaces_the_whole_page_and_reports_committed()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        string uri = McpResourceUris.TranslationPageUri(fixture.DocumentId, 1);

        McpCommandResult<McpPutMeta, McpPutResult> first = await fixture.Commands.PutAsync(
            new McpPutRequest(uri, fixture.TranslateFirstPage("第一版")));

        first.IsSuccess.Should().BeTrue($"error: {first.Error?.Code} {first.Error?.Detail}");
        McpPutResult put = first.Envelope!.Entries.Should().ContainSingle().Subject;
        put.Committed.Should().BeTrue();
        put.ResourceType.Should().Be("translation_page");
        put.TranslationErrors.Should().BeNull();

        McpCommandResult<McpPutMeta, McpPutResult> second = await fixture.Commands.PutAsync(
            new McpPutRequest(uri, fixture.TranslateFirstPage("第二版")));
        second.IsSuccess.Should().BeTrue($"error: {second.Error?.Code} {second.Error?.Detail}");

        McpCommandResult<McpFetchMeta, McpFetchResult> fetched = await fixture.Commands.FetchAsync(
            new McpFetchRequest([uri], null, null));
        fetched.Envelope!.Entries.Single().Content.Should().Contain("第二版").And.NotContain("第一版");
    }

    [Fact]
    public async Task Put_translation_with_a_structure_mismatch_reports_errors_and_commits_nothing()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        string uri = McpResourceUris.TranslationPageUri(fixture.DocumentId, 1);
        string mismatched = fixture.TranslateFirstPage("译文") + "\n\n多余的段落。";

        McpCommandResult<McpPutMeta, McpPutResult> result = await fixture.Commands.PutAsync(
            new McpPutRequest(uri, mismatched));

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be((int)McpErrorCode.InvalidContent);
        McpPutResult entry = result.Envelope!.Entries.Should().ContainSingle().Subject;
        entry.Committed.Should().BeFalse();
        entry.TranslationErrors.Should().NotBeNullOrEmpty();
        entry.TranslationErrors!.Should().Contain(error => error.Expected == "end of markdown");
        McpCommandResult<McpFetchMeta, McpFetchResult> fetched = await fixture.Commands.FetchAsync(
            new McpFetchRequest([uri], null, null));
        fetched.IsSuccess.Should().BeFalse("a rejected put must not persist any translation rows");
    }

    [Fact]
    public async Task Put_on_a_translation_directory_is_permission_denied()
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        McpCommandResult<McpPutMeta, McpPutResult> document = await fixture.Commands.PutAsync(
            new McpPutRequest(McpResourceUris.TranslationDocumentUri(fixture.DocumentId), "x"));
        document.Error!.Code.Should().Be((int)McpErrorCode.PermissionDenied);

        McpCommandResult<McpPutMeta, McpPutResult> scope = await fixture.Commands.PutAsync(
            new McpPutRequest(McpResourceUris.TranslationsScopeUri(), "x"));
        scope.Error!.Code.Should().Be((int)McpErrorCode.PermissionDenied);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TemporarySqliteDatabase _database;

        private Fixture(
            TemporarySqliteDatabase database,
            DocumentInstanceId documentId,
            PageId translatedPageId,
            PageId untranslatedPageId,
            string firstPageSource,
            PageTranslationService translations,
            McpCommandService commands)
        {
            _database = database;
            DocumentId = documentId;
            TranslatedPageId = translatedPageId;
            UntranslatedPageId = untranslatedPageId;
            FirstPageSource = firstPageSource;
            Translations = translations;
            Commands = commands;
        }

        public DocumentInstanceId DocumentId { get; }
        public PageId TranslatedPageId { get; }
        public PageId UntranslatedPageId { get; }
        public string FirstPageSource { get; }
        public PageTranslationService Translations { get; }
        public McpCommandService Commands { get; }

        public static async Task<Fixture> CreateAsync()
        {
            TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
            FixedClock clock = new(DateTimeOffset.Parse("2026-07-13T00:00:00Z"));
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            LibraryIdentityService library = new(database.ConnectionFactory, clock);
            await library.CreateLibraryAsync("Translation MCP");
            ItemService items = new(database.ConnectionFactory, library, clock);
            ItemMetadata item = (await items.CreateItemAsync("book", "Translated work")).Value;
            DocumentInstanceService documents = new(database.ConnectionFactory, clock);
            DocumentInstance document = (await documents.AttachDocumentInstanceAsync(
                item.ItemId, null, DocumentInstanceType.PrimaryScan, "Translated work", true)).Value;
            PageService pages = new(database.ConnectionFactory, clock);
            Page first = (await pages.CreatePageAsync(document.DocumentInstanceId, 0, "1", null, null, 0,
                CoordinateBasis.NormalizedPage, null, null, "test", null)).Value;
            Page second = (await pages.CreatePageAsync(document.DocumentInstanceId, 1, "2", null, null, 0,
                CoordinateBasis.NormalizedPage, null, null, "test", null)).Value;

            MarkdigMarkdownEngine markdown = new();
            DocumentTreeService trees = new(database.ConnectionFactory, clock, markdown);
            await CommitAsync(trees, document.DocumentInstanceId, first.PageId,
                new TextBoxSeed(0, "Alpha text"),
                new TextBoxSeed(1, "Beta text"));
            await CommitAsync(trees, document.DocumentInstanceId, second.PageId,
                new TextBoxSeed(0, "Gamma text"));

            DocumentMarkdownCompiler markdownCompiler = new(trees, markdown);
            DocumentTreeRevision firstRevision = (await trees.GetCurrentRevisionAsync(
                document.DocumentInstanceId, first.PageId)).Value;
            string firstSource = (await markdownCompiler.CompilePageMarkdownAsync(
                firstRevision.TreeRevisionId)).Value.Markdown;

            PageTranslationCache cache = new();
            PageTranslationService translations = new(
                database.ConnectionFactory,
                trees,
                markdownCompiler,
                markdown,
                new CachedPageTranslationCompiler(
                    new PageTranslationCompiler(database.ConnectionFactory, trees, markdown), cache),
                cache,
                clock);

            SqliteSearchService search = new(database.ConnectionFactory);
            CslStyleStore cslStore = new(database.ConnectionFactory, clock);
            McpReadApi read = new(database.ConnectionFactory, search, markdown: markdown,
                markdownCompiler: markdownCompiler, pageTranslations: translations);
            McpWriteApi writes = new(items, new BiblatexHelperClient(), cslStore, translations);
            BiblatexImportService biblatex = new(new BiblatexHelperClient(), items,
                new FileAssetService(database.ConnectionFactory, library, clock), documents);
            IVersionedEvidenceReader evidenceReader = new VersionedEvidenceReader(
                database.ConnectionFactory, library, trees, markdownCompiler);
            McpCommandService commands = new(read, writes, biblatex, items, evidenceReader);
            return new Fixture(database, document.DocumentInstanceId, first.PageId, second.PageId,
                firstSource, translations, commands);
        }

        public string TranslateFirstPage(string marker)
        {
            return FirstPageSource
                .Replace("Alpha text", $"{marker} Alpha")
                .Replace("Beta text", $"{marker} Beta");
        }

        public async Task PutTranslatedFirstPageAsync()
        {
            Result<PageTranslationStatus> put = await Translations.PutPageTranslationAsync(
                DocumentId, TranslatedPageId, TranslateFirstPage("译文"));
            put.IsSuccess.Should().BeTrue(put.ErrorMessage);
        }

        private static async Task CommitAsync(
            DocumentTreeService trees,
            DocumentInstanceId documentId,
            PageId pageId,
            params TextBoxSeed[] seeds)
        {
            Result<DocumentTreeRevision> working = await trees.BeginWorkingRevisionAsync(
                documentId, pageId,
                seeds.Select(seed => new DocumentBoxSeed(
                        DocumentBoxId.New(), null, seed.Order, DocumentBoxType.Text, null, null,
                        new NormalizedBBox(.1, .1 + seed.Order * .1, .8, .05), new TextBoxPayload(seed.Text)))
                    .ToArray(),
                DocumentTreeRevisionSource.Import);
            Result<DocumentTreeRevision> committed = await trees.CommitWorkingRevisionAsync(
                working.Value.TreeRevisionId);
            committed.IsSuccess.Should().BeTrue(committed.ErrorMessage);
        }

        public ValueTask DisposeAsync()
        {
            return _database.DisposeAsync();
        }

        private sealed record TextBoxSeed(int Order, string Text);
    }
}
