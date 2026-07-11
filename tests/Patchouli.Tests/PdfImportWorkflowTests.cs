using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Import;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Workflows;

namespace Patchouli.Tests;

public sealed class PdfImportWorkflowTests
{
    [Fact]
    public async Task ImportPdf_imports_real_fixture_pdf_and_creates_all_pages()
    {
        await using ImportContext context = await CreateContextAsync();
        PdfImportWorkflow workflow = context.CreateWorkflow(new PdfMetadataReader());
        string pdf = Path.Combine(Path.GetTempPath(), $"pdf-import-{Guid.NewGuid():N}.pdf");
        File.Copy(TestFixtures.RealThreePagePdf, pdf);

        try
        {
            PdfImportResult result = await workflow.ImportPdfAsync(new PdfImportRequest(
                pdf, "Real Fixture", "测试作者", null));

            result.Success.Should().BeTrue(result.ErrorMessage);
            result.CreatedItemId.Should().NotBeNullOrWhiteSpace();
            result.CreatedFileAssetId.Should().NotBeNullOrWhiteSpace();
            result.CreatedDocumentInstanceId.Should().NotBeNullOrWhiteSpace();

            await using SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            int pageCount = await connection.ExecuteScalarAsync<int>(
                "select count(1) from pages where document_instance_id = @Id;",
                new { Id = result.CreatedDocumentInstanceId });
            string? itemType = await connection.ExecuteScalarAsync<string>(
                "select item_type from items where item_id = @Id;",
                new { Id = result.CreatedItemId });
            pageCount.Should().Be(3);
            itemType.Should().Be("general");
        }
        finally
        {
            File.Delete(pdf);
        }
    }

    [Fact]
    public async Task ImportPdf_fails_when_page_count_unavailable()
    {
        await using ImportContext context = await CreateContextAsync();
        PdfImportWorkflow workflow = context.CreateWorkflow(new MissingPdfMetadataReader());
        string pdf = Path.Combine(Path.GetTempPath(), $"pdf-{Guid.NewGuid():N}.pdf");
        File.Copy(TestFixtures.RealThreePagePdf, pdf);

        try
        {
            PdfImportResult result = await workflow.ImportPdfAsync(new PdfImportRequest(pdf, "Bad", null, null));

            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Contain("page count");
        }
        finally
        {
            File.Delete(pdf);
        }
    }

    [Fact]
    public async Task ImportPdf_creates_type_inference_suggestion_from_filename_when_confident()
    {
        await using ImportContext context = await CreateContextAsync();
        PdfImportWorkflow workflow = context.CreateWorkflow(new PdfMetadataReader());
        string pdf = Path.Combine(Path.GetTempPath(), $"thesis-import-{Guid.NewGuid():N}.pdf");
        File.Copy(TestFixtures.RealThreePagePdf, pdf);

        try
        {
            PdfImportResult result = await workflow.ImportPdfAsync(new PdfImportRequest(
                pdf, "Thesis Fixture", null, null));

            result.Success.Should().BeTrue(result.ErrorMessage);
            Result<IReadOnlyList<ItemTypeInference>> suggestions =
                await context.ItemTypeInference.ListSuggestionsAsync(Core.Ids.ItemId.Parse(result.CreatedItemId!));
            suggestions.IsSuccess.Should().BeTrue();
            suggestions.Value.Should().ContainSingle();
            suggestions.Value.Single().SuggestedType.Should().Be("thesis");
            suggestions.Value.Single().Confidence.Should().BeGreaterThan(0.9);
        }
        finally
        {
            File.Delete(pdf);
        }
    }

    private static async Task<ImportContext> CreateContextAsync()
    {
        TemporarySqliteDatabase db = TemporarySqliteDatabase.Create();
        FixedClock clock = new(DateTimeOffset.Parse("2026-06-20T00:00:00Z"));
        await new MigrationRunner(db.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        LibraryIdentityService library = new(db.ConnectionFactory, clock);
        await library.CreateLibraryAsync("Test Library");
        return new ImportContext(db, clock, library);
    }

    private sealed class ImportContext : IAsyncDisposable
    {
        public TemporarySqliteDatabase Database { get; }
        private IClock Clock { get; }
        private LibraryIdentityService Library { get; }
        public ItemTypeInferenceService ItemTypeInference { get; }

        public ImportContext(TemporarySqliteDatabase database, IClock clock, LibraryIdentityService library)
        {
            Database = database;
            Clock = clock;
            Library = library;
            ItemTypeInference = new ItemTypeInferenceService(
                database.ConnectionFactory,
                clock,
                new CslItemTypeProfileService(),
                new ItemService(database.ConnectionFactory, library, clock));
        }

        public PdfImportWorkflow CreateWorkflow(IPdfMetadataReader metadataReader)
        {
            return new PdfImportWorkflow(
                new FileAssetService(Database.ConnectionFactory, Library, Clock),
                new ItemService(Database.ConnectionFactory, Library, Clock),
                new DocumentInstanceService(Database.ConnectionFactory, Clock),
                new PageService(Database.ConnectionFactory, Clock),
                metadataReader,
                Clock,
                ItemTypeInference);
        }

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }

    private sealed class MissingPdfMetadataReader : IPdfMetadataReader
    {
        public Task<int?> GetPageCountAsync(string pdfPath, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<int?>(null);
        }
    }
}
