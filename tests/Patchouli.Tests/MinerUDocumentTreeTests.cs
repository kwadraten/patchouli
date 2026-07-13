using System.IO.Compression;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Infrastructure.Ocr.MinerU;
using Patchouli.Ocr.MinerU;

namespace Patchouli.Tests;

public sealed class MinerUDocumentTreeTests
{
    [Fact]
    public async Task Importer_stages_typed_boxes_suppresses_auxiliary_and_builds_gfm_table()
    {
        await using Context context = await Context.CreateAsync();
        string zip = CreateZip("_content_list_v2.json", """
                                                        [
                                                          {"type":"title","level":2,"page_idx":0,"text":"Heading","bbox":[0,0,500,100]},
                                                          {"type":"header","page_idx":0,"text":"Running head","bbox":[0,110,500,150]},
                                                          {"type":"table","page_idx":0,"bbox":[0,200,800,500],"cells":[
                                                            {"row_index":0,"col_index":0,"text":"Name"},
                                                            {"row_index":0,"col_index":1,"text":"Value"},
                                                            {"row_index":1,"col_index":0,"text":"Pages"},
                                                            {"row_index":1,"col_index":1,"text":"12"}
                                                          ]}
                                                        ]
                                                        """);

        try
        {
            MinerUResultImporter importer = new(context.Database.ConnectionFactory, context.Clock,
                new OcrDocumentTreeImporter(context.Trees));
            Result<MinerUImportResult> result = await importer.ImportResultZipAsync(
                new MinerUImportRequest(zip, context.DocumentId.ToString(), context.LibraryId.ToString()));

            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            result.Value.BoxesCreated.Should().Be(3);
            IReadOnlyList<DocumentBox> boxes = (await context.Trees.ListBoxesAsync(
                DocumentTreeRevisionId.Parse(result.Value.StagingTreeRevisionIds.Single()))).Value;
            boxes.Should().Contain(box => box.BoxType == DocumentBoxType.Title && box.HeadingLevel == 2);
            boxes.Should().Contain(box => box.BoxType == DocumentBoxType.Header && box.Suppressed);
            boxes.Single(box => box.BoxType == DocumentBoxType.Table).Payload.Should().BeOfType<TableBoxPayload>()
                .Which.Markdown.Should().Contain("| Name | Value |").And.Contain("| Pages | 12 |");
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public async Task Importer_rejects_full_markdown_without_tree_artifact()
    {
        await using Context context = await Context.CreateAsync();
        string zip = CreateZip("full.md", "# Not a tree");
        try
        {
            Result<MinerUImportResult> result = await new MinerUResultImporter(
                    context.Database.ConnectionFactory, context.Clock,
                    new OcrDocumentTreeImporter(context.Trees))
                .ImportResultZipAsync(new MinerUImportRequest(
                    zip, context.DocumentId.ToString(), context.LibraryId.ToString()));
            result.IsFailure.Should().BeTrue();
            result.ErrorCode.Should().Be("tree_artifact_required");
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public async Task Irregular_table_becomes_placeholder_with_diagnostic()
    {
        await using Context context = await Context.CreateAsync();
        string zip = CreateZip("_content_list.json", """
                                                     [{"type":"table","page_idx":0,"bbox":[0,0,800,500],"cells":[
                                                       {"row_index":0,"col_index":0,"row_span":2,"text":"Merged"}
                                                     ]}]
                                                     """);
        try
        {
            Result<MinerUImportResult> result = await new MinerUResultImporter(
                    context.Database.ConnectionFactory, context.Clock,
                    new OcrDocumentTreeImporter(context.Trees))
                .ImportResultZipAsync(new MinerUImportRequest(
                    zip, context.DocumentId.ToString(), context.LibraryId.ToString()));
            result.Value.Warnings.Should().Contain("table_not_representable_as_gfm");
            IReadOnlyList<DocumentBox> boxes = (await context.Trees.ListBoxesAsync(
                DocumentTreeRevisionId.Parse(result.Value.StagingTreeRevisionIds.Single()))).Value;
            boxes.Single().Payload.Should().Be(new TableBoxPayload("[Table]"));
        }
        finally
        {
            File.Delete(zip);
        }
    }

    private static string CreateZip(string entryName, string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"mineru-box-{Guid.NewGuid():N}.zip");
        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using StreamWriter writer = new(archive.CreateEntry(entryName).Open());
        writer.Write(content);
        return path;
    }

    private sealed class Context : IAsyncDisposable
    {
        private Context(TemporarySqliteDatabase database, FixedClock clock, LibraryId libraryId,
            DocumentInstanceId documentId, DocumentTreeService trees)
        {
            Database = database;
            Clock = clock;
            LibraryId = libraryId;
            DocumentId = documentId;
            Trees = trees;
        }

        public TemporarySqliteDatabase Database { get; }
        public FixedClock Clock { get; }
        public LibraryId LibraryId { get; }
        public DocumentInstanceId DocumentId { get; }
        public DocumentTreeService Trees { get; }

        public static async Task<Context> CreateAsync()
        {
            TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
            FixedClock clock = new(DateTimeOffset.Parse("2026-07-13T00:00:00Z"));
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            LibraryIdentityService libraries = new(database.ConnectionFactory, clock);
            LibraryMetadata library = (await libraries.CreateLibraryAsync("MinerU Box Tree")).Value;
            ItemMetadata item = (await new ItemService(database.ConnectionFactory, libraries, clock)
                .CreateItemAsync("document", "MinerU")).Value;
            DocumentInstance document = (await new DocumentInstanceService(database.ConnectionFactory, clock)
                .AttachDocumentInstanceAsync(item.ItemId, null, DocumentInstanceType.PrimaryScan)).Value;
            await new Infrastructure.Layout.PageService(database.ConnectionFactory, clock).CreatePageAsync(
                document.DocumentInstanceId, 0, "1", null, null, 0, CoordinateBasis.NormalizedPage,
                null, null, "test", null);
            DocumentTreeService trees = BoxTreeTestData.CreateService(database.ConnectionFactory, clock);
            return new Context(database, clock, library.LibraryId, document.DocumentInstanceId, trees);
        }

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }
}
