using Dapper;
using FluentAssertions;
using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportPdf_skips_same_content_before_reading_pdf_metadata(bool renamed)
    {
        await using ImportContext context = await CreateContextAsync();
        string pdf = TestFixtures.CopyRealThreePagePdfTo(Path.GetTempPath(), $"dedup-{Guid.NewGuid():N}.pdf");
        string duplicatePath = renamed ? pdf + ".renamed.pdf" : pdf;
        try
        {
            PdfImportResult first = await context.CreateWorkflow(new PdfMetadataReader()).ImportPdfAsync(
                new PdfImportRequest(pdf, "Original", null, null));
            first.Success.Should().BeTrue(first.ErrorMessage);
            if (renamed)
            {
                File.Move(pdf, duplicatePath);
            }

            CountingPdfReader reader = new();
            CountingFingerprintService fingerprints = new();
            PdfImportResult duplicate = await context.CreateWorkflow(reader, pageInfoReader: reader,
                    fingerprintService: fingerprints)
                .ImportPdfAsync(new PdfImportRequest(duplicatePath, "Changed title", null, null));
            duplicate.Success.Should().BeTrue(duplicate.ErrorMessage);
            duplicate.IsDuplicate.Should().BeTrue();
            duplicate.CreatedItemId.Should().Be(first.CreatedItemId);
            duplicate.CreatedDocumentInstanceId.Should().Be(first.CreatedDocumentInstanceId);
            reader.PageCountCalls.Should().Be(0);
            reader.PageInfoCalls.Should().Be(0);
            fingerprints.Calls.Should().Be(1, "deduplication reuses the import fingerprint");
            await using SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            (await connection.ExecuteScalarAsync<int>("select count(*) from items;")).Should().Be(1);
            (await connection.ExecuteScalarAsync<int>("select count(*) from document_instances;")).Should().Be(1);
            (await connection.ExecuteScalarAsync<int>("select count(*) from pages;")).Should().Be(3);
            (await connection.ExecuteScalarAsync<int>(
                "select count(*) from known_file_locations where file_asset_id = @Id and path = @Path;",
                new { Id = first.CreatedFileAssetId, Path = Path.GetFullPath(duplicatePath) })).Should().Be(1);
        }
        finally
        {
            File.Delete(pdf);
            if (renamed)
            {
                File.Delete(duplicatePath);
            }
        }
    }

    [Fact]
    public async Task ImportPdf_rechecks_content_ownership_when_concurrent_imports_commit()
    {
        await using ImportContext context = await CreateContextAsync();
        string pdf = TestFixtures.CopyRealThreePagePdfTo(Path.GetTempPath(), $"race-{Guid.NewGuid():N}.pdf");
        try
        {
            ConcurrentPdfMetadataReader reader = new();
            PdfImportWorkflow firstWorkflow = context.CreateWorkflow(reader);
            PdfImportWorkflow secondWorkflow = context.CreateWorkflow(reader);
            PdfImportRequest request = new(pdf, null, null, null);
            PdfImportResult[] results = await Task.WhenAll(firstWorkflow.ImportPdfAsync(request),
                secondWorkflow.ImportPdfAsync(request));
            results.Should().OnlyContain(result => result.Success);
            results.Count(result => !result.IsDuplicate).Should().Be(1);
            results.Select(result => result.CreatedItemId).Distinct().Should().ContainSingle();
            await using SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            (await connection.ExecuteScalarAsync<int>("select count(*) from items;")).Should().Be(1);
            (await connection.ExecuteScalarAsync<int>("select count(*) from pages;")).Should().Be(3);
        }
        finally
        {
            File.Delete(pdf);
        }
    }

    [Theory]
    [InlineData(DocumentInstanceStatus.Active)]
    [InlineData(DocumentInstanceStatus.Deprecated)]
    public async Task ImportPdf_does_not_recreate_deleted_or_deprecated_content(string documentStatus)
    {
        await using ImportContext context = await CreateContextAsync();
        string pdf = TestFixtures.CopyRealThreePagePdfTo(Path.GetTempPath(), $"trash-{Guid.NewGuid():N}.pdf");
        try
        {
            PdfImportWorkflow workflow = context.CreateWorkflow(new PdfMetadataReader());
            PdfImportResult first = await workflow.ImportPdfAsync(new PdfImportRequest(pdf, null, null, null));
            await using SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                "update items set deleted_at = '2026-10-07T00:00:00Z' where item_id = @ItemId; " +
                "update document_instances set status = @Status where document_instance_id = @DocumentId; " +
                "update file_assets set status = 'missing' where file_asset_id = @FileId;",
                new
                {
                    ItemId = first.CreatedItemId, Status = documentStatus,
                    DocumentId = first.CreatedDocumentInstanceId, FileId = first.CreatedFileAssetId
                });
            PdfImportResult duplicate = await workflow.ImportPdfAsync(new PdfImportRequest(pdf, null, null, null));
            duplicate.Status.Should().Be("already_imported_deleted");
            duplicate.CreatedItemId.Should().Be(first.CreatedItemId);
            (await connection.ExecuteScalarAsync<int>("select count(*) from items;")).Should().Be(1);
            (await connection.ExecuteScalarAsync<string>(
                    "select deleted_at from items where item_id = @Id;", new { Id = first.CreatedItemId }))
                .Should().NotBeNull();
            (await connection.ExecuteScalarAsync<string>(
                    "select status from file_assets where file_asset_id = @Id;", new { Id = first.CreatedFileAssetId }))
                .Should().Be(FileAssetStatus.Available);
        }
        finally
        {
            File.Delete(pdf);
        }
    }

    [Fact]
    public async Task ImportPdf_matches_resolved_missing_file_with_noncanonical_asset_id()
    {
        await using ImportContext context = await CreateContextAsync();
        string pdf = Path.Combine(Path.GetTempPath(), $"legacy-{Guid.NewGuid():N}.pdf");
        try
        {
            FileAssetService files = new(context.Database.ConnectionFactory, context.Library, context.Clock);
            FileAsset missing = (await files.RegisterFileAsync(pdf)).Value;
            ItemService items = new(context.Database.ConnectionFactory, context.Library, context.Clock);
            ItemMetadata item = (await items.CreateItemAsync("book", "Manual source")).Value;
            DocumentInstance document = (await new DocumentInstanceService(context.Database.ConnectionFactory,
                context.Clock).AttachDocumentInstanceAsync(item.ItemId, missing.FileAssetId,
                DocumentInstanceType.PrimaryScan, makePrimary: true)).Value;
            File.Copy(TestFixtures.RealThreePagePdf, pdf);
            FileResolutionService resolution = new(context.Database.ConnectionFactory, context.Library, context.Clock);
            (await resolution.ConfirmChangedFileAsync(missing.FileAssetId, pdf)).IsSuccess.Should().BeTrue();
            CountingPdfReader reader = new();
            PdfImportResult duplicate = await context.CreateWorkflow(reader).ImportPdfAsync(
                new PdfImportRequest(pdf, null, null, null));
            duplicate.IsDuplicate.Should().BeTrue();
            duplicate.CreatedItemId.Should().Be(item.ItemId.ToString());
            duplicate.CreatedFileAssetId.Should().Be(missing.FileAssetId.ToString());
            duplicate.CreatedDocumentInstanceId.Should().Be(document.DocumentInstanceId.ToString());
            reader.PageCountCalls.Should().Be(0);
        }
        finally
        {
            File.Delete(pdf);
        }
    }

    [Fact]
    public async Task ImportPdf_preserves_changed_source_with_old_content_derived_id()
    {
        await using ImportContext context = await CreateContextAsync();
        string pdf = TestFixtures.CopyRealThreePagePdfTo(Path.GetTempPath(), $"changed-{Guid.NewGuid():N}.pdf");
        try
        {
            PdfImportWorkflow workflow = context.CreateWorkflow(new PdfMetadataReader());
            PdfImportResult first = await workflow.ImportPdfAsync(new PdfImportRequest(pdf, null, null, null));
            await using SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                "update file_assets set full_blake3 = 'changed-content' where file_asset_id = @Id;",
                new { Id = first.CreatedFileAssetId });
            PdfImportResult second = await workflow.ImportPdfAsync(new PdfImportRequest(pdf, null, null, null));
            second.Success.Should().BeTrue(second.ErrorMessage);
            second.IsDuplicate.Should().BeFalse();
            second.CreatedFileAssetId.Should().NotBe(first.CreatedFileAssetId);
            (await connection.ExecuteScalarAsync<string>(
                "select file_asset_id from document_instances where document_instance_id = @Id;",
                new { Id = second.CreatedDocumentInstanceId })).Should().Be(second.CreatedFileAssetId);
            (await connection.ExecuteScalarAsync<string>(
                "select full_blake3 from file_assets where file_asset_id = @Id;",
                new { Id = first.CreatedFileAssetId })).Should().Be("changed-content");
            (await workflow.ImportPdfAsync(new PdfImportRequest(pdf, null, null, null))).CreatedItemId
                .Should().Be(second.CreatedItemId);
        }
        finally
        {
            File.Delete(pdf);
        }
    }

    [Fact]
    public async Task ImportPdf_reuses_merge_target_without_recreating_source()
    {
        await using ImportContext context = await CreateContextAsync();
        string pdf = TestFixtures.CopyRealThreePagePdfTo(Path.GetTempPath(), $"merged-{Guid.NewGuid():N}.pdf");
        try
        {
            PdfImportWorkflow workflow = context.CreateWorkflow(new PdfMetadataReader());
            PdfImportResult source = await workflow.ImportPdfAsync(new PdfImportRequest(pdf, null, null, null));
            ItemMetadata target =
                (await new ItemService(context.Database.ConnectionFactory, context.Library, context.Clock)
                    .CreateItemAsync("book", "Merge target")).Value;
            await using SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                "update document_instances set item_id = @TargetId where document_instance_id = @DocumentId; " +
                "update items set merged_into_item_id = @TargetId, deleted_at = '2026-10-07' where item_id = @SourceId;",
                new
                {
                    TargetId = target.ItemId.ToString(), DocumentId = source.CreatedDocumentInstanceId,
                    SourceId = source.CreatedItemId
                });
            PdfImportResult duplicate = await workflow.ImportPdfAsync(new PdfImportRequest(pdf, null, null, null));
            duplicate.Status.Should().Be("already_imported");
            duplicate.CreatedItemId.Should().Be(target.ItemId.ToString());
            duplicate.CreatedDocumentInstanceId.Should().Be(source.CreatedDocumentInstanceId);
            (await connection.ExecuteScalarAsync<int>("select count(*) from items;")).Should().Be(2);
        }
        finally
        {
            File.Delete(pdf);
        }
    }

    [Fact]
    public async Task Manual_create_then_attach_can_share_content_and_import_prefers_active_owner()
    {
        await using ImportContext context = await CreateContextAsync();
        string pdf = TestFixtures.CopyRealThreePagePdfTo(Path.GetTempPath(), $"manual-{Guid.NewGuid():N}.pdf");
        try
        {
            PdfImportWorkflow workflow = context.CreateWorkflow(new PdfMetadataReader());
            PdfImportResult first = await workflow.ImportPdfAsync(new PdfImportRequest(pdf, null, null, null));
            ItemService items = new(context.Database.ConnectionFactory, context.Library, context.Clock);
            ItemMetadata manual = (await items.CreateItemAsync("book", "Deliberate manual duplicate")).Value;
            Result<DocumentInstance> attached = await new DocumentInstanceService(context.Database.ConnectionFactory,
                context.Clock).AttachDocumentInstanceAsync(manual.ItemId, FileAssetId.Parse(first.CreatedFileAssetId!),
                DocumentInstanceType.PrimaryScan, makePrimary: true);
            attached.IsSuccess.Should().BeTrue(attached.ErrorMessage);
            await using SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            await connection.ExecuteAsync("update items set deleted_at = '2026-10-07' where item_id = @Id;",
                new { Id = first.CreatedItemId });
            PdfImportResult duplicate = await workflow.ImportPdfAsync(new PdfImportRequest(pdf, null, null, null));
            duplicate.Status.Should().Be("already_imported");
            duplicate.CreatedItemId.Should().Be(manual.ItemId.ToString());
            (await connection.ExecuteScalarAsync<int>("select count(*) from items;")).Should().Be(2);
        }
        finally
        {
            File.Delete(pdf);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task File_owner_lookup_uses_indexes_with_unrelated_library_content(bool missingHash)
    {
        await using ImportContext context = await CreateContextAsync();
        await using SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        string libraryId = (await context.Library.GetCurrentLibraryAsync()).Value.LibraryId.ToString();
        await connection.ExecuteAsync("""
                                      with recursive rows(n) as (select 1 union all select n + 1 from rows where n < 1500)
                                      insert into file_assets(file_asset_id, library_id, original_path, file_name, size_bytes,
                                          full_blake3, status, created_at, updated_at)
                                      select printf('00000000-0000-0000-0000-%012x', n), @LibraryId, 'path-' || n, 'file-' || n, 1,
                                          'hash-' || n, 'available', '2026-10-07', '2026-10-07' from rows;
                                      insert into items(item_id, library_id, item_type, title, created_at, updated_at)
                                      select file_asset_id, library_id, 'book', file_name, created_at, updated_at from file_assets;
                                      insert into document_instances(document_instance_id, item_id, file_asset_id, instance_type,
                                          is_primary, status, created_at, updated_at)
                                      select file_asset_id, file_asset_id, file_asset_id, 'primary_scan',
                                          1, 'active', created_at, updated_at from file_assets;
                                      """, new { LibraryId = libraryId });
        string sql = missingHash ? FileImportDeduplication.OwnerByIdQuery : FileImportDeduplication.OwnerQuery;
        string assetId = await connection.ExecuteScalarAsync<string>(
                             "select file_asset_id from file_assets where library_id = @LibraryId and full_blake3 = 'hash-1000';",
                             new { LibraryId = libraryId }) ??
                         throw new InvalidOperationException("Indexed fixture asset was not found.");
        string[] plan = (await connection.QueryAsync<QueryPlanRow>("explain query plan " + sql,
                new { LibraryId = libraryId, FullBlake3 = "hash-1000", FileAssetId = assetId }))
            .Select(row => row.Detail).ToArray();
        plan.Should().NotContain(detail => detail.StartsWith("SCAN", StringComparison.OrdinalIgnoreCase));
        plan.Should().Contain(detail => detail.Contains(missingHash
            ? "sqlite_autoindex_file_assets_1"
            : "idx_file_assets_library_full_blake3", StringComparison.Ordinal));
        plan.Should().Contain(detail =>
            detail.Contains("idx_document_instances_file_asset_id", StringComparison.Ordinal));
        FileImportOwner? owner = await FileImportDeduplication.FindOwnerAsync(connection, null,
            LibraryId.Parse(libraryId), FileAssetId.Parse(assetId), missingHash ? null : "hash-1000");
        owner!.FileAssetId.Should().Be(assetId);
    }

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
                await context.ItemTypeInference.ListSuggestionsAsync(ItemId.Parse(result.CreatedItemId!));
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
        public IClock Clock { get; }
        public LibraryIdentityService Library { get; }
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
            double? maxFailedPageRatio = null,
            IFileFingerprintService? fingerprintService = null)
        {
            return new PdfImportWorkflow(
                new ImportBatchWriter(Database.ConnectionFactory),
                metadataReader,
                Clock,
                Library,
                fingerprintService,
                ItemTypeInference,
                activityTracker,
                pageInfoReader: pageInfoReader,
                maxFailedPageRatio: maxFailedPageRatio);
        }

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }

    private sealed class QueryPlanRow
    {
        public string Detail { get; init; } = string.Empty;
    }

    private sealed class CountingPdfReader : IPdfMetadataReader, IPdfPageInfoReader
    {
        public int PageCountCalls { get; private set; }
        public int PageInfoCalls { get; private set; }

        public Task<int?> GetPageCountAsync(string pdfPath, CancellationToken cancellationToken = default)
        {
            PageCountCalls++;
            return Task.FromResult<int?>(3);
        }

        public Task<IReadOnlyList<PdfPageInfoResult>?> GetPageInfosAsync(string pdfPath,
            CancellationToken cancellationToken = default)
        {
            PageInfoCalls++;
            return Task.FromResult<IReadOnlyList<PdfPageInfoResult>?>(SuccessPageInfos(3));
        }
    }

    private sealed class ConcurrentPdfMetadataReader : IPdfMetadataReader
    {
        private readonly TaskCompletionSource _bothEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public async Task<int?> GetPageCountAsync(string pdfPath, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 2)
            {
                _bothEntered.SetResult();
            }

            await _bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return 3;
        }
    }

    private sealed class CountingFingerprintService : IFileFingerprintService
    {
        private readonly FileFingerprintService _inner = new();
        public int Calls { get; private set; }

        public Task<Result<FileFingerprint>> GetFileMetadataAsync(string path,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return _inner.GetFileMetadataAsync(path, cancellationToken);
        }

        public Task<Result<string>> ComputeQuickHashAsync(string path, CancellationToken cancellationToken = default)
        {
            return _inner.ComputeQuickHashAsync(path, cancellationToken);
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
