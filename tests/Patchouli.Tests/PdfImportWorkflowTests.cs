using Dapper;
using FluentAssertions;
using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Documents;
using Patchouli.Core.Import;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.Import;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Workflows;

namespace Patchouli.Tests;

public sealed class PdfImportWorkflowTests
{
    [Fact]
    public async Task ImportPdf_reports_import_activity_until_database_results_are_committed()
    {
        await using ImportContext context = await CreateContextAsync();
        using HostActivityTracker tracker = new(TimeSpan.Zero);
        ConcurrentQueue<HostActivityItem> observed = new();
        tracker.Changed += (_, snapshot) =>
        {
            foreach (HostActivityItem item in snapshot.Items)
            {
                observed.Enqueue(item);
            }
        };
        PdfImportWorkflow workflow = context.CreateWorkflow(new PdfMetadataReader(), tracker);
        string pdf = TestFixtures.CopyRealThreePagePdfTo(Path.GetTempPath(), $"activity-{Guid.NewGuid():N}.pdf");

        try
        {
            PdfImportResult result = await workflow.ImportPdfAsync(new PdfImportRequest(pdf, "Activity", null, null));

            result.Success.Should().BeTrue(result.ErrorMessage);
            observed.Should().ContainSingle(item => item.Kind == HostActivityKind.Import);
            tracker.Current.IsBusy.Should().BeFalse();
        }
        finally
        {
            File.Delete(pdf);
        }
    }

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

    [Fact]
    public async Task ImportPdf_imports_with_placeholder_when_failed_pages_are_within_threshold()
    {
        await using ImportContext context = await CreateContextAsync();
        IReadOnlyList<PdfPageInfoResult> pageInfos = PageInfosWithFailures(3, "boom", 1);
        PdfImportWorkflow workflow = context.CreateWorkflow(
            new PdfMetadataReader(),
            pageInfoReader: new StubPdfPageInfoReader(pageInfos),
            maxFailedPageRatio: 0.5);
        string pdf = Path.Combine(Path.GetTempPath(), $"pdf-import-{Guid.NewGuid():N}.pdf");
        File.Copy(TestFixtures.RealThreePagePdf, pdf);

        try
        {
            PdfImportResult result = await workflow.ImportPdfAsync(new PdfImportRequest(pdf, "Partial", null, null));

            result.Success.Should().BeTrue(result.ErrorMessage);
            result.Status.Should().Be("imported_with_page_failures");
            result.PageCount.Should().Be(3);
            result.FailedPageCount.Should().Be(1);
            PdfImportPageFailure failure = result.PageFailures.Should().ContainSingle().Which;
            failure.PageIndex.Should().Be(1);
            failure.ErrorMessage.Should().Contain("boom");

            await using SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            int pageCount = await connection.ExecuteScalarAsync<int>(
                "select count(1) from pages where document_instance_id = @Id;",
                new { Id = result.CreatedDocumentInstanceId });
            pageCount.Should().Be(3);

            string? failedPageId = await connection.ExecuteScalarAsync<string>(
                "select page_id from pages where document_instance_id = @Id and page_index = 1;",
                new { Id = result.CreatedDocumentInstanceId });
            string? payloadJson = await connection.ExecuteScalarAsync<string>(
                "select payload_json from document_boxes where page_id = @PageId and box_type = 'logical_page';",
                new { PageId = failedPageId });
            payloadJson.Should().NotBeNull();
            payloadJson.Should().Contain("Import failed for this page");
            payloadJson.Should().Contain("boom");

            int revisionCount = await connection.ExecuteScalarAsync<int>(
                """
                select count(1) from document_tree_revisions
                where page_id = @PageId and source = 'import' and status = 'committed' and is_current = 1;
                """,
                new { PageId = failedPageId });
            revisionCount.Should().Be(1);

            int boxCount = await connection.ExecuteScalarAsync<int>(
                "select count(1) from document_boxes where document_instance_id = @Id;",
                new { Id = result.CreatedDocumentInstanceId });
            boxCount.Should().Be(1);
        }
        finally
        {
            File.Delete(pdf);
        }
    }

    [Fact]
    public async Task ImportPdf_fails_and_writes_nothing_when_failed_pages_exceed_threshold()
    {
        await using ImportContext context = await CreateContextAsync();
        IReadOnlyList<PdfPageInfoResult> pageInfos = PageInfosWithFailures(3, "boom", 0, 1);
        PdfImportWorkflow workflow = context.CreateWorkflow(
            new PdfMetadataReader(),
            pageInfoReader: new StubPdfPageInfoReader(pageInfos),
            maxFailedPageRatio: 0.2);
        string pdf = Path.Combine(Path.GetTempPath(), $"pdf-import-{Guid.NewGuid():N}.pdf");
        File.Copy(TestFixtures.RealThreePagePdf, pdf);

        try
        {
            PdfImportResult result = await workflow.ImportPdfAsync(new PdfImportRequest(pdf, "Broken", null, null));

            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Contain("2 of 3");
            result.ErrorMessage.Should().Contain("0.667");
            result.ErrorMessage.Should().Contain("0.2");
            result.PageCount.Should().Be(3);
            result.FailedPageCount.Should().Be(2);

            await using SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            int itemCount = await connection.ExecuteScalarAsync<int>("select count(1) from items;");
            int fileAssetCount = await connection.ExecuteScalarAsync<int>("select count(1) from file_assets;");
            int instanceCount = await connection.ExecuteScalarAsync<int>("select count(1) from document_instances;");
            int pageCount = await connection.ExecuteScalarAsync<int>("select count(1) from pages;");
            int revisionCount =
                await connection.ExecuteScalarAsync<int>("select count(1) from document_tree_revisions;");
            int boxCount = await connection.ExecuteScalarAsync<int>("select count(1) from document_boxes;");
            itemCount.Should().Be(0);
            fileAssetCount.Should().Be(0);
            instanceCount.Should().Be(0);
            pageCount.Should().Be(0);
            revisionCount.Should().Be(0);
            boxCount.Should().Be(0);
        }
        finally
        {
            File.Delete(pdf);
        }
    }

    [Fact]
    public async Task ImportPdf_succeeds_when_failure_ratio_equals_threshold()
    {
        await using ImportContext context = await CreateContextAsync();
        IReadOnlyList<PdfPageInfoResult> pageInfos = PageInfosWithFailures(3, "boom", 1);
        PdfImportWorkflow workflow = context.CreateWorkflow(
            new PdfMetadataReader(),
            pageInfoReader: new StubPdfPageInfoReader(pageInfos),
            maxFailedPageRatio: 1.0 / 3.0);
        string pdf = Path.Combine(Path.GetTempPath(), $"pdf-import-{Guid.NewGuid():N}.pdf");
        File.Copy(TestFixtures.RealThreePagePdf, pdf);

        try
        {
            PdfImportResult result = await workflow.ImportPdfAsync(new PdfImportRequest(pdf, "Edge", null, null));

            result.Success.Should().BeTrue(result.ErrorMessage);
            result.Status.Should().Be("imported_with_page_failures");
            result.FailedPageCount.Should().Be(1);
        }
        finally
        {
            File.Delete(pdf);
        }
    }

    [Fact]
    public async Task ImportPdf_degrades_gracefully_when_page_info_reader_is_unavailable()
    {
        await using ImportContext context = await CreateContextAsync();
        PdfImportWorkflow workflow = context.CreateWorkflow(
            new PdfMetadataReader(),
            pageInfoReader: new StubPdfPageInfoReader(null));
        string pdf = Path.Combine(Path.GetTempPath(), $"pdf-import-{Guid.NewGuid():N}.pdf");
        File.Copy(TestFixtures.RealThreePagePdf, pdf);

        try
        {
            PdfImportResult result = await workflow.ImportPdfAsync(new PdfImportRequest(pdf, "Degraded", null, null));

            result.Success.Should().BeTrue(result.ErrorMessage);
            result.Status.Should().Be("imported");
            result.FailedPageCount.Should().Be(0);
            result.PageFailures.Should().BeEmpty();

            await using SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            List<double?> widths = (await connection.QueryAsync<double?>(
                "select width from pages where document_instance_id = @Id order by page_index;",
                new { Id = result.CreatedDocumentInstanceId })).ToList();
            List<double?> heights = (await connection.QueryAsync<double?>(
                "select height from pages where document_instance_id = @Id order by page_index;",
                new { Id = result.CreatedDocumentInstanceId })).ToList();
            widths.Should().HaveCount(3).And.OnlyContain(width => !width.HasValue);
            heights.Should().HaveCount(3).And.OnlyContain(height => !height.HasValue);

            int boxCount = await connection.ExecuteScalarAsync<int>(
                "select count(1) from document_boxes where document_instance_id = @Id;",
                new { Id = result.CreatedDocumentInstanceId });
            boxCount.Should().Be(0);
        }
        finally
        {
            File.Delete(pdf);
        }
    }

    [Fact]
    public async Task ImportPdf_reads_real_page_dimensions_via_pdfium_page_info_reader()
    {
        await using ImportContext context = await CreateContextAsync();
        PdfMetadataReader reader = new();
        PdfImportWorkflow workflow = context.CreateWorkflow(reader, pageInfoReader: reader);
        string pdf = Path.Combine(Path.GetTempPath(), $"pdf-import-{Guid.NewGuid():N}.pdf");
        File.Copy(TestFixtures.RealThreePagePdf, pdf);

        try
        {
            PdfImportResult result = await workflow.ImportPdfAsync(new PdfImportRequest(pdf, "Real Pages", null, null));

            result.Success.Should().BeTrue(result.ErrorMessage);
            result.Status.Should().Be("imported");
            result.FailedPageCount.Should().Be(0);

            await using SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            List<double?> widths = (await connection.QueryAsync<double?>(
                "select width from pages where document_instance_id = @Id order by page_index;",
                new { Id = result.CreatedDocumentInstanceId })).ToList();
            List<double?> heights = (await connection.QueryAsync<double?>(
                "select height from pages where document_instance_id = @Id order by page_index;",
                new { Id = result.CreatedDocumentInstanceId })).ToList();
            widths.Should().HaveCount(3).And.OnlyContain(width => width.HasValue && width.Value > 0);
            heights.Should().HaveCount(3).And.OnlyContain(height => height.HasValue && height.Value > 0);
        }
        finally
        {
            File.Delete(pdf);
        }
    }

    [Fact]
    public async Task ImportPdf_imports_large_page_count_with_stubbed_page_infos()
    {
        const int pageCount = 1000;
        await using ImportContext context = await CreateContextAsync();
        PdfImportWorkflow workflow = context.CreateWorkflow(
            new PdfMetadataReader(),
            pageInfoReader: new StubPdfPageInfoReader(SuccessPageInfos(pageCount)));
        string pdf = Path.Combine(Path.GetTempPath(), $"pdf-import-{Guid.NewGuid():N}.pdf");
        File.Copy(TestFixtures.RealThreePagePdf, pdf);

        try
        {
            PdfImportResult result = await workflow.ImportPdfAsync(
                new PdfImportRequest(pdf, "Large", null, pageCount));

            result.Success.Should().BeTrue(result.ErrorMessage);
            result.Status.Should().Be("imported");
            result.PageCount.Should().Be(pageCount);

            await using SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            int storedPageCount = await connection.ExecuteScalarAsync<int>(
                "select count(1) from pages where document_instance_id = @Id;",
                new { Id = result.CreatedDocumentInstanceId });
            storedPageCount.Should().Be(pageCount);
        }
        finally
        {
            File.Delete(pdf);
        }
    }

    private static IReadOnlyList<PdfPageInfoResult> SuccessPageInfos(int count)
    {
        return Enumerable.Range(0, count)
            .Select(_ => new PdfPageInfoResult(true, new PdfPageInfo(612, 792, 0), null))
            .ToList();
    }

    private static IReadOnlyList<PdfPageInfoResult> PageInfosWithFailures(
        int count,
        string failureReason,
        params int[] failedIndexes)
    {
        HashSet<int> failed = new(failedIndexes);
        return Enumerable.Range(0, count)
            .Select(index => failed.Contains(index)
                ? new PdfPageInfoResult(false, null, failureReason)
                : new PdfPageInfoResult(true, new PdfPageInfo(612, 792, 0), null))
            .ToList();
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

        public PdfImportWorkflow CreateWorkflow(
            IPdfMetadataReader metadataReader,
            IHostActivityTracker? activityTracker = null,
            IPdfPageInfoReader? pageInfoReader = null,
            double? maxFailedPageRatio = null)
        {
            return new PdfImportWorkflow(
                new ImportBatchWriter(Database.ConnectionFactory),
                metadataReader,
                Clock,
                Library,
                itemTypeInferenceService: ItemTypeInference,
                activityTracker: activityTracker,
                pageInfoReader: pageInfoReader,
                maxFailedPageRatio: maxFailedPageRatio);
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

    private sealed class StubPdfPageInfoReader : IPdfPageInfoReader
    {
        private readonly IReadOnlyList<PdfPageInfoResult>? _results;

        public StubPdfPageInfoReader(IReadOnlyList<PdfPageInfoResult>? results)
        {
            _results = results;
        }

        public Task<IReadOnlyList<PdfPageInfoResult>?> GetPageInfosAsync(
            string pdfPath,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_results);
        }
    }
}
