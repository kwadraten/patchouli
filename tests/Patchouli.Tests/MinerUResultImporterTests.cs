using System.IO.Compression;
using Dapper;
using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Ocr.MinerU;
using Patchouli.Ocr.MinerU;

namespace Patchouli.Tests;

public sealed class MinerUResultImporterTests
{
    [Fact]
    public async Task ImportResultZip_creates_one_current_revision()
    {
        await using var context = await ImportTestContext.CreateAsync();
        var zipPath = CreateContentListZip(sampleJson: """
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
            var request = new MinerUImportRequest(zipPath, context.DocumentInstanceId.ToString(), null);
            var result = await context.Importer.ImportResultZipAsync(request);

            result.IsSuccess.Should().BeTrue();
            result.Value.NodesCreated.Should().Be(1);

            await using var conn = context.Database.ConnectionFactory.CreateConnection();
            await conn.OpenAsync();
            var revisions = (await conn.QueryAsync<string>("select layout_revision_id from layout_revisions where document_instance_id = @Id;", new { Id = context.DocumentInstanceId.ToString() })).ToArray();
            revisions.Should().HaveCount(1);

            var currentCount = await conn.ExecuteScalarAsync<int>("select count(1) from layout_revisions where document_instance_id = @Id and is_current = 1;", new { Id = context.DocumentInstanceId.ToString() });
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
        await using var context = await ImportTestContext.CreateAsync();
        var zipPath = Path.Combine(Path.GetTempPath(), $"mineru-empty-{Guid.NewGuid():N}.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("some_other_file.txt");
            using var w = new StreamWriter(entry.Open());
            w.Write("not a content list");
        }

        try
        {
            var request = new MinerUImportRequest(zipPath, context.DocumentInstanceId.ToString(), null);
            var result = await context.Importer.ImportResultZipAsync(request);
            result.IsFailure.Should().BeTrue();
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task ImportResultZip_creates_nodes_for_all_pages()
    {
        await using var context = await ImportTestContext.CreateAsync(pageCount: 3);
        var zipPath = CreateContentListZip(sampleJson: """
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
            var request = new MinerUImportRequest(zipPath, context.DocumentInstanceId.ToString(), null);
            var result = await context.Importer.ImportResultZipAsync(request);

            result.IsSuccess.Should().BeTrue();
            result.Value.NodesCreated.Should().Be(3);

            await using var conn = context.Database.ConnectionFactory.CreateConnection();
            await conn.OpenAsync();
            var revisionId = await conn.ExecuteScalarAsync<string>("select layout_revision_id from layout_revisions where document_instance_id = @Id and is_current = 1;", new { Id = context.DocumentInstanceId.ToString() });
            var nodeCount = await conn.ExecuteScalarAsync<int>("select count(1) from layout_nodes where revision_id = @Rev;", new { Rev = revisionId });
            nodeCount.Should().Be(3);
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    private static string CreateContentListZip(string sampleJson)
    {
        var zipPath = Path.Combine(Path.GetTempPath(), $"mineru-test-{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("sample_content_list.json");
        using var writer = new StreamWriter(entry.Open());
        writer.Write(sampleJson);
        return zipPath;
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

        public static async Task<ImportTestContext> CreateAsync(int pageCount = 1)
        {
            var db = TemporarySqliteDatabase.Create();
            var clock = new FixedClock(DateTimeOffset.Parse("2026-06-20T00:00:00Z"));
            await new MigrationRunner(db.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            var library = new LibraryIdentityService(db.ConnectionFactory, clock);
            await library.CreateLibraryAsync("Test Lib");
            var items = new Patchouli.Infrastructure.Bibliography.ItemService(db.ConnectionFactory, library, clock);
            var item = await items.CreateItemAsync("document", "Test Doc");
            var docs = new DocumentInstanceService(db.ConnectionFactory, clock);
            var doc = await docs.AttachDocumentInstanceAsync(item.Value.ItemId, null, DocumentInstanceType.PrimaryScan);
            var pages = new PageService(db.ConnectionFactory, clock);
            for (var i = 0; i < pageCount; i++)
                await pages.CreatePageAsync(doc.Value.DocumentInstanceId, i, $"Page {i + 1}", null, null, 0, CoordinateBasis.NormalizedPage, null, null, "test", null);

            var importer = new MinerUResultImporter(db.ConnectionFactory, clock);
            return new ImportTestContext(db, importer, doc.Value.DocumentInstanceId);
        }

        public async ValueTask DisposeAsync() => await Database.DisposeAsync();
    }
}
