using Dapper;
using FluentAssertions;
using LiteratureApp.Core.Documents;
using LiteratureApp.Core.Import;
using LiteratureApp.Core.Time;
using LiteratureApp.Infrastructure.Bibliography;
using LiteratureApp.Infrastructure.Documents;
using LiteratureApp.Infrastructure.Files;
using LiteratureApp.Infrastructure.Layout;
using LiteratureApp.Infrastructure.LibraryIdentity;
using LiteratureApp.Infrastructure.Migrations;
using LiteratureApp.Infrastructure.Workflows;

namespace LiteratureApp.Tests;

public sealed class PdfImportWorkflowTests
{
    private const string RealPdfPath =
        @"C:\Users\squaresum\WPSDrive\615704893\WPS云盘\扫描或下载的内容（不区分时段）\在村兰学.pdf";

    [Fact]
    public async Task ImportPdf_imports_real_user_pdf_and_creates_pages()
    {
        if (!File.Exists(RealPdfPath))
            return;

        await using var context = await CreateContextAsync();
        var workflow = context.CreateWorkflow(new PdfMetadataReader());

        var result = await workflow.ImportPdfAsync(new PdfImportRequest(
            RealPdfPath, "在村兰学", "测试作者", null));

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.CreatedItemId.Should().NotBeNullOrWhiteSpace();
        result.CreatedFileAssetId.Should().NotBeNullOrWhiteSpace();
        result.CreatedDocumentInstanceId.Should().NotBeNullOrWhiteSpace();

        await using var connection = context.Database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        var pageCount = await connection.ExecuteScalarAsync<int>(
            "select count(1) from pages where document_instance_id = @Id;",
            new { Id = result.CreatedDocumentInstanceId });
        pageCount.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task ImportPdf_fails_when_page_count_unavailable()
    {
        await using var context = await CreateContextAsync();
        var workflow = context.CreateWorkflow(new MissingPdfMetadataReader());
        var pdf = Path.Combine(Path.GetTempPath(), $"pdf-{Guid.NewGuid():N}.pdf");
        await File.WriteAllTextAsync(pdf, "%PDF-1.4");

        try
        {
            var result = await workflow.ImportPdfAsync(new PdfImportRequest(pdf, "Bad", null, null));

            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Contain("page count");
        }
        finally
        {
            File.Delete(pdf);
        }
    }

    private static async Task<ImportContext> CreateContextAsync()
    {
        var db = TemporarySqliteDatabase.Create();
        var clock = new FixedClock(DateTimeOffset.Parse("2026-06-20T00:00:00Z"));
        await new MigrationRunner(db.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        var library = new LibraryIdentityService(db.ConnectionFactory, clock);
        await library.CreateLibraryAsync("Test Library");
        return new ImportContext(db, clock, library);
    }

    private sealed class ImportContext : IAsyncDisposable
    {
        public TemporarySqliteDatabase Database { get; }
        private IClock Clock { get; }
        private LibraryIdentityService Library { get; }

        public ImportContext(TemporarySqliteDatabase database, IClock clock, LibraryIdentityService library)
        {
            Database = database;
            Clock = clock;
            Library = library;
        }

        public PdfImportWorkflow CreateWorkflow(IPdfMetadataReader metadataReader)
        {
            return new PdfImportWorkflow(
                new FileAssetService(Database.ConnectionFactory, Library, Clock),
                new ItemService(Database.ConnectionFactory, Library, Clock),
                new DocumentInstanceService(Database.ConnectionFactory, Clock),
                new PageService(Database.ConnectionFactory, Clock),
                metadataReader,
                Clock);
        }

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }

    private sealed class MissingPdfMetadataReader : IPdfMetadataReader
    {
        public Task<int?> GetPageCountAsync(string pdfPath, CancellationToken cancellationToken = default) =>
            Task.FromResult<int?>(null);
    }
}
