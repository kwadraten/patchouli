using System.Data.Common;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Documents;
using Patchouli.Core.Layout;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Infrastructure.Search;
using Patchouli.Ocr;

namespace Patchouli.Tests;

public sealed class ItemMergeServiceTests
{
    [Fact]
    public async Task BuildMergePreviewAsync_detects_conflicts_and_missing_fields()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> source = await context.Items.CreateItemAsync(
            "book",
            "Source Title",
            publicationTitle: "Source Journal",
            tagsJson: "[\"shared\",\"source-only\"]");
        Result<ItemMetadata> target = await context.Items.CreateItemAsync(
            "book",
            "Target Title",
            tagsJson: "[\"shared\",\"target-only\"]");

        Result<ItemMergePreview> preview = await context.MergeItems.BuildMergePreviewAsync(
            source.Value.ItemId, target.Value.ItemId);

        preview.IsSuccess.Should().BeTrue();
        preview.Value.SourceItemId.Should().Be(source.Value.ItemId);
        preview.Value.TargetItemId.Should().Be(target.Value.ItemId);
        preview.Value.ConflictFields.Should().ContainSingle(f => f.FieldName == "title");
        preview.Value.MissingFields.Should().ContainSingle(f => f.FieldName == "publication_title");
        preview.Value.TagUnion.Should().Equal("shared", "target-only", "source-only");
    }

    [Fact]
    public async Task MergeAsync_updates_target_with_source_values_and_tag_union()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> source = await context.Items.CreateItemAsync(
            "book",
            "Source Title",
            publicationTitle: "Source Journal",
            tagsJson: "[\"shared\",\"source-only\"]");
        Result<ItemMetadata> target = await context.Items.CreateItemAsync(
            "book",
            "Target Title",
            tagsJson: "[\"shared\",\"target-only\"]");

        Result<ItemMergePreview> preview = await context.MergeItems.BuildMergePreviewAsync(
            source.Value.ItemId, target.Value.ItemId);

        Result result = await context.MergeItems.MergeAsync(
            source.Value.ItemId,
            target.Value.ItemId,
            preview.Value.ConflictFields
                .Select(f => new MergeFieldChoice(f.FieldName, false))
                .ToArray(),
            _ => false);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);

        Result<ItemMetadata> updatedTarget = await context.Items.GetItemAsync(target.Value.ItemId);
        updatedTarget.Value.Title.Should().Be("Target Title");
        updatedTarget.Value.PublicationTitle.Should().Be("Source Journal");
        updatedTarget.Value.TagsJson.Should().Be("[\"shared\",\"target-only\",\"source-only\"]");
    }

    [Fact]
    public async Task MergeAsync_chooses_source_values_when_requested()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> source = await context.Items.CreateItemAsync("book", "Source Title");
        Result<ItemMetadata> target = await context.Items.CreateItemAsync("book", "Target Title");

        Result result = await context.MergeItems.MergeAsync(
            source.Value.ItemId,
            target.Value.ItemId,
            [new MergeFieldChoice("title", true)],
            _ => false);

        result.IsSuccess.Should().BeTrue();

        Result<ItemMetadata> updatedTarget = await context.Items.GetItemAsync(target.Value.ItemId);
        updatedTarget.Value.Title.Should().Be("Source Title");
    }

    [Fact]
    public async Task MergeAsync_can_adopt_source_citation_key_without_unique_collision()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> source = await context.Items.CreateItemAsync("book", "Source Title");
        Result<ItemMetadata> target = await context.Items.CreateItemAsync("book", "Target Title");
        string sourceKey = source.Value.CitationKey;

        Result result = await context.MergeItems.MergeAsync(
            source.Value.ItemId,
            target.Value.ItemId,
            [new MergeFieldChoice("citation_key", true)],
            _ => false);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);

        Result<ItemMetadata> updatedTarget = await context.Items.GetItemAsync(target.Value.ItemId);
        updatedTarget.Value.CitationKey.Should().Be(sourceKey);

        await using SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        string? sourceCitationKey = await connection.ExecuteScalarAsync<string>(
            "select citation_key from items where item_id = @Id;",
            new { Id = source.Value.ItemId.ToString() });
        sourceCitationKey.Should().StartWith("merged-");
        sourceCitationKey.Should().NotBe(sourceKey);
    }

    [Fact]
    public async Task MergeAsync_transfers_document_instances_to_target()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> source = await context.Items.CreateItemAsync("book", "Source");
        Result<ItemMetadata> target = await context.Items.CreateItemAsync("book", "Target");
        DocumentInstance document = (await context.Documents.AttachDocumentInstanceAsync(
            source.Value.ItemId, null, DocumentInstanceType.PrimaryScan)).Value;

        Result result = await context.MergeItems.MergeAsync(
            source.Value.ItemId,
            target.Value.ItemId,
            [],
            _ => false);

        result.IsSuccess.Should().BeTrue();

        Result<IReadOnlyList<DocumentInstance>> targetDocuments =
            await context.Documents.ListDocumentInstancesForItemAsync(target.Value.ItemId);
        targetDocuments.Value.Should().ContainSingle(d => d.DocumentInstanceId == document.DocumentInstanceId);

        Result<IReadOnlyList<DocumentInstance>> sourceDocuments =
            await context.Documents.ListDocumentInstancesForItemAsync(source.Value.ItemId);
        sourceDocuments.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task MergeAsync_sets_merged_into_item_id_on_source_and_keeps_row()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> source = await context.Items.CreateItemAsync("book", "Source");
        Result<ItemMetadata> target = await context.Items.CreateItemAsync("book", "Target");

        Result result = await context.MergeItems.MergeAsync(
            source.Value.ItemId, target.Value.ItemId, [], _ => false);

        result.IsSuccess.Should().BeTrue();

        string? mergedInto = await GetMergedIntoAsync(context, source.Value.ItemId);
        mergedInto.Should().Be(target.Value.ItemId.ToString());

        Result<ItemMetadata> sourceItem = await context.Items.GetItemAsync(source.Value.ItemId);
        sourceItem.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task MergeAsync_preserves_page_identity_on_transferred_document()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> source = await context.Items.CreateItemAsync("book", "Source");
        Result<ItemMetadata> target = await context.Items.CreateItemAsync("book", "Target");
        DocumentInstance document = (await context.Documents.AttachDocumentInstanceAsync(
            source.Value.ItemId, null, DocumentInstanceType.PrimaryScan)).Value;
        Page page = (await context.Pages.CreatePageAsync(
            document.DocumentInstanceId,
            0,
            "1",
            null,
            null,
            0,
            CoordinateBasis.NormalizedPage,
            null,
            null,
            "test",
            null)).Value;

        Result result = await context.MergeItems.MergeAsync(
            source.Value.ItemId, target.Value.ItemId, [], _ => false);

        result.IsSuccess.Should().BeTrue();

        await using SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        string? documentItemId = await connection.ExecuteScalarAsync<string>(
            "select item_id from document_instances where document_instance_id = @DocumentInstanceId;",
            new { DocumentInstanceId = document.DocumentInstanceId.ToString() });
        documentItemId.Should().Be(target.Value.ItemId.ToString());

        string? pageOwnerId = await connection.ExecuteScalarAsync<string>(
            "select document_instance_id from pages where page_id = @PageId;",
            new { PageId = page.PageId.ToString() });
        pageOwnerId.Should().Be(document.DocumentInstanceId.ToString());
    }

    [Fact]
    public async Task MergeAsync_leaves_no_evidence_artifacts_and_preserves_search_units()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> source = await context.Items.CreateItemAsync("book", "Source");
        Result<ItemMetadata> target = await context.Items.CreateItemAsync("book", "Target");
        DocumentInstance document = (await context.Documents.AttachDocumentInstanceAsync(
            source.Value.ItemId, null, DocumentInstanceType.PrimaryScan)).Value;
        Page page = (await context.Pages.CreatePageAsync(
            document.DocumentInstanceId, 0, "1", null, null, 0,
            CoordinateBasis.NormalizedPage, null, null, "test", null)).Value;
        DocumentTreeService trees = new(context.Database.ConnectionFactory, context.Clock, new MarkdigMarkdownEngine());
        DocumentTreeRevision working = (await trees.BeginWorkingRevisionAsync(
            document.DocumentInstanceId,
            page.PageId,
            [
                new DocumentBoxSeed(null, null, 0, DocumentBoxType.Text, null, null,
                    new NormalizedBBox(.1, .1, .8, .1), new TextBoxPayload("merge sample"))
            ],
            DocumentTreeRevisionSource.Import)).Value;
        await trees.CommitWorkingRevisionAsync(working.TreeRevisionId);
        SearchUnitBuilder units = new(context.Database.ConnectionFactory, context.Clock);
        (await units.RebuildForDocumentInstanceAsync(document.DocumentInstanceId)).IsSuccess.Should().BeTrue();

        Result result = await context.MergeItems.MergeAsync(
            source.Value.ItemId, target.Value.ItemId, [], _ => false);

        result.IsSuccess.Should().BeTrue();

        await using SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        int searchUnitCount = await connection.ExecuteScalarAsync<int>("select count(1) from search_units;");
        searchUnitCount.Should().Be(1);
        int evidenceTableExists = await connection.ExecuteScalarAsync<int>(
            "select count(1) from sqlite_master where type = 'table' and name = 'evidence_ref_records';");
        evidenceTableExists.Should().Be(0, "evidence_ref_records table was dropped in the unified model");
    }

    [Fact]
    public async Task MergeAsync_blocks_active_ocr_runs()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> source = await context.Items.CreateItemAsync("book", "Source");
        Result<ItemMetadata> target = await context.Items.CreateItemAsync("book", "Target");
        DocumentInstance document = (await context.Documents.AttachDocumentInstanceAsync(
            source.Value.ItemId, null, DocumentInstanceType.PrimaryScan)).Value;

        OcrPreset preset = (await context.OcrPresets.CreatePresetAsync(
            "Test",
            null,
            OcrEngineIds.Mock,
            OcrModelIds.MockBasic,
            null,
            "{}",
            false)).Value;
        OcrPresetVersion version = (await context.OcrPresets.GetCurrentVersionAsync(preset.PresetId)).Value;

        await using (SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                """
                insert into ocr_runs (
                    ocr_run_id, document_instance_id, preset_id, preset_version_id, engine_id, model_id,
                    parameters_snapshot_json, state, created_at, updated_at)
                values (
                    @OcrRunId, @DocumentInstanceId, @PresetId, @PresetVersionId, @EngineId, @ModelId,
                    '{}', @State, @CreatedAt, @UpdatedAt);
                """,
                new
                {
                    OcrRunId = OcrRunId.New().ToString(),
                    DocumentInstanceId = document.DocumentInstanceId.ToString(),
                    PresetId = preset.PresetId.ToString(),
                    PresetVersionId = version.PresetVersionId.ToString(),
                    EngineId = OcrEngineIds.Mock,
                    ModelId = OcrModelIds.MockBasic,
                    State = OcrRunState.Running,
                    CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
                    UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
                });
        }

        Result result = await context.MergeItems.MergeAsync(
            source.Value.ItemId, target.Value.ItemId, [], _ => false);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(AppErrorCodes.InvalidState);
    }

    [Fact]
    public async Task MergeAsync_blocks_unsaved_edits()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> source = await context.Items.CreateItemAsync("book", "Source");
        Result<ItemMetadata> target = await context.Items.CreateItemAsync("book", "Target");

        Result result = await context.MergeItems.MergeAsync(
            source.Value.ItemId, target.Value.ItemId, [], id => id == source.Value.ItemId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(AppErrorCodes.InvalidState);
    }

    [Fact]
    public async Task MergeAsync_emits_single_revision_for_source_and_target()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> source = await context.Items.CreateItemAsync("book", "Source");
        Result<ItemMetadata> target = await context.Items.CreateItemAsync("book", "Target");
        List<ItemId> captured = new();
        context.Revisions.ChangeCommitted += (_, args) => captured.AddRange(args.ChangeSet.ItemIds);
        context.Revisions.ResetPublishCount();

        Result result = await context.MergeItems.MergeAsync(
            source.Value.ItemId, target.Value.ItemId, [], _ => false);

        result.IsSuccess.Should().BeTrue();
        captured.Should().Contain(source.Value.ItemId);
        captured.Should().Contain(target.Value.ItemId);
        context.Revisions.PublishCount.Should().Be(1);
    }

    [Fact]
    public async Task BuildMergePreviewAsync_fails_when_source_or_target_is_merged()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> source = await context.Items.CreateItemAsync("book", "Source");
        Result<ItemMetadata> target = await context.Items.CreateItemAsync("book", "Target");
        Result<ItemMetadata> other = await context.Items.CreateItemAsync("book", "Other");

        await context.MergeItems.MergeAsync(
            source.Value.ItemId, other.Value.ItemId, [], _ => false);

        Result<ItemMergePreview> preview = await context.MergeItems.BuildMergePreviewAsync(
            source.Value.ItemId, target.Value.ItemId);

        preview.IsSuccess.Should().BeFalse();
    }

    private static async Task<string?> GetMergedIntoAsync(TestContext context, ItemId itemId)
    {
        await using SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<string>(
            "select merged_into_item_id from items where item_id = @ItemId;",
            new { ItemId = itemId.ToString() });
    }

    private static async Task<TestContext> CreateContextAsync()
    {
        TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        FixedClock clock = new(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        LibraryIdentityService library = new(database.ConnectionFactory, clock);
        await library.CreateLibraryAsync("Merge Test");
        CapturingRevisionService revisions = new();
        ItemService items = new(database.ConnectionFactory, library, clock, revisions);
        DocumentInstanceService documents = new(database.ConnectionFactory, clock, revisions);
        PageService pages = new(database.ConnectionFactory, clock);
        OcrPresetService ocrPresets = new(database.ConnectionFactory, library, clock);
        ItemMergeService mergeItems = new(database.ConnectionFactory, clock, library, revisions);
        return new TestContext(database, clock, items, documents, pages, ocrPresets, mergeItems, revisions);
    }

    private sealed class TestContext : IAsyncDisposable
    {
        public TestContext(
            TemporarySqliteDatabase database,
            FixedClock clock,
            ItemService items,
            DocumentInstanceService documents,
            PageService pages,
            OcrPresetService ocrPresets,
            ItemMergeService mergeItems,
            CapturingRevisionService revisions)
        {
            Database = database;
            Clock = clock;
            Items = items;
            Documents = documents;
            Pages = pages;
            OcrPresets = ocrPresets;
            MergeItems = mergeItems;
            Revisions = revisions;
        }

        public TemporarySqliteDatabase Database { get; }
        public FixedClock Clock { get; }
        public ItemService Items { get; }
        public DocumentInstanceService Documents { get; }
        public PageService Pages { get; }
        public OcrPresetService OcrPresets { get; }
        public ItemMergeService MergeItems { get; }
        public CapturingRevisionService Revisions { get; }

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }

    private sealed class CapturingRevisionService : ILibraryRevisionService
    {
        public event EventHandler<LibraryRevisionCommittedEventArgs>? ChangeCommitted;
        public int PublishCount { get; private set; }

        public void ResetPublishCount()
        {
            PublishCount = 0;
        }

        public Task<Result<long>> GetCurrentRevisionAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<long>.Success(0));
        }

        public Task<Result<long>> CommitAsync(LibraryChangeSet changeSet,
            CancellationToken cancellationToken = default)
        {
            PublishCommitted(changeSet);
            return Task.FromResult(Result<long>.Success(1));
        }

        public Task<Result<LibraryChangeSet>> IncrementInTransactionAsync(
            DbConnection connection,
            DbTransaction transaction,
            LibraryChangeSet changeSet,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<LibraryChangeSet>.Success(changeSet));
        }

        public void PublishCommitted(LibraryChangeSet changeSet)
        {
            PublishCount++;
            ChangeCommitted?.Invoke(this, new LibraryRevisionCommittedEventArgs(changeSet));
        }
    }
}
