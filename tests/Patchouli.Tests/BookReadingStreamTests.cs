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
using Patchouli.UI.Reading;

namespace Patchouli.Tests;

public sealed class BookReadingStreamTests
{
    private static readonly DocumentInstanceId DocumentId = DocumentInstanceId.New();

    [Fact]
    public async Task Streams_from_the_start_page_then_prepends_the_earlier_pages()
    {
        BookReadingStream stream = CreateStream(CreatePages(0, 1, 2, 3, 4));

        List<BookReadingPage> pages = await CollectAsync(stream, 2);

        pages.Select(page => page.PageIndex).Should().Equal(2, 3, 4, 0, 1);
        pages.Select(page => page.IsPrepend).Should().Equal(false, false, false, true, true);
        pages.Should().OnlyContain(page => page.PageCount == 5);
        pages.Should().OnlyContain(page => page.Html.StartsWith("<h2 data-page=", StringComparison.Ordinal));
        pages.Should().OnlyContain(page => page.Html.Contains(
            $"data-page=\"{page.PageIndex + 1}\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Streams_every_page_in_order_when_starting_at_the_first_page()
    {
        BookReadingStream stream = CreateStream(CreatePages(0, 1, 2));

        List<BookReadingPage> pages = await CollectAsync(stream, 0);

        pages.Select(page => page.PageIndex).Should().Equal(0, 1, 2);
        pages.Should().OnlyContain(page => !page.IsPrepend);
    }

    [Theory]
    [InlineData(-3, 0, 1, 2, 3)]
    [InlineData(9, 3, 0, 1, 2)]
    public async Task Clamps_an_out_of_range_start_page_into_the_book(
        int startPageIndex, int expectedFirst, int second, int third, int fourth)
    {
        BookReadingStream stream = CreateStream(CreatePages(0, 1, 2, 3));

        List<BookReadingPage> pages = await CollectAsync(stream, startPageIndex);

        pages.Select(page => page.PageIndex).Should().Equal(expectedFirst, second, third, fourth);
    }

    [Fact]
    public async Task Streams_no_pages_for_a_document_without_pages()
    {
        BookReadingStream stream = CreateStream([]);

        List<BookReadingPage> pages = await CollectAsync(stream, 0);

        pages.Should().BeEmpty();
    }

    [Fact]
    public async Task Streams_no_pages_when_the_page_list_cannot_be_read()
    {
        BookReadingStream stream = CreateStream(
            CreatePages(0, 1),
            listPages: (_, _) => Task.FromResult(
                Result<IReadOnlyList<Page>>.Failure(AppErrorCodes.NotFound, "Document was not found.")));

        List<BookReadingPage> pages = await CollectAsync(stream, 0);

        pages.Should().BeEmpty();
    }

    [Fact]
    public async Task Uses_placeholder_html_when_a_page_has_no_committed_revision()
    {
        BookReadingStream stream = CreateStream(
            CreatePages(0, 1),
            _ => Result<DocumentTreeRevision>.Failure(AppErrorCodes.NotFound, "No committed revision."));

        List<BookReadingPage> pages = await CollectAsync(stream, 0);

        pages.Should().HaveCount(2);
        pages[0].Html.Should().Be("<h2 data-page=\"1\">第 1 页 / 共 2 页</h2>\n<p><i>本页尚未识别文字。</i></p>");
        pages[1].Html.Should().Contain("本页尚未识别文字。");
        pages.Select(page => page.IsPrepend).Should().Equal(false, false);
    }

    [Fact]
    public async Task Uses_placeholder_html_when_markdown_compilation_fails()
    {
        BookReadingStream stream = CreateStream(
            CreatePages(0, 1),
            compile: _ => Result<CompiledMarkdown>.Failure(
                AppErrorCodes.DatabaseError, "Compilation failed."));

        List<BookReadingPage> pages = await CollectAsync(stream, 0);

        pages.Should().HaveCount(2);
        pages.Should().OnlyContain(page =>
            page.Html.Contains("本页尚未识别文字。", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Uses_compiled_markdown_html_when_a_committed_revision_exists()
    {
        BookReadingStream stream = CreateStream(
            CreatePages(0),
            compile: _ => Result<CompiledMarkdown>.Success(new CompiledMarkdown("识别后的正文", [], [])));

        List<BookReadingPage> pages = await CollectAsync(stream, 0);

        pages.Should().ContainSingle();
        pages[0].Html.Should().Contain("<p>识别后的正文</p>");
        pages[0].Html.Should().NotContain("本页尚未识别文字。");
    }

    [Fact]
    public async Task Stops_streaming_when_cancelled_mid_enumeration()
    {
        BookReadingStream stream = CreateStream(CreatePages(0, 1, 2, 3, 4));
        using CancellationTokenSource cancellation = new();
        await using IAsyncEnumerator<BookReadingPage> enumerator =
            stream.StreamPagesAsync(DocumentId, 2, cancellation.Token).GetAsyncEnumerator();

        (await enumerator.MoveNextAsync()).Should().BeTrue();
        BookReadingPage first = enumerator.Current;
        await cancellation.CancelAsync();
        bool cancelled = false;
        try
        {
            await enumerator.MoveNextAsync();
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        cancelled.Should().BeTrue();
        first.PageIndex.Should().Be(2);
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
        Func<Task> enumerate = () => CollectAsync(stream, 0, cancellationToken: token);

        await enumerate.Should().ThrowAsync<OperationCanceledException>();
        listCalled.Should().BeFalse();
    }

    [Fact]
    public async Task Streams_real_compiled_markdown_for_recognized_pages_and_placeholder_for_the_rest()
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

        List<BookReadingPage> pages = await CollectAsync(stream, 0, documentId);

        pages.Select(page => page.PageIndex).Should().Equal(0, 1);
        pages[0].Html.Should().Contain("<p>识别出的正文</p>");
        pages[1].Html.Should().Be("<h2 data-page=\"2\">第 2 页 / 共 2 页</h2>\n<p><i>本页尚未识别文字。</i></p>");
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
        Func<DocumentInstanceId, CancellationToken, Task<Result<IReadOnlyList<Page>>>>? listPages = null)
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
                : compile(pagesByRevision[revisionId])));
    }

    private static async Task<List<BookReadingPage>> CollectAsync(
        IBookReadingStream stream,
        int startPageIndex,
        DocumentInstanceId? documentInstanceId = null,
        CancellationToken cancellationToken = default)
    {
        List<BookReadingPage> pages = [];
        await foreach (BookReadingPage page in stream.StreamPagesAsync(
                           documentInstanceId ?? DocumentId, startPageIndex, cancellationToken))
        {
            pages.Add(page);
        }

        return pages;
    }
}
