using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Import;
using Patchouli.Core.Layout;
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

public sealed class AtomicItemDocumentImportTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Automatic_import_requires_verified_content_while_manual_attachment_remains_available(string? hash)
    {
        await using ImportContext context = await ImportContext.CreateAsync();
        await using SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("update file_assets set full_blake3 = @Hash where file_asset_id = @Id;",
            new { Hash = hash, Id = context.Asset.FileAssetId.ToString() });

        Result<ItemMetadata> automatic = await context.Items.CreateItemWithPrimaryDocumentAsync(
            new CreateItemRequest("book", "Unverified import"), context.Asset.FileAssetId);

        automatic.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
        FileAsset unverified = context.Asset with { FullBlake3 = hash };
        ImportBatchWriter writer = new(context.Database.ConnectionFactory);
        (await writer.TryReuseAsync(unverified, context.PdfPath, context.Clock.UtcNow))
            .ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
        (ItemMetadata item, IReadOnlyList<ItemCreatorInput> creators, IReadOnlyList<ItemDateInput> dates) =
            ItemService.CreateItemMetadata(unverified.LibraryId,
                new CreateItemRequest("book", "Unverified PDF"), context.Clock.UtcNow);
        DocumentInstance document = new(DocumentInstanceId.New(), item.ItemId, unverified.FileAssetId,
            "Unverified PDF", DocumentInstanceType.PrimaryScan, true, DocumentInstanceStatus.Active,
            context.Clock.UtcNow, context.Clock.UtcNow);
        (await writer.CommitAsync(new PdfImportBatch(context.Clock.UtcNow, unverified, context.PdfPath,
                item, creators, dates, document, [], [])))
            .ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
        await context.AssertCountsAsync(0, 0);
        Result<ItemMetadata> manual = await context.Items.CreateItemAsync(new CreateItemRequest("book", "Manual"));
        manual.IsSuccess.Should().BeTrue(manual.ErrorMessage);
        (await context.Documents.AttachDocumentInstanceAsync(manual.Value.ItemId, context.Asset.FileAssetId,
            DocumentInstanceType.PrimaryScan)).IsSuccess.Should().BeTrue();
        await context.AssertCountsAsync(1, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Automatic_import_rejects_owned_content_without_creating_active_or_trashed_items(bool trashOwner)
    {
        await using ImportContext context = await ImportContext.CreateAsync();
        Result<ItemMetadata> first = await context.Items.CreateItemWithPrimaryDocumentAsync(
            new CreateItemRequest("book", "Milk Is Gold"), context.Asset.FileAssetId, "milk.pdf");
        first.IsSuccess.Should().BeTrue(first.ErrorMessage);
        if (trashOwner)
        {
            (await context.Items.DeleteItemAsync(first.Value.ItemId)).IsSuccess.Should().BeTrue();
        }

        Result<ItemMetadata> duplicate = await context.Items.CreateItemWithPrimaryDocumentAsync(
            new CreateItemRequest("thesis", "Milk Is Gold: full title"), context.Asset.FileAssetId);

        duplicate.IsFailure.Should().BeTrue();
        duplicate.ErrorCode.Should().Be(AppErrorCodes.Conflict);
        duplicate.ErrorMessage.Should().Contain(first.Value.ItemId.ToString());
        await context.AssertCountsAsync(1, 1);
    }

    [Fact]
    public async Task Automatic_import_matches_full_hash_even_when_asset_ids_differ()
    {
        await using ImportContext context = await ImportContext.CreateAsync();
        FileAssetId legacyId = FileAssetId.New();
        await using SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            """
            insert into file_assets (
                file_asset_id, library_id, original_path, file_name, size_bytes, full_blake3,
                status, created_at, updated_at
            ) select @LegacyId, library_id, original_path, file_name, size_bytes, full_blake3,
                status, created_at, updated_at from file_assets where file_asset_id = @AssetId;
            """, new { LegacyId = legacyId.ToString(), AssetId = context.Asset.FileAssetId.ToString() });
        Result<ItemMetadata> owner = await context.Items.CreateItemAsync(new CreateItemRequest("book", "Legacy"));
        owner.IsSuccess.Should().BeTrue(owner.ErrorMessage);
        (await context.Documents.AttachDocumentInstanceAsync(owner.Value.ItemId, legacyId,
            DocumentInstanceType.PrimaryScan)).IsSuccess.Should().BeTrue();

        Result<ItemMetadata> duplicate = await context.Items.CreateItemWithPrimaryDocumentAsync(
            new CreateItemRequest("book", "Duplicate"), context.Asset.FileAssetId);

        duplicate.ErrorCode.Should().Be(AppErrorCodes.Conflict);
        await context.AssertCountsAsync(1, 1);
    }

    [Fact]
    public async Task Document_insert_failure_rolls_back_item_metadata()
    {
        await using ImportContext context = await ImportContext.CreateAsync();
        await using SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            """
            create trigger fail_imported_document before insert on document_instances
            begin select raise(abort, 'document insert rejected'); end;
            """);

        Result<ItemMetadata> result = await context.Items.CreateItemWithPrimaryDocumentAsync(
            new CreateItemRequest("book", "Failed import",
                Creators: [new ItemCreatorInput("author", "Linden", "Kenneth E.")]),
            context.Asset.FileAssetId);

        result.ErrorCode.Should().Be(AppErrorCodes.DatabaseError);
        await context.AssertCountsAsync(0, 0);
        (await connection.ExecuteScalarAsync<int>("select count(*) from item_creators;")).Should().Be(0);
    }

    [Fact]
    public async Task Manual_create_fill_and_attach_can_share_an_existing_file()
    {
        await using ImportContext context = await ImportContext.CreateAsync();
        Result<ItemMetadata> imported = await context.Items.CreateItemWithPrimaryDocumentAsync(
            new CreateItemRequest("book", "Imported"), context.Asset.FileAssetId);
        imported.IsSuccess.Should().BeTrue(imported.ErrorMessage);
        Result<ItemMetadata> manual = await context.Items.CreateItemAsync(new CreateItemRequest("thesis", "Manual"));
        manual.IsSuccess.Should().BeTrue(manual.ErrorMessage);
        Result<ItemMetadata> filled = await context.Items.UpdateItemAsync(manual.Value.ItemId,
            new UpdateItemRequest("thesis", "Manual title", Publisher: "Indiana University"));
        filled.IsSuccess.Should().BeTrue(filled.ErrorMessage);

        Result<DocumentInstance> attachment = await context.Documents.AttachDocumentInstanceAsync(
            manual.Value.ItemId, context.Asset.FileAssetId, DocumentInstanceType.PrimaryScan);

        attachment.IsSuccess.Should().BeTrue(attachment.ErrorMessage);
        await context.AssertCountsAsync(2, 2);
    }

    [Fact]
    public async Task Concurrent_automatic_creations_claim_file_content_once()
    {
        await using ImportContext context = await ImportContext.CreateAsync();
        ItemService items = context.Items;
        FileAssetId fileAssetId = context.Asset.FileAssetId;
        Result<ItemMetadata>[] results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(index => items.CreateItemWithPrimaryDocumentAsync(
                new CreateItemRequest("book", "Import " + index), fileAssetId)));

        results.Count(result => result.IsSuccess).Should().Be(1);
        results.Where(result => result.IsFailure).Should()
            .OnlyContain(result => result.ErrorCode == AppErrorCodes.Conflict);
        await context.AssertCountsAsync(1, 1);
    }

    [Fact]
    public async Task Pdf_commit_rechecks_owner_created_by_bibliography_import_after_preflight()
    {
        await using ImportContext context = await ImportContext.CreateAsync();
        BlockingPageInfoReader pageInfo = new();
        PdfImportWorkflow pdf = new(new ImportBatchWriter(context.Database.ConnectionFactory),
            new PdfMetadataReader(), context.Clock, context.Library, pageInfoReader: pageInfo);
        Task<PdfImportResult> pdfImport = pdf.ImportPdfAsync(new PdfImportRequest(context.PdfPath, "PDF", null, 3));
        await pageInfo.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Result<ItemMetadata> bibliography = await context.Items.CreateItemWithPrimaryDocumentAsync(
            new CreateItemRequest("thesis", "Bibliography"), context.Asset.FileAssetId);
        pageInfo.Continue.TrySetResult();
        PdfImportResult pdfResult = await pdfImport.WaitAsync(TimeSpan.FromSeconds(10));

        bibliography.IsSuccess.Should().BeTrue(bibliography.ErrorMessage);
        pdfResult.Success.Should().BeTrue(pdfResult.ErrorMessage);
        pdfResult.CreatedItemId.Should().Be(bibliography.Value.ItemId.ToString());
        pdfResult.Status.Should().Be("already_imported");
        await context.AssertCountsAsync(1, 1);
    }

    private sealed class BlockingPageInfoReader : IPdfPageInfoReader
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<PdfPageInfoResult>?> GetPageInfosAsync(
            string pdfPath, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Continue.Task.WaitAsync(cancellationToken);
            return null;
        }
    }

    private sealed class ImportContext : IAsyncDisposable
    {
        public TemporarySqliteDatabase Database { get; }
        public IClock Clock { get; }
        public LibraryIdentityService Library { get; }
        public ItemService Items { get; }
        public DocumentInstanceService Documents { get; }
        public FileAsset Asset { get; }
        public string PdfPath { get; }

        private ImportContext(TemporarySqliteDatabase database, IClock clock, LibraryIdentityService library,
            FileAsset asset, string pdfPath)
        {
            Database = database;
            Clock = clock;
            Library = library;
            Items = new ItemService(database.ConnectionFactory, library, clock);
            Documents = new DocumentInstanceService(database.ConnectionFactory, clock);
            Asset = asset;
            PdfPath = pdfPath;
        }

        public static async Task<ImportContext> CreateAsync()
        {
            TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
            SystemClock clock = new();
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            LibraryIdentityService library = new(database.ConnectionFactory, clock);
            (await library.CreateLibraryAsync("Atomic import tests")).IsSuccess.Should().BeTrue();
            string pdfPath =
                TestFixtures.CopyRealThreePagePdfTo(Path.GetTempPath(), $"atomic-import-{Guid.NewGuid():N}.pdf");
            Result<FileAsset> asset = await new FileAssetService(database.ConnectionFactory, library, clock)
                .RegisterFileAsync(pdfPath);
            asset.IsSuccess.Should().BeTrue(asset.ErrorMessage);
            return new ImportContext(database, clock, library, asset.Value, pdfPath);
        }

        public async Task AssertCountsAsync(int items, int documents)
        {
            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            (await connection.ExecuteScalarAsync<int>("select count(*) from items;")).Should().Be(items);
            (await connection.ExecuteScalarAsync<int>("select count(*) from document_instances;")).Should()
                .Be(documents);
        }

        public async ValueTask DisposeAsync()
        {
            File.Delete(PdfPath);
            await Database.DisposeAsync();
        }
    }
}
