using System.IO.Compression;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Ocr.MinerU;
using Patchouli.Ocr;
using Patchouli.Ocr.MinerU;

namespace Patchouli.Tests;

public sealed class MinerUResultImporterTests
{
    [Fact]
    public async Task ImportResultZip_creates_one_current_revision()
    {
        await using ImportTestContext context = await ImportTestContext.CreateAsync();
        string zipPath = CreateContentListZip("""
                                              {
                                                  "pages": [
                                                      {"page_num": 1, "width": 595, "height": 842, "blocks": [
                                                          {"type": "text", "bbox": [0, 0, 100, 50], "text": "Page 1 text"}
                                                      ]}
                                                  ]
                                              }
                                              """);

        try
        {
            MinerUImportRequest request = new(zipPath, context.DocumentInstanceId.ToString(), null);
            Result<MinerUImportResult> result = await context.Importer.ImportResultZipAsync(request);

            result.IsSuccess.Should().BeTrue();
            result.Value.NodesCreated.Should().Be(1);

            await using SqliteConnection conn = context.Database.ConnectionFactory.CreateConnection();
            await conn.OpenAsync();
            string[] revisions =
                (await conn.QueryAsync<string>(
                    "select layout_revision_id from layout_revisions where document_instance_id = @Id;",
                    new { Id = context.DocumentInstanceId.ToString() })).ToArray();
            revisions.Should().HaveCount(1);

            int currentCount = await conn.ExecuteScalarAsync<int>(
                "select count(1) from layout_revisions where document_instance_id = @Id and is_current = 1;",
                new { Id = context.DocumentInstanceId.ToString() });
            currentCount.Should().Be(1);
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task ImportResultZip_handles_missing_optional_files()
    {
        await using ImportTestContext context = await ImportTestContext.CreateAsync();
        string zipPath = Path.Combine(Path.GetTempPath(), $"mineru-empty-{Guid.NewGuid():N}.zip");
        using (ZipArchive archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = archive.CreateEntry("some_other_file.txt");
            using StreamWriter w = new(entry.Open());
            w.Write("not a content list");
        }

        try
        {
            MinerUImportRequest request = new(zipPath, context.DocumentInstanceId.ToString(), null);
            Result<MinerUImportResult> result = await context.Importer.ImportResultZipAsync(request);
            result.IsFailure.Should().BeTrue();
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task ImportResultZip_delegates_layout_persistence_to_common_importer()
    {
        SpyLayoutImporter spy = new();
        await using ImportTestContext context = await ImportTestContext.CreateAsync(layoutImporter: spy);
        string zipPath = CreateContentListZip("""
                                              {
                                                  "pages": [
                                                      {"page_num": 1, "width": 595, "height": 842, "blocks": [
                                                          {"type": "text", "bbox": [0, 0, 100, 50], "text": "Delegated"}
                                                      ]}
                                                  ]
                                              }
                                              """);

        try
        {
            Result<MinerUImportResult> result =
                await context.Importer.ImportResultZipAsync(new MinerUImportRequest(zipPath,
                    context.DocumentInstanceId.ToString(), null));

            result.IsSuccess.Should().BeTrue();
            spy.ImportCalls.Should().Be(1);
            spy.LastImportRequest.Should().NotBeNull();
            spy.LastImportRequest!.RevisionSource.Should().Be(LayoutRevisionSource.Import);
            spy.LastImportRequest.NodeSource.Should().Be(LayoutNodeSource.Import);
            spy.LastImportRequest.Document.TotalBlockCount.Should().Be(1);
            spy.LastImportRequest.Document.Pages[0].Blocks[0].Text.Should().Be("Delegated");
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task ImportResultZip_creates_nodes_for_all_pages()
    {
        await using ImportTestContext context = await ImportTestContext.CreateAsync(3);
        string zipPath = CreateContentListZip("""
                                              {
                                                  "pages": [
                                                      {"page_num": 1, "width": 595, "height": 842, "blocks": [
                                                          {"type": "text", "bbox": [0, 0, 100, 50], "text": "Page 1"}
                                                      ]},
                                                      {"page_num": 2, "width": 595, "height": 842, "blocks": [
                                                          {"type": "text", "bbox": [0, 0, 100, 50], "text": "Page 2"}
                                                      ]},
                                                      {"page_num": 3, "width": 595, "height": 842, "blocks": [
                                                          {"type": "text", "bbox": [0, 0, 100, 50], "text": "Page 3"}
                                                      ]}
                                                  ]
                                              }
                                              """);

        try
        {
            MinerUImportRequest request = new(zipPath, context.DocumentInstanceId.ToString(), null);
            Result<MinerUImportResult> result = await context.Importer.ImportResultZipAsync(request);

            result.IsSuccess.Should().BeTrue();
            result.Value.NodesCreated.Should().Be(3);

            await using SqliteConnection conn = context.Database.ConnectionFactory.CreateConnection();
            await conn.OpenAsync();
            string? revisionId = await conn.ExecuteScalarAsync<string>(
                "select layout_revision_id from layout_revisions where document_instance_id = @Id and is_current = 1;",
                new { Id = context.DocumentInstanceId.ToString() });
            int nodeCount =
                await conn.ExecuteScalarAsync<int>("select count(1) from layout_nodes where revision_id = @Rev;",
                    new { Rev = revisionId });
            nodeCount.Should().Be(3);
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task ImportResultZip_preserves_structured_table_cells()
    {
        await using ImportTestContext context = await ImportTestContext.CreateAsync();
        string zipPath = CreateContentListZip("""
                                              {
                                                  "pages": [
                                                      {"page_num": 1, "width": 600, "height": 800, "blocks": [
                                                          {"type": "table", "bbox": [50, 100, 550, 260], "table_cells": [
                                                              {"row_index": 0, "col_index": 0, "row_span": 1, "col_span": 1, "is_header": true, "text": "Name", "bbox": [50, 100, 300, 140]},
                                                              {"row_index": 0, "col_index": 1, "row_span": 1, "col_span": 1, "is_header": true, "text": "Value", "bbox": [300, 100, 550, 140]},
                                                              {"row_index": 1, "col_index": 0, "row_span": 1, "col_span": 1, "text": "Pages", "bbox": [50, 140, 300, 180]},
                                                              {"row_index": 1, "col_index": 1, "row_span": 1, "col_span": 1, "text": "12", "bbox": [300, 140, 550, 180]}
                                                          ]}
                                                      ]}
                                                  ]
                                              }
                                              """);

        try
        {
            Result<MinerUImportResult> result =
                await context.Importer.ImportResultZipAsync(new MinerUImportRequest(zipPath,
                    context.DocumentInstanceId.ToString(), null));

            result.IsSuccess.Should().BeTrue();
            result.Value.NodesCreated.Should().Be(7);

            await using SqliteConnection conn = context.Database.ConnectionFactory.CreateConnection();
            await conn.OpenAsync();
            int tableCount =
                await conn.ExecuteScalarAsync<int>("select count(1) from layout_nodes where node_type = 'table';");
            int rowCount =
                await conn.ExecuteScalarAsync<int>("select count(1) from layout_nodes where node_type = 'table_row';");
            (int? RowIndex, int? ColIndex, int IsHeader, string? OwnText)[] cellRows =
                (await conn.QueryAsync<(int? RowIndex, int? ColIndex, int IsHeader, string? OwnText)>(
                    "select row_index as RowIndex, col_index as ColIndex, is_header as IsHeader, own_text as OwnText from layout_nodes where node_type = 'table_cell' order by row_index, col_index;"))
                .ToArray();

            tableCount.Should().Be(1);
            rowCount.Should().Be(2);
            cellRows.Select(c => c.OwnText).Should().Equal("Name", "Value", "Pages", "12");
            cellRows.Where(c => c.RowIndex == 0).Should().OnlyContain(c => c.IsHeader == 1);
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task ImportResultZip_derives_table_cells_from_mineru_html_table_body()
    {
        await using ImportTestContext context = await ImportTestContext.CreateAsync();
        string zipPath = CreateContentListZip("""
                                              [
                                                  {"type": "title", "text": "Spans And Grouped Headers", "bbox": [80, 57, 413, 76], "page_idx": 0},
                                                  {"type": "table", "bbox": [84, 101, 914, 225], "page_idx": 0,
                                                   "table_body": "<table><tr><td colspan=\"4\">Quarterly Plan</td></tr><tr><td>Region</td><td colspan=\"2\">Sales</td><td>Risk</td></tr><tr><td rowspan=\"2\">North</td><td>Q1</td><td>120</td><td>Low</td></tr><tr><td>Q2</td><td>135</td><td>Medium</td></tr></table>"}
                                              ]
                                              """);

        try
        {
            Result<MinerUImportResult> result =
                await context.Importer.ImportResultZipAsync(new MinerUImportRequest(zipPath,
                    context.DocumentInstanceId.ToString(), null));

            result.IsSuccess.Should().BeTrue();

            await using SqliteConnection conn = context.Database.ConnectionFactory.CreateConnection();
            await conn.OpenAsync();
            TableCellRow[] cells = (await conn.QueryAsync<TableCellRow>(
                """
                select row_index as RowIndex, col_index as ColIndex, row_span as RowSpan, col_span as ColSpan, is_header as IsHeader, own_text as OwnText
                from layout_nodes
                where node_type = 'table_cell'
                order by row_index, col_index;
                """)).ToArray();

            cells.Select(c => c.OwnText).Should().Equal("Quarterly Plan", "Region", "Sales", "Risk", "North", "Q1",
                "120", "Low", "Q2", "135", "Medium");
            cells[0].ColSpan.Should().Be(4);
            cells.Single(c => c.OwnText == "North").RowSpan.Should().Be(2);
            cells.Where(c => c.RowIndex == 0).Should().OnlyContain(c => c.IsHeader == 1);
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task ImportResultZip_reads_mineru_content_list_v2_page_arrays()
    {
        await using ImportTestContext context = await ImportTestContext.CreateAsync();
        string zipPath = CreateContentListZip("""
                                              [
                                                  [
                                                      {"type": "paragraph", "content": {"paragraph_content": [{"type": "text", "content": "Before"}]}, "bbox": [80, 77, 591, 93]},
                                                      {"type": "table", "content": {"html": "<table><tr><td>Name</td><td>Value</td></tr><tr><td>Pages</td><td>12</td></tr>", "table_type": "simple_table"}, "bbox": [82, 101, 914, 184]}
                                                  ]
                                              ]
                                              """, "sample_content_list_v2.json");

        try
        {
            Result<MinerUImportResult> result =
                await context.Importer.ImportResultZipAsync(new MinerUImportRequest(zipPath,
                    context.DocumentInstanceId.ToString(), null));

            result.IsSuccess.Should().BeTrue();

            await using SqliteConnection conn = context.Database.ConnectionFactory.CreateConnection();
            await conn.OpenAsync();
            int paragraphCount = await conn.ExecuteScalarAsync<int>(
                "select count(1) from layout_nodes where node_type = 'paragraph' and own_text = 'Before';");
            string[] cellTexts =
                (await conn.QueryAsync<string>(
                    "select own_text from layout_nodes where node_type = 'table_cell' order by row_index, col_index;"))
                .ToArray();

            paragraphCount.Should().Be(1);
            cellTexts.Should().Equal("Name", "Value", "Pages", "12");
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    private static string CreateContentListZip(string sampleJson, string entryName = "sample_content_list.json")
    {
        string zipPath = Path.Combine(Path.GetTempPath(), $"mineru-test-{Guid.NewGuid():N}.zip");
        using ZipArchive archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        ZipArchiveEntry entry = archive.CreateEntry(entryName);
        using StreamWriter writer = new(entry.Open());
        writer.Write(sampleJson);
        return zipPath;
    }

    private sealed class TableCellRow
    {
        public int? RowIndex { get; set; }
        public int? ColIndex { get; set; }
        public int? RowSpan { get; set; }
        public int? ColSpan { get; set; }
        public int IsHeader { get; set; }
        public string? OwnText { get; set; }
    }

    private sealed class ImportTestContext : IAsyncDisposable
    {
        public TemporarySqliteDatabase Database { get; }
        public MinerUResultImporter Importer { get; }
        public DocumentInstanceId DocumentInstanceId { get; }

        private ImportTestContext(TemporarySqliteDatabase db, MinerUResultImporter importer, DocumentInstanceId docId)
        {
            Database = db;
            Importer = importer;
            DocumentInstanceId = docId;
        }

        public static async Task<ImportTestContext> CreateAsync(int pageCount = 1,
            IOcrLayoutImporter? layoutImporter = null)
        {
            TemporarySqliteDatabase db = TemporarySqliteDatabase.Create();
            FixedClock clock = new(DateTimeOffset.Parse("2026-06-20T00:00:00Z"));
            await new MigrationRunner(db.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            LibraryIdentityService library = new(db.ConnectionFactory, clock);
            await library.CreateLibraryAsync("Test Lib");
            ItemService items = new(db.ConnectionFactory, library, clock);
            Result<ItemMetadata> item = await items.CreateItemAsync("document", "Test Doc");
            DocumentInstanceService docs = new(db.ConnectionFactory, clock);
            Result<DocumentInstance> doc =
                await docs.AttachDocumentInstanceAsync(item.Value.ItemId, null, DocumentInstanceType.PrimaryScan);
            PageService pages = new(db.ConnectionFactory, clock);
            for (int i = 0; i < pageCount; i++)
            {
                await pages.CreatePageAsync(doc.Value.DocumentInstanceId, i, $"Page {i + 1}", null, null, 0,
                    CoordinateBasis.NormalizedPage, null, null, "test", null);
            }

            MinerUResultImporter importer = new(db.ConnectionFactory, clock, layoutImporter);
            return new ImportTestContext(db, importer, doc.Value.DocumentInstanceId);
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
        }
    }

    private sealed class SpyLayoutImporter : IOcrLayoutImporter
    {
        public int ImportCalls { get; private set; }
        public OcrLayoutImportRequest? LastImportRequest { get; private set; }

        public Task<Result<OcrLayoutImportResult>> ImportRevisionAsync(OcrLayoutImportRequest request,
            CancellationToken cancellationToken = default)
        {
            ImportCalls++;
            LastImportRequest = request;
            return Task.FromResult(Result<OcrLayoutImportResult>.Success(
                new OcrLayoutImportResult(request.RevisionId ?? LayoutRevisionId.New(),
                    request.Document.TotalBlockCount)));
        }

        public Task<Result<OcrLayoutCopyResult>> CopyPagesAsync(OcrLayoutCopyRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<OcrLayoutCopyResult>.Success(new OcrLayoutCopyResult(0)));
        }
    }
}
