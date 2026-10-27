using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Search;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Ocr;

namespace Patchouli.Tests;

/// <summary>
/// Issue #47: "移除关联文件" is a physical document-instance deletion. These tests pin the
/// complete payload cleanup, the task guards, the revision publication and the source-file
/// retention that the editor's remove flow relies on.
/// </summary>
public sealed class DocumentInstanceRemovalTests
{
    [Fact]
    public async Task Removal_deletes_pages_history_commits_translations_and_page_map()
    {
        await using RemovalCtx c = await RemovalCtx.Create();
        ItemId itemId = await c.InsertActiveItemWithPayloadAsync();
        await c.InsertHistoryAndTranslationRowsAsync();
        await c.BeginWorkingRevisionAsync();

        Result<DocumentInstanceRemoval> result =
            await c.Documents.RemoveDocumentInstanceAsync(itemId, c.DocumentInstanceId);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        (await c.ForeignKeysEnabled()).Should().Be(1);
        (await c.Count("document_instances")).Should().Be(0);
        (await c.Count("pages")).Should().Be(0);
        (await c.Count("document_page_maps")).Should().Be(0);
        (await c.Count("document_tree_revisions")).Should().Be(0);
        (await c.Count("document_boxes")).Should().Be(0);
        (await c.Count("document_commits")).Should().Be(0);
        (await c.Count("document_commit_pages")).Should().Be(0);
        (await c.Count("page_translations")).Should().Be(0);
        (await c.Count("translation_boxes")).Should().Be(0);
        (await c.ForeignKeyViolationCount()).Should().Be(0);
        (await c.Count("items")).Should().Be(1);
    }

    [Fact]
    public async Task Removal_deletes_ocr_runs_results_and_adopted_candidates()
    {
        await using RemovalCtx c = await RemovalCtx.Create();
        ItemId itemId = await c.InsertActiveItemWithPayloadAsync();
        // Adoptions carry non-cascading references to both the run and the instance; the old
        // narrow delete hit a foreign-key failure exactly on this shape.
        await c.InsertHistoricalOcrRowsAsync();

        Result<DocumentInstanceRemoval> result =
            await c.Documents.RemoveDocumentInstanceAsync(itemId, c.DocumentInstanceId);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        (await c.Count("ocr_runs")).Should().Be(0);
        (await c.Count("ocr_page_results")).Should().Be(0);
        (await c.Count("ocr_candidate_adoptions")).Should().Be(0);
        (await c.Count("document_instances")).Should().Be(0);
        (await c.ForeignKeyViolationCount()).Should().Be(0);
    }

    [Fact]
    public async Task Removal_deletes_search_units_fts_rows_and_index_status()
    {
        await using RemovalCtx c = await RemovalCtx.Create();
        ItemId itemId = await c.InsertActiveItemWithPayloadAsync();

        Result<DocumentInstanceRemoval> result =
            await c.Documents.RemoveDocumentInstanceAsync(itemId, c.DocumentInstanceId);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        (await c.Count("search_units")).Should().Be(0);
        (await c.Count("search_units_fts")).Should().Be(0);
        (await c.Count("fts_row_map")).Should().Be(0);
        (await c.Count("search_index_status")).Should().Be(0);
        (await c.ForeignKeyViolationCount()).Should().Be(0);
    }

    [Theory]
    [InlineData(OcrRunState.Pending)]
    [InlineData(OcrRunState.Running)]
    public async Task Removal_blocks_while_an_ocr_run_is_active(string state)
    {
        await using RemovalCtx c = await RemovalCtx.Create();
        ItemId itemId = await c.InsertActiveItemWithPayloadAsync();
        long revisionBefore = await c.RevisionAsync();
        await c.InsertOcrRunAsync(state);

        Result<DocumentInstanceRemoval> blocked =
            await c.Documents.RemoveDocumentInstanceAsync(itemId, c.DocumentInstanceId);

        blocked.IsFailure.Should().BeTrue();
        blocked.ErrorCode.Should().Be(AppErrorCodes.InvalidState);
        blocked.ErrorMessage.Should().Contain("OCR");
        (await c.Count("document_instances")).Should().Be(1);
        (await c.Count("pages")).Should().Be(1);
        (await c.Count("search_units")).Should().Be(1);
        (await c.RevisionAsync()).Should().Be(revisionBefore);
    }

    [Fact]
    public async Task Removal_rolls_back_everything_when_the_document_delete_fails()
    {
        await using RemovalCtx c = await RemovalCtx.Create();
        ItemId itemId = await c.InsertActiveItemWithPayloadAsync();
        await c.InsertHistoryAndTranslationRowsAsync();
        long revisionBefore = await c.RevisionAsync();
        await c.AddDocumentDeleteFailureTriggerAsync();

        Result<DocumentInstanceRemoval> result =
            await c.Documents.RemoveDocumentInstanceAsync(itemId, c.DocumentInstanceId);

        result.IsFailure.Should().BeTrue();
        (await c.Count("document_instances")).Should().Be(1);
        (await c.Count("pages")).Should().Be(1);
        (await c.Count("document_tree_revisions")).Should().Be(1);
        (await c.Count("document_commits")).Should().Be(2);
        (await c.Count("search_units")).Should().Be(1);
        (await c.Count("search_units_fts")).Should().Be(1);
        (await c.Count("fts_row_map")).Should().Be(1);
        (await c.Count("page_translations")).Should().Be(1);
        (await c.RevisionAsync()).Should().Be(revisionBefore);
        (await c.ForeignKeyViolationCount()).Should().Be(0);
    }

    [Fact]
    public async Task Removal_publishes_one_revision_and_reports_item_and_document_ids()
    {
        await using RemovalCtx c = await RemovalCtx.Create();
        ItemId itemId = await c.InsertActiveItemWithPayloadAsync();
        long before = await c.RevisionAsync();
        List<LibraryChangeSet> published = [];
        c.Revisions.ChangeCommitted += (_, args) => published.Add(args.ChangeSet);

        Result<DocumentInstanceRemoval> result =
            await c.Documents.RemoveDocumentInstanceAsync(itemId, c.DocumentInstanceId);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        (await c.RevisionAsync()).Should().Be(before + 1);
        published.Should().HaveCount(1);
        published[0].ItemIds.Should().Contain(itemId);
        published[0].DocumentInstanceIds.Should().Contain(c.DocumentInstanceId);
    }

    [Fact]
    public async Task Removal_keeps_source_and_payload_files_and_reports_gc_candidates()
    {
        await using RemovalCtx c = await RemovalCtx.Create();
        ItemId itemId = await c.InsertActiveItemWithPayloadAsync();
        await c.AddPayloadAssetReferenceAsync();

        Result<DocumentInstanceRemoval> result =
            await c.Documents.RemoveDocumentInstanceAsync(itemId, c.DocumentInstanceId);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        // The original file asset survives removal; both assets become GC candidates exactly
        // like after a permanent item purge.
        result.Value.FileAssetIds.Should().Contain(c.SourceFileAssetId);
        result.Value.FileAssetIds.Should().Contain(c.PayloadAssetId);
        File.Exists(c.OriginalFilePath).Should().BeTrue();
        (await c.Count("file_assets")).Should().Be(2);
        (await c.Count("file_asset_payload_refs")).Should().Be(0);
    }

    [Fact]
    public async Task Versioned_evidence_resolves_not_found_after_removal()
    {
        await using RemovalCtx c = await RemovalCtx.Create();
        ItemId itemId = await c.InsertActiveItemWithPayloadAsync();
        Result<EvidencePageText> before = await c.Evidence.GetBoxTextAsync(
            c.DocumentInstanceId, 1, c.TreeRevisionId, c.BoxId);
        before.IsSuccess.Should().BeTrue();

        Result<DocumentInstanceRemoval> result =
            await c.Documents.RemoveDocumentInstanceAsync(itemId, c.DocumentInstanceId);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        Result<EvidencePageText> after = await c.Evidence.GetBoxTextAsync(
            c.DocumentInstanceId, 1, c.TreeRevisionId, c.BoxId);
        after.IsFailure.Should().BeTrue();
        after.ErrorCode.Should().Be(AppErrorCodes.NotFound);
    }

    [Fact]
    public async Task Removal_promotes_the_oldest_remaining_instance()
    {
        await using RemovalCtx c = await RemovalCtx.Create();
        ItemId itemId = await c.InsertActiveItemWithPayloadAsync();
        DocumentInstanceId siblingId = await c.InsertSiblingDocumentInstanceAsync(itemId);

        Result<DocumentInstanceRemoval> result =
            await c.Documents.RemoveDocumentInstanceAsync(itemId, c.DocumentInstanceId);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        (await c.Count("document_instances")).Should().Be(1);
        await using SqliteConnection connection = c.Database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        int primary = await connection.ExecuteScalarAsync<int>(
            "select is_primary from document_instances where document_instance_id = @DocumentId;",
            new { DocumentId = siblingId.ToString() });
        primary.Should().Be(1);
    }

    private sealed class RemovalCtx : IAsyncDisposable
    {
        private RemovalCtx(
            TemporarySqliteDatabase database,
            FixedClock clock,
            LibraryIdentityService library,
            LibraryRevisionService revisions,
            ItemService items,
            IVersionedEvidenceReader evidence,
            DocumentInstanceService documents,
            string originalFilePath)
        {
            Database = database;
            Clock = clock;
            Library = library;
            Revisions = revisions;
            Items = items;
            Evidence = evidence;
            Documents = documents;
            OriginalFilePath = originalFilePath;
        }

        public TemporarySqliteDatabase Database { get; }
        public FixedClock Clock { get; }
        public LibraryIdentityService Library { get; }
        public LibraryRevisionService Revisions { get; }
        public ItemService Items { get; }
        public IVersionedEvidenceReader Evidence { get; }
        public DocumentInstanceService Documents { get; }
        public string OriginalFilePath { get; }
        public FileAssetId SourceFileAssetId { get; private set; }
        public FileAssetId PayloadAssetId { get; private set; }
        public DocumentInstanceId DocumentInstanceId { get; private set; }
        public PageId PageId { get; private set; }
        public DocumentTreeRevisionId TreeRevisionId { get; private set; }
        public DocumentBoxId BoxId { get; private set; }

        public static async Task<RemovalCtx> Create()
        {
            TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
            FixedClock clock = new(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            LibraryIdentityService library = new(database.ConnectionFactory, clock);
            LibraryRevisionService revisions = new(database.ConnectionFactory);
            await library.CreateLibraryAsync("Removal Test");
            ItemService items = new(database.ConnectionFactory, library, clock, revisions);
            DocumentTreeService trees = new(database.ConnectionFactory, clock, new MarkdigMarkdownEngine());
            IVersionedEvidenceReader evidence = new VersionedEvidenceReader(
                database.ConnectionFactory,
                library,
                trees,
                new DocumentMarkdownCompiler(trees, new MarkdigMarkdownEngine()));
            DocumentInstanceService documents = new(database.ConnectionFactory, clock, revisions);
            string originalFilePath = Path.Combine(Path.GetTempPath(), $"removal-{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(originalFilePath, "original");
            return new RemovalCtx(database, clock, library, revisions, items, evidence, documents,
                originalFilePath);
        }

        public async ValueTask DisposeAsync()
        {
            if (File.Exists(OriginalFilePath))
            {
                File.Delete(OriginalFilePath);
            }

            await Database.DisposeAsync();
        }

        public async Task<ItemId> InsertActiveItemWithPayloadAsync()
        {
            Result<ItemMetadata> item = await Items.CreateItemAsync("book", "Remove Me");
            item.IsSuccess.Should().BeTrue();

            LibraryMetadata library = (await Library.GetCurrentLibraryAsync()).Value;
            string now = Clock.UtcNow.ToString("O");
            FileAssetId fileAssetId = FileAssetId.New();
            SourceFileAssetId = fileAssetId;
            DocumentInstanceId documentId = DocumentInstanceId.New();
            PageId pageId = PageId.New();
            DocumentTreeRevisionId revisionId = DocumentTreeRevisionId.New();
            DocumentBoxId boxId = DocumentBoxId.New();
            SearchUnitId unitId = SearchUnitId.New();

            DocumentInstanceId = documentId;
            PageId = pageId;
            TreeRevisionId = revisionId;
            BoxId = boxId;

            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                """
                insert into file_assets (file_asset_id, library_id, original_path, file_name, size_bytes, status, created_at, updated_at)
                values (@FileAssetId, @LibraryId, @OriginalPath, 'original.txt', 8, 'available', @Now, @Now);

                insert into document_instances (document_instance_id, item_id, file_asset_id, instance_type, is_primary, status, created_at, updated_at)
                values (@DocumentId, @ItemId, @FileAssetId, 'primary_scan', 1, 'active', @Now, @Now);

                insert into pages (page_id, document_instance_id, page_index, rotation, coordinate_basis, renderer_basis_version, created_at, updated_at)
                values (@PageId, @DocumentId, 0, 0, 'normalized_page', 'test', @Now, @Now);

                insert into document_tree_revisions (tree_revision_id, document_instance_id, page_id, source, status, is_current, created_at, committed_at)
                values (@RevisionId, @DocumentId, @PageId, 'manual_edit', 'committed', 1, @Now, @Now);

                insert into document_boxes (tree_revision_id, box_id, document_instance_id, page_id, box_type, bbox_x, bbox_y, bbox_width, bbox_height, payload_json, suppressed)
                values (@RevisionId, @BoxId, @DocumentId, @PageId, 'text', 0.1, 0.1, 0.8, 0.1, '{"markdown":"sample"}', 0);

                insert into search_units (unit_id, document_instance_id, page_id, box_id, tree_revision_id, resolved_text, bbox_json, box_type, ordinal, status, created_at, updated_at)
                values (@UnitId, @DocumentId, @PageId, @BoxId, @RevisionId, 'sample text', '{"x":0.1,"y":0.1,"width":0.8,"height":0.1}', 'text', 1, @Current, @Now, @Now);

                insert into fts_row_map (document_instance_id, unit_id)
                values (@DocumentId, @UnitId);

                insert into search_units_fts (rowid, unit_id, document_instance_id, page_id, resolved_text)
                select fts_row_id, @UnitId, @DocumentId, @PageId, 'sample text'
                from fts_row_map
                where unit_id = @UnitId;

                insert into document_page_maps (document_instance_id, definition_json, created_at, updated_at)
                values (@DocumentId, '{"labels":["1"]}', @Now, @Now);

                insert into search_index_status (scope_type, scope_id, status, updated_at)
                values (@DocumentScope, @DocumentId, 'current', @Now);

                insert into search_index_status (scope_type, scope_id, status, updated_at)
                values (@PageScope, @PageId, 'current', @Now);
                """,
                new
                {
                    FileAssetId = fileAssetId.ToString(),
                    LibraryId = library.LibraryId.ToString(),
                    OriginalPath = OriginalFilePath,
                    DocumentId = documentId.ToString(),
                    ItemId = item.Value.ItemId.ToString(),
                    PageId = pageId.ToString(),
                    RevisionId = revisionId.ToString(),
                    BoxId = boxId.ToString(),
                    UnitId = unitId.ToString(),
                    Now = now,
                    Current = SearchUnitStatus.Current,
                    DocumentScope = SearchIndexScopeType.DocumentInstance,
                    PageScope = SearchIndexScopeType.Page
                });

            return item.Value.ItemId;
        }

        public async Task<DocumentInstanceId> InsertSiblingDocumentInstanceAsync(ItemId itemId)
        {
            DocumentInstanceId siblingId = DocumentInstanceId.New();
            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                """
                insert into document_instances (document_instance_id, item_id, file_asset_id, instance_type, is_primary, status, created_at, updated_at)
                values (@DocumentId, @ItemId, @FileAssetId, 'attachment', 0, 'active', @Now, @Now);
                """,
                new
                {
                    DocumentId = siblingId.ToString(),
                    ItemId = itemId.ToString(),
                    FileAssetId = SourceFileAssetId.ToString(),
                    Now = Clock.UtcNow.ToString("O")
                });
            return siblingId;
        }

        public async Task AddPayloadAssetReferenceAsync()
        {
            PayloadAssetId = FileAssetId.New();
            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            LibraryMetadata library = (await Library.GetCurrentLibraryAsync()).Value;
            string now = Clock.UtcNow.ToString("O");
            await connection.ExecuteAsync(
                """
                insert into file_assets (file_asset_id, library_id, original_path, file_name, size_bytes, status, created_at, updated_at)
                values (@FileAssetId, @LibraryId, @OriginalPath, 'payload.txt', 1, 'available', @Now, @Now);
                """,
                new
                {
                    FileAssetId = PayloadAssetId.ToString(),
                    LibraryId = library.LibraryId.ToString(),
                    OriginalPath = OriginalFilePath + ".payload",
                    Now = now
                });
            await connection.ExecuteAsync(
                "insert into file_asset_payload_refs (tree_revision_id, box_id, file_asset_id) values (@RevisionId, @BoxId, @FileAssetId);",
                new
                {
                    RevisionId = TreeRevisionId.ToString(),
                    BoxId = BoxId.ToString(),
                    FileAssetId = PayloadAssetId.ToString()
                });
        }

        public async Task AddDocumentDeleteFailureTriggerAsync()
        {
            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                "create trigger reject_document_delete before delete on document_instances begin select raise(abort, 'forced removal failure'); end;");
        }

        public async Task InsertHistoryAndTranslationRowsAsync()
        {
            string parentCommitId = Guid.NewGuid().ToString("N");
            string commitId = Guid.NewGuid().ToString("N");
            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                """
                insert into document_commits (commit_id, document_instance_id, parent_commit_id, source, message, created_at)
                values (@ParentCommitId, @DocumentId, null, 'manual_edit', 'parent', @Now);

                insert into document_commits (commit_id, document_instance_id, parent_commit_id, source, message, created_at)
                values (@CommitId, @DocumentId, @ParentCommitId, 'manual_edit', 'latest', @Now);

                insert into document_commit_pages (commit_id, page_id, tree_revision_id)
                values (@ParentCommitId, @PageId, @RevisionId);

                insert into document_commit_pages (commit_id, page_id, tree_revision_id)
                values (@CommitId, @PageId, @RevisionId);

                insert into page_translations (page_id, source_tree_revision_id, version, updated_at)
                values (@PageId, @RevisionId, 1, @Now);

                insert into translation_boxes (page_id, box_id, ordinal, translated_md, source_hash)
                values (@PageId, @BoxId, 0, 'translated sample', 'source-hash');
                """,
                new
                {
                    ParentCommitId = parentCommitId,
                    CommitId = commitId,
                    DocumentId = DocumentInstanceId.ToString(),
                    PageId = PageId.ToString(),
                    RevisionId = TreeRevisionId.ToString(),
                    BoxId = BoxId.ToString(),
                    Now = Clock.UtcNow.ToString("O")
                });
        }

        public async Task InsertHistoricalOcrRowsAsync()
        {
            string presetId = OcrPresetId.New().ToString();
            string presetVersionId = OcrPresetVersionId.New().ToString();
            string runId = OcrRunId.New().ToString();
            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                """
                insert into ocr_presets (preset_id, library_id, name, description, archived, current_version_id, created_at, updated_at)
                values (@PresetId, @LibraryId, 'Historical Test', 'Test', 0, @PresetVersionId, @Now, @Now);

                insert into ocr_preset_versions (preset_version_id, preset_id, engine_id, model_id, parameters_json, apply_on_success, created_at)
                values (@PresetVersionId, @PresetId, 'mock', 'mock-default', '{}', 0, @Now);

                insert into ocr_runs (ocr_run_id, document_instance_id, preset_id, preset_version_id, engine_id, model_id, parameters_snapshot_json, state, source_tree_revision_id, created_at, updated_at)
                values (@RunId, @DocumentId, @PresetId, @PresetVersionId, 'mock', 'mock-default', '{}', @Failed, @RevisionId, @Now, @Now);

                insert into ocr_page_results (result_id, ocr_run_id, page_id, state, working_tree_revision_id, created_at, updated_at)
                values (@ResultId, @RunId, @PageId, @Failed, @RevisionId, @Now, @Now);

                insert into ocr_candidate_adoptions (adoption_id, ocr_run_id, document_instance_id, adopted_tree_revisions_json, adopted_pages_json, created_at)
                values (@AdoptionId, @RunId, @DocumentId, '[]', '[]', @Now);
                """,
                new
                {
                    PresetId = presetId,
                    LibraryId = (await Library.GetCurrentLibraryAsync()).Value.LibraryId.ToString(),
                    PresetVersionId = presetVersionId,
                    RunId = runId,
                    DocumentId = DocumentInstanceId.ToString(),
                    RevisionId = TreeRevisionId.ToString(),
                    ResultId = Guid.NewGuid().ToString("N"),
                    PageId = PageId.ToString(),
                    AdoptionId = Guid.NewGuid().ToString("N"),
                    Failed = OcrRunState.Failed,
                    Now = Clock.UtcNow.ToString("O")
                });
        }

        public async Task InsertOcrRunAsync(string state)
        {
            string presetId = OcrPresetId.New().ToString();
            string presetVersionId = OcrPresetVersionId.New().ToString();
            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                """
                insert into ocr_presets (preset_id, library_id, name, description, archived, current_version_id, created_at, updated_at)
                values (@PresetId, @LibraryId, 'Test', 'Test', 0, @PresetVersionId, @Now, @Now);

                insert into ocr_preset_versions (preset_version_id, preset_id, engine_id, model_id, parameters_json, apply_on_success, created_at)
                values (@PresetVersionId, @PresetId, 'mock', 'mock-default', '{}', 0, @Now);

                insert into ocr_runs (ocr_run_id, document_instance_id, preset_id, preset_version_id, engine_id, model_id, parameters_snapshot_json, state, created_at, updated_at)
                values (@RunId, @DocumentId, @PresetId, @PresetVersionId, 'mock', 'mock-default', '{}', @State, @Now, @Now);
                """,
                new
                {
                    PresetId = presetId,
                    LibraryId = (await Library.GetCurrentLibraryAsync()).Value.LibraryId.ToString(),
                    PresetVersionId = presetVersionId,
                    RunId = OcrRunId.New().ToString(),
                    DocumentId = DocumentInstanceId.ToString(),
                    State = state,
                    Now = Clock.UtcNow.ToString("O")
                });
        }

        public async Task BeginWorkingRevisionAsync()
        {
            DocumentTreeService trees = new(Database.ConnectionFactory, Clock, new MarkdigMarkdownEngine());
            await trees.BeginWorkingRevisionAsync(
                DocumentInstanceId,
                PageId,
                [
                    new DocumentBoxSeed(null, null, 0, DocumentBoxType.Text, null,
                        new NormalizedBBox(.1, .1, .8, .1), new TextBoxPayload("working draft"))
                ],
                DocumentTreeRevisionSource.ManualEdit);
        }

        public async Task<int> Count(string table)
        {
            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            return await connection.ExecuteScalarAsync<int>($"select count(1) from {table}");
        }

        public async Task<long> RevisionAsync()
        {
            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            return await connection.ExecuteScalarAsync<long>("select library_revision from library_metadata limit 1;");
        }

        public async Task<int> ForeignKeysEnabled()
        {
            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            return await connection.ExecuteScalarAsync<int>("pragma foreign_keys;");
        }

        public async Task<int> ForeignKeyViolationCount()
        {
            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            return (await connection.QueryAsync("pragma foreign_key_check;")).Count();
        }
    }
}
