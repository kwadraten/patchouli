using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Reading;
using Patchouli.UI.Reading;

namespace Patchouli.Tests;

public sealed class BookReadingStreamTests
{
    private static readonly DocumentInstanceId DocumentId = DocumentInstanceId.New();

    [Fact]
    public async Task Lists_page_indices_in_ascending_order()
    {
        BookReadingStream stream = CreateStream(CreatePages(3, 0, 2, 1));

        IReadOnlyList<int> indices = await stream.ListPageIndicesAsync(DocumentId);

        indices.Should().Equal(0, 1, 2, 3);
    }

    [Fact]
    public async Task Lists_pages_once_across_mixed_calls()
    {
        Page[] pages = CreatePages(0, 1);
        int listCalls = 0;
        BookReadingStream stream = CreateStream(
            pages,
            listPages: (_, _) =>
            {
                listCalls++;
                return Task.FromResult(Result<IReadOnlyList<Page>>.Success((IReadOnlyList<Page>)pages));
            });

        await stream.ListPageIndicesAsync(DocumentId);
        await stream.LoadPageAsync(DocumentId, 0, 2);
        await stream.LoadPageAsync(DocumentId, 1, 2);

        listCalls.Should().Be(1,
            "LoadPageAsync runs once per page, so the stream caches the page list for the session");
    }

    [Fact]
    public async Task Lists_no_pages_for_a_document_without_pages()
    {
        BookReadingStream stream = CreateStream([]);

        IReadOnlyList<int> indices = await stream.ListPageIndicesAsync(DocumentId);

        indices.Should().BeEmpty();
    }

    [Fact]
    public async Task Lists_no_pages_when_the_page_list_cannot_be_read()
    {
        BookReadingStream stream = CreateStream(
            CreatePages(0, 1),
            listPages: (_, _) => Task.FromResult(
                Result<IReadOnlyList<Page>>.Failure(AppErrorCodes.NotFound, "Document was not found.")));

        IReadOnlyList<int> indices = await stream.ListPageIndicesAsync(DocumentId);

        indices.Should().BeEmpty();
    }

    [Fact]
    public async Task Loads_a_page_compiled_from_its_committed_revision()
    {
        BookReadingStream stream = CreateStream(
            CreatePages(0, 1),
            compile: _ => Result<CompiledMarkdown>.Success(new CompiledMarkdown("识别后的正文", [], [])));

        BookReadingPage page = await stream.LoadPageAsync(DocumentId, 0, 2);

        page.PageIndex.Should().Be(0);
        page.PageCount.Should().Be(2);
        page.IsPrepend.Should().BeFalse("the view model decides prepend ordering, not the stream");
        SceneText(page.Source).Should().Contain("识别后的正文");
        page.Source.Blocks.Should().NotContain(block => block.Text.Contains("本页尚未识别文字。"));
    }

    [Fact]
    public async Task Uses_placeholder_when_a_page_has_no_committed_revision()
    {
        BookReadingStream stream = CreateStream(
            CreatePages(0, 1),
            _ => Result<DocumentTreeRevision>.Failure(AppErrorCodes.NotFound, "No committed revision."));

        BookReadingPage page = await stream.LoadPageAsync(DocumentId, 1, 2);

        SceneText(page.Source).Should().Be("本页尚未识别文字。");
        page.PageIndex.Should().Be(1);
        page.IsPrepend.Should().BeFalse();
    }

    [Fact]
    public async Task Uses_placeholder_when_markdown_compilation_fails()
    {
        BookReadingStream stream = CreateStream(
            CreatePages(0),
            compile: _ => Result<CompiledMarkdown>.Failure(
                AppErrorCodes.DatabaseError, "Compilation failed."));

        BookReadingPage page = await stream.LoadPageAsync(DocumentId, 0, 1);

        SceneText(page.Source).Should().Contain("本页尚未识别文字。");
    }

    [Fact]
    public async Task Uses_placeholder_when_the_page_list_cannot_be_read()
    {
        BookReadingStream stream = CreateStream(
            CreatePages(0),
            listPages: (_, _) => Task.FromResult(
                Result<IReadOnlyList<Page>>.Failure(AppErrorCodes.NotFound, "Document was not found.")));

        BookReadingPage page = await stream.LoadPageAsync(DocumentId, 0, 1);

        SceneText(page.Source).Should().Contain("本页尚未识别文字。");
    }

    [Fact]
    public async Task Loads_the_compiled_translation_beside_the_source()
    {
        DocumentTreeRevisionId revision = DocumentTreeRevisionId.New();
        BookReadingStream stream = CreateStream(
            CreatePages(0, 1),
            getTranslation: (_, _, _) => Task.FromResult<TranslatedPageMarkdown?>(
                new TranslatedPageMarkdown("翻译后的正文", [],
                    new PageTranslationStatus(1, 1, [], revision, true))));

        BookReadingPage page = await stream.LoadPageAsync(DocumentId, 0, 2);

        SceneText(page.Source).Should().Contain("识别后的正文");
        page.Translation.Should().NotBeNull();
        SceneText(page.Translation!).Should().Contain("翻译后的正文");
        page.Translation!.Blocks.Should().NotContain(block => block.Kind == ReadingBlock.UntranslatedKind);
    }

    [Fact]
    public async Task Leaves_translation_null_when_the_page_has_no_translation()
    {
        BookReadingStream stream = CreateStream(
            CreatePages(0, 1),
            getTranslation: (_, _, _) => Task.FromResult<TranslatedPageMarkdown?>(null));

        BookReadingPage page = await stream.LoadPageAsync(DocumentId, 0, 2);

        page.Translation.Should().BeNull();
    }

    [Fact]
    public async Task Leaves_translation_null_when_translations_are_not_supplied()
    {
        BookReadingStream stream = CreateStream(CreatePages(0, 1));

        BookReadingPage page = await stream.LoadPageAsync(DocumentId, 0, 2);

        page.Translation.Should().BeNull();
    }

    [Fact]
    public async Task Leaves_translation_null_for_an_empty_compiled_translation()
    {
        DocumentTreeRevisionId revision = DocumentTreeRevisionId.New();
        BookReadingStream stream = CreateStream(
            CreatePages(0, 1),
            getTranslation: (_, _, _) => Task.FromResult<TranslatedPageMarkdown?>(
                new TranslatedPageMarkdown("   ", [],
                    new PageTranslationStatus(0, 0, [], revision, true))));

        BookReadingPage page = await stream.LoadPageAsync(DocumentId, 0, 2);

        page.Translation.Should().BeNull();
    }

    [Fact]
    public async Task Replaces_stale_translation_blocks_with_untranslated_placeholders()
    {
        DocumentTreeRevisionId revision = DocumentTreeRevisionId.New();
        DocumentBoxId staleBox = DocumentBoxId.New();
        // The translation has one translated block and one stale (untranslated) box; the stale box
        // renders as a 未翻译 placeholder, never as source text.
        MarkdownSourceMapEntry[] map =
        [
            new(staleBox, 0, 3, 0, 1)
        ];
        BookReadingStream stream = CreateStream(
            CreatePages(0, 1),
            compile: _ => Result<CompiledMarkdown>.Success(new CompiledMarkdown("源正文", map, [],
                new MarkdownDocumentModel([new MarkdownBlock("paragraph", "源正文", 0, 3)]))),
            getTranslation: (_, _, _) => Task.FromResult<TranslatedPageMarkdown?>(
                new TranslatedPageMarkdown("源正文", map,
                    new PageTranslationStatus(0, 1, [staleBox], revision, true))));

        BookReadingPage page = await stream.LoadPageAsync(DocumentId, 0, 2);

        page.Translation.Should().NotBeNull();
        page.Translation!.Blocks.Should().ContainSingle()
            .Which.Kind.Should().Be(ReadingBlock.UntranslatedKind);
        SceneText(page.Translation!).Should().Be(ReadingBlock.UntranslatedText);
    }

    [Fact]
    public async Task Throws_without_touching_the_document_when_the_token_is_already_cancelled()
    {
        bool listCalled = false;
        BookReadingStream stream = CreateStream(
            CreatePages(0, 1),
            listPages: (_, _) =>
            {
                listCalled = true;
                return Task.FromResult(Result<IReadOnlyList<Page>>.Success((IReadOnlyList<Page>)CreatePages(0, 1)));
            });
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();
        CancellationToken token = cancellation.Token;

        Func<Task> load = () => stream.LoadPageAsync(DocumentId, 0, 1, token);

        await load.Should().ThrowAsync<OperationCanceledException>();
        listCalled.Should().BeFalse();
    }

    [Fact]
    public async Task Loads_real_compiled_markdown_for_recognized_pages_and_placeholder_for_the_rest()
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        DocumentInstanceId documentId = DocumentInstanceId.New();
        PageId recognized = PageId.New();
        PageId missing = PageId.New();
        FixedClock clock = new(new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero));
        await SeedDocumentAsync(database, documentId, recognized, missing);
        MarkdigMarkdownEngine markdown = new();
        DocumentTreeService trees = new(database.ConnectionFactory, clock, markdown);
        DocumentMarkdownCompiler compiler = new(trees, markdown);
        PageEditSession edit = (await trees.BeginPageEditAsync(documentId, recognized)).Value;
        (await trees.DrawAndInsertLeafAsync(
                edit.SessionId,
                new InsertLeafCommand(
                    null,
                    null,
                    DocumentBoxType.Text,
                    null,
                    null,
                    new NormalizedBBox(0.05, 0.05, 0.9, 0.1),
                    new TextBoxPayload("识别出的正文"))))
            .IsSuccess.Should().BeTrue();
        (await trees.CommitPageEditAsync(edit.SessionId)).IsSuccess.Should().BeTrue();
        BookReadingStream stream = new(
            new PageService(database.ConnectionFactory, clock).ListPagesAsync,
            trees.GetCurrentRevisionAsync,
            compiler.CompilePageMarkdownAsync);

        IReadOnlyList<int> indices = await stream.ListPageIndicesAsync(documentId);
        BookReadingPage recognizedPage = await stream.LoadPageAsync(documentId, 0, indices.Count);
        BookReadingPage missingPage = await stream.LoadPageAsync(documentId, 1, indices.Count);

        indices.Should().Equal(0, 1);
        SceneText(recognizedPage.Source).Should().Contain("识别出的正文");
        SceneText(missingPage.Source).Should().Be("本页尚未识别文字。");
    }

    private static async Task SeedDocumentAsync(
        TemporarySqliteDatabase database,
        DocumentInstanceId documentId,
        params PageId[] pageIds)
    {
        await using SqliteConnection connection = database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        string now = DateTimeOffset.UtcNow.ToString("O");
        await connection.ExecuteAsync(
            """
            insert into library_metadata values (@LibraryId, 'Test', 2, @Now, @Now, 1);
            insert into items (
                item_id, library_id, item_type, title, creators_json, tags_json,
                collections_json, custom_fields_json, created_at, updated_at)
            values (@ItemId, @LibraryId, 'book', 'Test', '[]', '[]', '[]', '{}', @Now, @Now);
            insert into document_instances (
                document_instance_id, item_id, file_asset_id, title, instance_type,
                is_primary, status, created_at, updated_at)
            values (@DocumentId, @ItemId, null, 'Test', 'scan', 1, 'active', @Now, @Now);
            """,
            new
            {
                LibraryId = LibraryId.New().ToString(),
                ItemId = ItemId.New().ToString(),
                DocumentId = documentId.ToString(),
                Now = now
            });
        for (int index = 0; index < pageIds.Length; index++)
        {
            await connection.ExecuteAsync(
                """
                insert into pages (
                    page_id, document_instance_id, page_index, page_label, width, height,
                    rotation, coordinate_basis, basis_width, basis_height,
                    renderer_basis_version, source_file_hash, created_at, updated_at)
                values (@PageId, @DocumentId, @PageIndex, @PageLabel, 100, 100, 0, 'upright_render',
                    100, 100, 'test-v1', null, @Now, @Now);
                """,
                new
                {
                    PageId = pageIds[index].ToString(),
                    DocumentId = documentId.ToString(),
                    PageIndex = index,
                    PageLabel = (index + 1).ToString(),
                    Now = now
                });
        }
    }

    private static Page[] CreatePages(params int[] pageIndices)
    {
        return pageIndices.Select(pageIndex => new Page(
            PageId.New(),
            DocumentId,
            pageIndex,
            (pageIndex + 1).ToString(),
            100,
            100,
            0,
            "upright_render",
            100,
            100,
            "test-v1",
            null,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch)).ToArray();
    }

    private static BookReadingStream CreateStream(
        IReadOnlyList<Page> pages,
        Func<Page, Result<DocumentTreeRevision>>? getRevision = null,
        Func<Page, Result<CompiledMarkdown>>? compile = null,
        Func<DocumentInstanceId, CancellationToken, Task<Result<IReadOnlyList<Page>>>>? listPages = null,
        Func<DocumentInstanceId, PageId, CancellationToken, Task<TranslatedPageMarkdown?>>? getTranslation = null)
    {
        Dictionary<PageId, DocumentTreeRevision> revisions = pages.ToDictionary(
            page => page.PageId,
            page => new DocumentTreeRevision(
                DocumentTreeRevisionId.New(),
                DocumentId,
                page.PageId,
                null,
                DocumentTreeRevisionSource.OcrAdopted,
                DocumentTreeRevisionStatus.Committed,
                true,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch));
        Dictionary<DocumentTreeRevisionId, Page> pagesByRevision = pages.ToDictionary(
            page => revisions[page.PageId].TreeRevisionId,
            page => page);

        return new BookReadingStream(
            listPages ?? ((_, _) => Task.FromResult(Result<IReadOnlyList<Page>>.Success(pages))),
            (_, pageId, _) => Task.FromResult(getRevision is null
                ? Result<DocumentTreeRevision>.Success(revisions[pageId])
                : getRevision(pages.Single(page => page.PageId == pageId))),
            (revisionId, _, _, _) => Task.FromResult(compile is null
                ? Result<CompiledMarkdown>.Success(new CompiledMarkdown("识别后的正文", [], []))
                : compile(pagesByRevision[revisionId])),
            getPageTranslation: getTranslation,
            parseMarkdown: ParseParagraph);
    }

    // Minimal markdown parse for the stream's fallback: one paragraph block per non-empty line.
    private static MarkdownDocumentModel ParseParagraph(string markdown)
    {
        List<MarkdownBlock> blocks = [];
        foreach (string raw in markdown.Split("\n\n"))
        {
            string text = raw.Trim();
            if (text.Length > 0)
            {
                blocks.Add(new MarkdownBlock("paragraph", text, 0, text.Length));
            }
        }

        return new MarkdownDocumentModel(blocks);
    }

    // Concatenated plain text of a scene's blocks, for asserting on rendered content.
    private static string SceneText(ReadingScene scene)
    {
        return string.Join("\n", scene.Blocks.Select(block => block.Text));
    }

    private static bool SceneHasPlaceholder(ReadingScene scene, string text)
    {
        return scene.Blocks.Any(block => block.Text.Contains(text, StringComparison.Ordinal));
    }
}
