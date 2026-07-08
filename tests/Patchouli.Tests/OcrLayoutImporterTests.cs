using Dapper;
using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Ocr;

namespace Patchouli.Tests;

public sealed class OcrLayoutImporterTests
{
    [Fact]
    public async Task ImportRevisionAsync_creates_current_revision_and_table_metadata()
    {
        await using var context = await OcrLayoutImportContext.CreateAsync(pageCount: 1);

        var result = await context.Importer.ImportRevisionAsync(new OcrLayoutImportRequest(
            context.DocumentInstanceId,
            CreateTableDocument(context.Pages[0]),
            LayoutRevisionSource.Import,
            LayoutNodeSource.Import,
            MakeCurrent: true));

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        await using var connection = context.Database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        var currentRevisionId = await connection.ExecuteScalarAsync<string>(
            "select layout_revision_id from layout_revisions where document_instance_id = @DocumentInstanceId and is_current = 1 limit 1;",
            new { DocumentInstanceId = context.DocumentInstanceId.ToString() });
        var rowCount = await connection.ExecuteScalarAsync<int>(
            "select count(1) from layout_nodes where revision_id = @RevisionId and node_type = @NodeType;",
            new { RevisionId = result.Value.RevisionId.ToString(), NodeType = LayoutNodeType.TableRow });
        var cellTexts = (await connection.QueryAsync<string>(
            "select own_text from layout_nodes where revision_id = @RevisionId and node_type = @NodeType order by row_index, col_index;",
            new { RevisionId = result.Value.RevisionId.ToString(), NodeType = LayoutNodeType.TableCell })).ToArray();
        var headerFlags = (await connection.QueryAsync<int>(
            "select is_header from layout_nodes where revision_id = @RevisionId and node_type = @NodeType order by row_index, col_index;",
            new { RevisionId = result.Value.RevisionId.ToString(), NodeType = LayoutNodeType.TableCell })).ToArray();

        currentRevisionId.Should().Be(result.Value.RevisionId.ToString());
        rowCount.Should().Be(2);
        cellTexts.Should().Equal("Name", "Value", "Pages", "12");
        headerFlags.Take(2).Should().OnlyContain(flag => flag == 1);
    }

    [Fact]
    public async Task CopyPagesAsync_preserves_tree_and_table_metadata()
    {
        await using var context = await OcrLayoutImportContext.CreateAsync(pageCount: 2);
        var source = await context.Importer.ImportRevisionAsync(new OcrLayoutImportRequest(
            context.DocumentInstanceId,
            new OcrLayoutDocument([
                CreateTablePage(context.Pages[0]),
                CreateParagraphPage(context.Pages[1], "Second page")
            ]),
            LayoutRevisionSource.Import,
            LayoutNodeSource.Import));
        source.IsSuccess.Should().BeTrue(source.ErrorMessage);

        var targetRevisionId = LayoutRevisionId.New();
        await using (var connection = context.Database.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                "insert into layout_revisions (layout_revision_id, document_instance_id, parent_revision_id, source, is_current, created_at) values (@RevisionId, @DocumentInstanceId, @ParentRevisionId, @Source, 0, @CreatedAt);",
                new
                {
                    RevisionId = targetRevisionId.ToString(),
                    DocumentInstanceId = context.DocumentInstanceId.ToString(),
                    ParentRevisionId = source.Value.RevisionId.ToString(),
                    Source = LayoutRevisionSource.OcrAdopted,
                    CreatedAt = context.Clock.UtcNow.ToString("O")
                });
        }

        var copy = await context.Importer.CopyPagesAsync(new OcrLayoutCopyRequest(source.Value.RevisionId, targetRevisionId, [context.Pages[0].PageId]));

        copy.IsSuccess.Should().BeTrue(copy.ErrorMessage);
        await using var verify = context.Database.ConnectionFactory.CreateConnection();
        await verify.OpenAsync();
        var orphanCount = await verify.ExecuteScalarAsync<int>(
            """
            select count(1)
            from layout_nodes child
            left join layout_nodes parent
              on parent.node_id = child.parent_node_id
             and parent.revision_id = child.revision_id
            where child.revision_id = @RevisionId
              and child.parent_node_id is not null
              and parent.node_id is null;
            """,
            new { RevisionId = targetRevisionId.ToString() });
        var copiedPages = (await verify.QueryAsync<string>(
            "select distinct page_id from layout_nodes where revision_id = @RevisionId order by page_id;",
            new { RevisionId = targetRevisionId.ToString() })).ToArray();
        var copiedCells = (await verify.QueryAsync<string>(
            "select own_text from layout_nodes where revision_id = @RevisionId and node_type = @NodeType order by row_index, col_index;",
            new { RevisionId = targetRevisionId.ToString(), NodeType = LayoutNodeType.TableCell })).ToArray();

        orphanCount.Should().Be(0);
        copiedPages.Should().Equal(context.Pages[0].PageId.ToString());
        copiedCells.Should().Equal("Name", "Value", "Pages", "12");
    }

    private static OcrLayoutDocument CreateTableDocument(Patchouli.Core.Layout.Page page)
        => new([CreateTablePage(page)]);

    private static OcrLayoutPage CreateTablePage(Patchouli.Core.Layout.Page page)
        => new(
            page.PageId,
            page.PageIndex,
            page.Width,
            page.Height,
            [
                new OcrLayoutBlock(
                    LayoutNodeType.Table,
                    TextPolicy.AggregateChildren,
                    1,
                    Children:
                    [
                        new OcrLayoutBlock(
                            LayoutNodeType.TableRow,
                            TextPolicy.AggregateChildren,
                            2,
                            Children:
                            [
                                new OcrLayoutBlock(LayoutNodeType.TableCell, TextPolicy.Own, 3, Text: "Name", TableCell: new OcrTableCell(0, 0, 1, 1, true)),
                                new OcrLayoutBlock(LayoutNodeType.TableCell, TextPolicy.Own, 4, Text: "Value", TableCell: new OcrTableCell(0, 1, 1, 1, true))
                            ]),
                        new OcrLayoutBlock(
                            LayoutNodeType.TableRow,
                            TextPolicy.AggregateChildren,
                            5,
                            Children:
                            [
                                new OcrLayoutBlock(LayoutNodeType.TableCell, TextPolicy.Own, 6, Text: "Pages", TableCell: new OcrTableCell(1, 0)),
                                new OcrLayoutBlock(LayoutNodeType.TableCell, TextPolicy.Own, 7, Text: "12", TableCell: new OcrTableCell(1, 1))
                            ])
                    ])
            ]);

    private static OcrLayoutPage CreateParagraphPage(Patchouli.Core.Layout.Page page, string text)
        => new(
            page.PageId,
            page.PageIndex,
            page.Width,
            page.Height,
            [
                new OcrLayoutBlock(LayoutNodeType.Paragraph, TextPolicy.Own, 1, Text: text)
            ]);

    private sealed class OcrLayoutImportContext : IAsyncDisposable
    {
        private OcrLayoutImportContext(
            TemporarySqliteDatabase database,
            FixedClock clock,
            OcrLayoutImporter importer,
            DocumentInstanceId documentInstanceId,
            IReadOnlyList<Patchouli.Core.Layout.Page> pages)
        {
            Database = database;
            Clock = clock;
            Importer = importer;
            DocumentInstanceId = documentInstanceId;
            Pages = pages;
        }

        public TemporarySqliteDatabase Database { get; }
        public FixedClock Clock { get; }
        public OcrLayoutImporter Importer { get; }
        public DocumentInstanceId DocumentInstanceId { get; }
        public IReadOnlyList<Patchouli.Core.Layout.Page> Pages { get; }

        public static async Task<OcrLayoutImportContext> CreateAsync(int pageCount)
        {
            var database = TemporarySqliteDatabase.Create();
            var clock = new FixedClock(DateTimeOffset.Parse("2026-06-20T00:00:00Z"));
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            var library = new LibraryIdentityService(database.ConnectionFactory, clock);
            await library.CreateLibraryAsync("OCR layout import");
            var item = await new ItemService(database.ConnectionFactory, library, clock).CreateItemAsync("book", "OCR layout");
            var document = await new DocumentInstanceService(database.ConnectionFactory, clock).AttachDocumentInstanceAsync(item.Value.ItemId, null, DocumentInstanceType.PrimaryScan);
            var pageService = new PageService(database.ConnectionFactory, clock);
            var pages = new List<Patchouli.Core.Layout.Page>();
            for (var index = 0; index < pageCount; index++)
            {
                pages.Add((await pageService.CreatePageAsync(document.Value.DocumentInstanceId, index, $"{index + 1}", 600, 800, 0, CoordinateBasis.NormalizedPage, 600, 800, "test", null)).Value);
            }

            return new OcrLayoutImportContext(database, clock, new OcrLayoutImporter(database.ConnectionFactory, clock), document.Value.DocumentInstanceId, pages);
        }

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }
}
