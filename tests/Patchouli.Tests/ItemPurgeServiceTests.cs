using Dapper;
using System.Collections.Concurrent;
using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Ocr;
using Patchouli.Core.Search;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Search;

namespace Patchouli.Tests;

public sealed class ItemPurgeServiceTests
{
    [Fact]
    public async Task Purge_deletes_payload_and_versioned_evidence_resolves_to_not_found()
    {
        await using Ctx c = await Ctx.Create();
        ItemId itemId = await c.InsertTrashedItemWithPayloadAsync();
        string versionedUri = await c.CreateVersionedEvidenceUriAsync(itemId);

        Result<EvidencePageText> before = await c.Evidence.GetBoxTextAsync(
            c.DocumentInstanceId, 1, c.TreeRevisionId, c.BoxId);
        before.IsSuccess.Should().BeTrue();
        before.Value.Markdown.Should().Contain("sample");

        Result result = await c.Purge.PurgeItemsAsync([itemId]);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        (await c.Count("items")).Should().Be(0);
        (await c.Count("document_instances")).Should().Be(0);
        (await c.Count("pages")).Should().Be(0);
        (await c.Count("document_tree_revisions")).Should().Be(0);
        (await c.Count("document_boxes")).Should().Be(0);
        (await c.Count("search_units")).Should().Be(0);
        (await c.Count("search_units_fts")).Should().Be(0);
        (await c.Count("fts_row_map")).Should().Be(0);
        (await c.Count("item_purge_records")).Should().Be(1);
        (await c.ForeignKeyViolationCount()).Should().Be(0);

        Result<EvidencePageText> after = await c.Evidence.GetBoxTextAsync(
            c.DocumentInstanceId, 1, c.TreeRevisionId, c.BoxId);
        after.IsFailure.Should().BeTrue();
        after.ErrorCode.Should().Be(AppErrorCodes.NotFound);
        File.Exists(c.OriginalFilePath).Should().BeTrue();
        (await c.Count("file_assets")).Should().Be(1);
    }

    [Fact]
    public async Task Purge_empties_trash_and_blocks_active_ocr()
    {
        await using Ctx c = await Ctx.Create();
        ItemId itemId = await c.InsertTrashedItemWithPayloadAsync();
        await c.InsertActiveOcrRunAsync(itemId);

        Result blocked = await c.Purge.PurgeItemsAsync([itemId]);

        blocked.IsFailure.Should().BeTrue();
        blocked.ErrorCode.Should().Be(AppErrorCodes.InvalidState);
        (await c.Count("items")).Should().Be(1);
    }

    [Fact]
    public async Task Purge_increments_revision_once()
    {
        await using Ctx c = await Ctx.Create();
        ItemId itemId = await c.InsertTrashedItemWithPayloadAsync();
        long before = await c.RevisionAsync();
        int eventCount = 0;
        c.Revisions.ChangeCommitted += (_, _) => eventCount++;

        Result result = await c.Purge.PurgeItemsAsync([itemId]);

        result.IsSuccess.Should().BeTrue();
        (await c.RevisionAsync()).Should().Be(before + 1);
        eventCount.Should().Be(1);
    }

    [Fact]
    public async Task BuildReport_reflects_dependencies_without_evidence_count()
    {
        await using Ctx c = await Ctx.Create();
        ItemId itemId = await c.InsertTrashedItemWithPayloadAsync();
        await c.AddPayloadAssetReferenceAsync();
        await c.CreateVersionedEvidenceUriAsync(itemId);

        Result<ItemPurgeDependencyReport> report = await c.Purge.BuildPurgeReportAsync(itemId);

        report.IsSuccess.Should().BeTrue(report.ErrorMessage);
        report.Value.ItemId.Should().Be(itemId);
        report.Value.HasActiveOcr.Should().BeFalse();
        report.Value.HasOcrCandidates.Should().BeFalse();
        report.Value.HasWorking.Should().BeFalse();
        report.Value.FileAssetIds.Should().Contain(c.SourceFileAssetId);
        report.Value.FileAssetIds.Should().Contain(c.PayloadAssetId);
    }

    [Fact]
    public async Task Purge_report_ignores_non_asset_strings_in_legacy_payloads()
    {
        await using Ctx c = await Ctx.Create();
        ItemId itemId = await c.InsertTrashedItemWithPayloadAsync();
        await c.BeginWorkingRevisionAsync(itemId);
        await using SqliteConnection connection = c.Database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            """
            update document_boxes set payload_json = '{"text":"text","assetId":"not-an-asset-id"}'
            where tree_revision_id in (select tree_revision_id from document_tree_revisions
                where document_instance_id = @DocumentId and status = 'working');
            """,
            new { DocumentId = c.DocumentInstanceId.ToString() });

        Result<ItemPurgeDependencyReport> report = await c.Purge.BuildPurgeReportAsync(itemId);

        report.IsSuccess.Should().BeTrue(report.ErrorMessage);
    }

    [Fact]
    public async Task Document_rebuild_uses_explicit_row_map_and_deletes_only_scoped_orphans()
    {
        await using Ctx c = await Ctx.Create();
        _ = await c.InsertTrashedItemWithPayloadAsync();
        string staleTargetUnitId = SearchUnitId.New().ToString();
        string unrelatedDocumentId = DocumentInstanceId.New().ToString();
        string unrelatedPageId = PageId.New().ToString();
        string unrelatedUnitId = SearchUnitId.New().ToString();
        await c.InsertMappedFtsRowAsync(c.DocumentInstanceId.ToString(), staleTargetUnitId,
            c.PageId.ToString(), "stale target text");
        await c.InsertMappedFtsRowAsync(unrelatedDocumentId, unrelatedUnitId, unrelatedPageId,
            "unrelated text");

        SearchIndexRebuilder rebuilder = new(c.Database.ConnectionFactory, c.Clock);
        Result rebuilt = await rebuilder.RebuildFtsForDocumentInstanceAsync(c.DocumentInstanceId);

        rebuilt.IsSuccess.Should().BeTrue(rebuilt.ErrorMessage);
        await using SqliteConnection connection = c.Database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        int targetRows = await connection.ExecuteScalarAsync<int>(
            "select count(1) from fts_row_map where document_instance_id = @DocumentId;",
            new { DocumentId = c.DocumentInstanceId.ToString() });
        int unrelatedRows = await connection.ExecuteScalarAsync<int>(
            "select count(1) from fts_row_map where document_instance_id = @DocumentId;",
            new { DocumentId = unrelatedDocumentId });
        string currentUnitId = (await connection.ExecuteScalarAsync<string>(
            "select unit_id from search_units where document_instance_id = @DocumentId;",
            new { DocumentId = c.DocumentInstanceId.ToString() }))!;
        long mappedRowId = await connection.ExecuteScalarAsync<long>(
            "select fts_row_id from fts_row_map where unit_id = @UnitId;",
            new { UnitId = currentUnitId });
        long indexedRowId = await connection.ExecuteScalarAsync<long>(
            "select rowid from search_units_fts where unit_id = @UnitId;",
            new { UnitId = currentUnitId });
        long searchUnitRowId = await connection.ExecuteScalarAsync<long>(
            "select rowid from search_units where unit_id = @UnitId;",
            new { UnitId = currentUnitId });

        targetRows.Should().Be(1);
        unrelatedRows.Should().Be(1);
        (await c.Count("search_units_fts")).Should().Be(2);
        mappedRowId.Should().Be(indexedRowId);
        mappedRowId.Should().NotBe(searchUnitRowId);
    }

    [Fact]
    public async Task Purge_deletes_commits_translations_and_historical_ocr_with_foreign_keys_enabled()
    {
        await using Ctx c = await Ctx.Create();
        ItemId itemId = await c.InsertTrashedItemWithPayloadAsync();
        await c.InsertHistoryAndTranslationRowsAsync();
        await c.InsertHistoricalOcrRowsAsync();

        Result purged = await c.Purge.PurgeItemsAsync([itemId]);

        purged.IsSuccess.Should().BeTrue(purged.ErrorMessage);
        (await c.ForeignKeysEnabled()).Should().Be(1);
        (await c.Count("document_commits")).Should().Be(0);
        (await c.Count("document_commit_pages")).Should().Be(0);
        (await c.Count("page_translations")).Should().Be(0);
        (await c.Count("translation_boxes")).Should().Be(0);
        (await c.Count("ocr_runs")).Should().Be(0);
        (await c.Count("ocr_page_results")).Should().Be(0);
        (await c.Count("ocr_candidate_adoptions")).Should().Be(0);
        (await c.ForeignKeyViolationCount()).Should().Be(0);
    }

    [Fact]
    public async Task Purge_rolls_back_payload_cache_and_revision_when_a_later_delete_fails()
    {
        await using Ctx c = await Ctx.Create();
        ItemId itemId = await c.InsertTrashedItemWithPayloadAsync();
        long revisionBefore = await c.RevisionAsync();
        await c.AddItemDeleteFailureTriggerAsync();

        Result purged = await c.Purge.PurgeItemsAsync([itemId]);

        purged.IsFailure.Should().BeTrue();
        (await c.Count("items")).Should().Be(1);
        (await c.Count("document_instances")).Should().Be(1);
        (await c.Count("search_units")).Should().Be(1);
        (await c.Count("search_units_fts")).Should().Be(1);
        (await c.Count("fts_row_map")).Should().Be(1);
        (await c.Count("item_purge_records")).Should().Be(0);
        (await c.RevisionAsync()).Should().Be(revisionBefore);
        (await c.ForeignKeyViolationCount()).Should().Be(0);
    }

    [Fact]
    public async Task BuildPurgeReports_returns_a_report_for_each_requested_item()
    {
        await using Ctx c = await Ctx.Create();
        ItemId first = await c.InsertTrashedItemWithPayloadAsync();
        ItemId second = await c.InsertTrashedItemWithPayloadAsync();

        Result<IReadOnlyList<ItemPurgeDependencyReport>> reports =
            await c.Purge.BuildPurgeReportsAsync([first, second]);

        reports.IsSuccess.Should().BeTrue(reports.ErrorMessage);
        reports.Value.Select(report => report.ItemId).Should().Equal(first, second);
    }

    [Fact]
    public async Task BuildReport_blocks_when_working_revision_exists()
    {
        await using Ctx c = await Ctx.Create();
        ItemId itemId = await c.InsertTrashedItemWithPayloadAsync();
        await c.BeginWorkingRevisionAsync(itemId);

        Result<ItemPurgeDependencyReport> report = await c.Purge.BuildPurgeReportAsync(itemId);

        report.IsSuccess.Should().BeTrue();
        report.Value.HasWorking.Should().BeTrue();
    }

    [Fact]
    public async Task Purge_rejects_active_items_and_clears_item_satellite_tables()
    {
        await using Ctx c = await Ctx.Create();
        Result<ItemMetadata> active = await c.Items.CreateItemAsync("book", "Still Active");
        Result blocked = await c.Purge.PurgeItemsAsync([active.Value.ItemId]);
        blocked.IsFailure.Should().BeTrue();
        blocked.ErrorCode.Should().Be(AppErrorCodes.NotFound);

        ItemId itemId = await c.InsertTrashedItemWithPayloadAsync();
        await c.Items.AddIdentifierAsync(itemId, "doi", "10.1/purge", null);
        Result purged = await c.Purge.PurgeItemsAsync([itemId]);
        purged.IsSuccess.Should().BeTrue(purged.ErrorMessage);

        (await c.CountForItem("item_identifiers", itemId)).Should().Be(0);
        (await c.CountForItem("item_creators", itemId)).Should().Be(0);
        (await c.CountForItem("item_dates", itemId)).Should().Be(0);
        (await c.Count("items")).Should().Be(1);
        (await c.Count("item_purge_records")).Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scaled_history_purge_profiles_ready_and_recovery_FTS_paths_without_losing_atomicity(
        bool rebuildCache)
    {
        await using Ctx c = await Ctx.Create();
        List<ItemId> ids = [];
        DocumentTreeService trees = BoxTreeTestData.CreateService(c.Database.ConnectionFactory, c.Clock);
        SearchIndexRebuilder index = new(c.Database.ConnectionFactory, c.Clock);
        SearchUnitBuilder units = new(c.Database.ConnectionFactory, c.Clock);
        for (int item = 0; item < 8; item++)
        {
            ids.Add(await c.InsertTrashedItemWithPayloadAsync());
            for (int history = 0; history < 3; history++)
            {
                DocumentBoxSeed[] boxes = Enumerable.Range(0, 200).Select(ordinal => new DocumentBoxSeed(
                    null, null, ordinal, DocumentBoxType.Text, null,
                    new NormalizedBBox(.04 + ordinal % 8 * .115, .04 + ordinal / 8 * .036, .1, .03),
                    new TextBoxPayload($"History {history}, paragraph {ordinal}."))).ToArray();
                DocumentTreeRevision working = (await trees.BeginWorkingRevisionAsync(c.DocumentInstanceId,
                    c.PageId, boxes, DocumentTreeRevisionSource.Import)).Value;
                (await trees.CommitWorkingRevisionAsync(working.TreeRevisionId)).IsSuccess.Should().BeTrue();
            }

            (await units.RebuildForDocumentInstanceAsync(c.DocumentInstanceId)).IsSuccess.Should().BeTrue();
            (await index.RebuildFtsForDocumentInstanceAsync(c.DocumentInstanceId)).IsSuccess.Should().BeTrue();
        }

        (await index.EnsureCacheAsync()).IsSuccess.Should().BeTrue();
        if (rebuildCache)
        {
            await using SqliteConnection connection = c.Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            await connection.ExecuteAsync("delete from fts_cache_state;");
        }

        (await c.Count("document_boxes")).Should().BeGreaterThan(4000);
        (await c.Count("search_units")).Should().BeGreaterThan(1000);
        RecordingPurgeProgress progress = new();
        long before = await c.RevisionAsync();
        int events = 0;
        c.Revisions.ChangeCommitted += (_, _) => Interlocked.Increment(ref events);
        Result<IReadOnlyList<ItemPurgeDependencyReport>> reports =
            await c.Purge.BuildPurgeReportsAsync(ids, progress: progress);
        reports.IsSuccess.Should().BeTrue(reports.ErrorMessage);

        Result result = await c.Purge.PurgeItemsAsync(ids, progress: progress);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        foreach (string table in new[]
                 {
                     "items", "document_instances", "pages", "document_tree_revisions", "document_boxes",
                     "search_units", "search_units_fts", "fts_row_map"
                 })
        {
            (await c.Count(table)).Should().Be(0, table + " payload belongs to the purged batch");
        }

        (await c.ForeignKeyViolationCount()).Should().Be(0);
        (await c.Count("item_purge_records")).Should().Be(ids.Count);
        (await c.RevisionAsync()).Should().Be(before + 1);
        events.Should().Be(1);
        File.Exists(c.OriginalFilePath).Should().BeTrue();
        ItemPurgeProgress[] measured = progress.Updates.Where(update => update.IsCompleted).ToArray();
        measured.Single(update => update.Phase == "fts_prepare").FtsCacheRebuilt.Should().Be(rebuildCache);
        measured.Select(update => update.Phase).Should().Contain(new[]
        {
            "report_total", "snapshot_scan", "write_lease_wait", "sqlite_write_lock", "fts_prepare",
            "fts_delete", "document_history_delete", "commit", "purge_total"
        });
        measured.Should().OnlyContain(update => update.ElapsedMilliseconds >= 0);
        await WritePhaseEvidenceAsync(rebuildCache ? "fts-recovery" : "fts-ready", progress.Updates);
    }

    [Fact]
    public async Task Cancellation_after_FTS_mutation_rolls_back_the_whole_batch_and_revision()
    {
        await using Ctx c = await Ctx.Create();
        ItemId[] ids = [await c.InsertTrashedItemWithPayloadAsync(), await c.InsertTrashedItemWithPayloadAsync()];
        long before = await c.RevisionAsync();
        int boxes = await c.Count("document_boxes");
        using CancellationTokenSource cancellation = new();
        Action requestCancellation = cancellation.Cancel;
        RecordingPurgeProgress progress = new(update =>
        {
            if (update.Phase == "fts_delete" && update.IsCompleted)
            {
                requestCancellation();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            c.Purge.PurgeItemsAsync(ids, cancellation.Token, progress));

        (await c.Count("items")).Should().Be(2);
        (await c.Count("document_boxes")).Should().Be(boxes);
        (await c.Count("search_units_fts")).Should().Be(2);
        (await c.Count("item_purge_records")).Should().Be(0);
        (await c.RevisionAsync()).Should().Be(before);
        progress.Updates.Should().NotContain(update => update.Phase == "commit" && update.IsCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repository_lease_wait_and_native_SQLite_write_lock_wait_are_measured_separately(bool nativeLock)
    {
        await using Ctx c = await Ctx.Create();
        ItemId id = await c.InsertTrashedItemWithPayloadAsync();
        ItemPurgeService purge = c.Purge;
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        string expectedPhase = nativeLock ? "sqlite_write_lock" : "write_lease_wait";
        RecordingPurgeProgress progress = new(update =>
        {
            if (update.Phase == expectedPhase && !update.IsCompleted)
            {
                started.TrySetResult();
            }
        });
        IDisposable? lease = null;
        SqliteConnection? connection = null;
        DbTransaction? transaction = null;
        Task<Result>? pending = null;
        try
        {
            if (nativeLock)
            {
                connection = c.Database.ConnectionFactory.CreateConnection();
                await connection.OpenAsync();
                transaction = await connection.BeginTransactionAsync();
            }
            else
            {
                lease = await c.Database.ConnectionFactory.EnterWriteAsync();
            }

            pending = Task.Run(() => purge.PurgeItemsAsync([id], progress: progress));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(250);
            pending.IsCompleted.Should().BeFalse();
            if (transaction is not null)
            {
                await transaction.CommitAsync();
                await transaction.DisposeAsync();
                transaction = null;
            }

            lease?.Dispose();
            lease = null;
            (await pending.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess.Should().BeTrue();
            progress.Updates.Single(update => update.Phase == expectedPhase && update.IsCompleted)
                .ElapsedMilliseconds.Should().BeGreaterThan(150);
            await WritePhaseEvidenceAsync(expectedPhase, progress.Updates);
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }

            if (connection is not null)
            {
                await connection.DisposeAsync();
            }

            lease?.Dispose();
            if (pending is not null)
            {
                await pending;
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Diagnostic_failure_or_cancellation_cannot_change_report_or_committed_purge_outcomes(
        bool diagnosticCancellation)
    {
        await using Ctx c = await Ctx.Create();
        ItemId id = await c.InsertTrashedItemWithPayloadAsync();
        ThrowingDiagnostics diagnostics = new(diagnosticCancellation);
        ItemPurgeService purge = new(c.Database.ConnectionFactory, c.Clock, c.Library,
            revisions: c.Revisions, logger: diagnostics);

        (await purge.BuildPurgeReportAsync(id, progress: diagnostics)).IsSuccess.Should().BeTrue();
        (await purge.PurgeItemsAsync([id], progress: diagnostics)).IsSuccess.Should().BeTrue();

        diagnostics.LogCalls.Should().BeGreaterThan(0);
        diagnostics.ProgressCalls.Should().BeGreaterThan(0);
        (await c.Count("items")).Should().Be(0);
        (await c.Count("item_purge_records")).Should().Be(1);
        File.Exists(c.OriginalFilePath).Should().BeTrue();
    }

    private sealed class ThrowingDiagnostics(bool cancellation) : IAppLogger, IProgress<ItemPurgeProgress>
    {
        public int LogCalls { get; private set; }
        public int ProgressCalls { get; private set; }

        public Task LogAsync(string operation, string message)
        {
            LogCalls++;
            return Task.FromException(CreateFailure());
        }

        public void Report(ItemPurgeProgress value)
        {
            ProgressCalls++;
            throw CreateFailure();
        }

        private Exception CreateFailure()
        {
            return cancellation
                ? new OperationCanceledException("injected diagnostic cancellation")
                : new IOException("injected diagnostic failure");
        }
    }

    private static async Task WritePhaseEvidenceAsync(string scenario, IEnumerable<ItemPurgeProgress> progress)
    {
        string root = Path.Combine(Testing.RepositoryTestEnvironment.TemporaryRoot, "test-evidence", "item-purge");
        Directory.CreateDirectory(root);
        string json = System.Text.Json.JsonSerializer.Serialize(progress.ToArray());
        Performance.ReportPrivacy.AssertSafe(json);
        await File.WriteAllTextAsync(Path.Combine(root, scenario + "-" + Guid.NewGuid().ToString("N") + ".json"), json);
    }

    private sealed class RecordingPurgeProgress(Action<ItemPurgeProgress>? callback = null)
        : IProgress<ItemPurgeProgress>
    {
        public ConcurrentQueue<ItemPurgeProgress> Updates { get; } = new();

        public void Report(ItemPurgeProgress value)
        {
            Updates.Enqueue(value);
            callback?.Invoke(value);
        }
    }

    private sealed class Ctx : IAsyncDisposable
    {
        private Ctx(
            TemporarySqliteDatabase database,
            FixedClock clock,
            LibraryIdentityService library,
            LibraryRevisionService revisions,
            ItemService items,
            IVersionedEvidenceReader evidence,
            ItemPurgeService purge,
            string originalFilePath)
        {
            Database = database;
            Clock = clock;
            Library = library;
            Revisions = revisions;
            Items = items;
            Evidence = evidence;
            Purge = purge;
            OriginalFilePath = originalFilePath;
        }

        public TemporarySqliteDatabase Database { get; }
        public FixedClock Clock { get; }
        public LibraryIdentityService Library { get; }
        public LibraryRevisionService Revisions { get; }
        public ItemService Items { get; }
        public IVersionedEvidenceReader Evidence { get; }
        public ItemPurgeService Purge { get; }
        public string OriginalFilePath { get; }
        public FileAssetId SourceFileAssetId { get; private set; }
        public FileAssetId PayloadAssetId { get; private set; }
        public DocumentInstanceId DocumentInstanceId { get; private set; }
        public PageId PageId { get; private set; }
        public DocumentTreeRevisionId TreeRevisionId { get; private set; }
        public DocumentBoxId BoxId { get; private set; }

        public static async Task<Ctx> Create()
        {
            TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
            FixedClock clock = new(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            LibraryIdentityService library = new(database.ConnectionFactory, clock);
            LibraryRevisionService revisions = new(database.ConnectionFactory);
            await library.CreateLibraryAsync("Purge Test");
            ItemService items = new(database.ConnectionFactory, library, clock, revisions);
            DocumentTreeService trees = new(database.ConnectionFactory, clock, new MarkdigMarkdownEngine());
            IVersionedEvidenceReader evidence = new VersionedEvidenceReader(
                database.ConnectionFactory,
                library,
                trees,
                new DocumentMarkdownCompiler(trees, new MarkdigMarkdownEngine()));
            ItemPurgeService purge = new(database.ConnectionFactory, clock, library, revisions: revisions);
            string originalFilePath = Path.Combine(Path.GetTempPath(), $"purge-{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(originalFilePath, "original");
            return new Ctx(database, clock, library, revisions, items, evidence, purge, originalFilePath);
        }

        public async ValueTask DisposeAsync()
        {
            if (File.Exists(OriginalFilePath))
            {
                File.Delete(OriginalFilePath);
            }

            await Database.DisposeAsync();
        }

        public async Task<ItemId> InsertTrashedItemWithPayloadAsync()
        {
            Result<ItemMetadata> item = await Items.CreateItemAsync("book", "Purge Me");
            item.IsSuccess.Should().BeTrue();
            await Items.DeleteItemAsync(item.Value.ItemId);

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
                    Current = SearchUnitStatus.Current
                });

            return item.Value.ItemId;
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

        public async Task AddItemDeleteFailureTriggerAsync()
        {
            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                "create trigger reject_purge_items before delete on items begin select raise(abort, 'forced purge failure'); end;");
        }

        public async Task InsertMappedFtsRowAsync(
            string documentId,
            string unitId,
            string pageId,
            string text)
        {
            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                """
                insert into fts_row_map (document_instance_id, unit_id)
                values (@DocumentId, @UnitId);
                insert into search_units_fts (rowid, unit_id, document_instance_id, page_id, resolved_text)
                select fts_row_id, @UnitId, @DocumentId, @PageId, @Text
                from fts_row_map where unit_id = @UnitId;
                """,
                new { DocumentId = documentId, UnitId = unitId, PageId = pageId, Text = text });
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
                values (@ResultId, @RunId, @PageId, @Failed, null, @Now, @Now);

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

        public async Task<string> CreateVersionedEvidenceUriAsync(ItemId itemId)
        {
            _ = itemId;
            return $"patchouli://texts/{DocumentInstanceId}/page-1.md?rev={TreeRevisionId}&box={BoxId}";
        }

        public async Task BeginWorkingRevisionAsync(ItemId itemId)
        {
            _ = itemId;
            DocumentTreeService trees = new(Database.ConnectionFactory, Clock, new MarkdigMarkdownEngine());
            await trees.BeginWorkingRevisionAsync(
                DocumentInstanceId,
                PageId,
                [
                    new DocumentBoxSeed(null, null, 0, DocumentBoxType.Text, null, new NormalizedBBox(.1, .1, .8, .1),
                        new TextBoxPayload("working draft"))
                ],
                DocumentTreeRevisionSource.ManualEdit);
        }

        public async Task InsertActiveOcrRunAsync(ItemId itemId)
        {
            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            string now = Clock.UtcNow.ToString("O");
            string? documentId = await connection.ExecuteScalarAsync<string>(
                "select document_instance_id from document_instances where item_id = @ItemId;",
                new { ItemId = itemId.ToString() });
            string presetId = OcrPresetId.New().ToString();
            string presetVersionId = OcrPresetVersionId.New().ToString();
            await connection.ExecuteAsync(
                """
                insert into ocr_presets (preset_id, library_id, name, description, archived, current_version_id, created_at, updated_at)
                values (@PresetId, @LibraryId, 'Test', 'Test', 0, @PresetVersionId, @Now, @Now);

                insert into ocr_preset_versions (preset_version_id, preset_id, engine_id, model_id, parameters_json, apply_on_success, created_at)
                values (@PresetVersionId, @PresetId, 'mock', 'mock-default', '{}', 0, @Now);

                insert into ocr_runs (ocr_run_id, document_instance_id, preset_id, preset_version_id, engine_id, model_id, parameters_snapshot_json, state, created_at, updated_at)
                values (@RunId, @DocumentId, @PresetId, @PresetVersionId, 'mock', 'mock-default', '{}', @Pending, @Now, @Now);
                """,
                new
                {
                    PresetId = presetId,
                    LibraryId = (await Library.GetCurrentLibraryAsync()).Value.LibraryId.ToString(),
                    PresetVersionId = presetVersionId,
                    RunId = OcrRunId.New().ToString(),
                    DocumentId = documentId,
                    Now = now,
                    Pending = OcrRunState.Pending
                });
        }

        public async Task<int> Count(string table)
        {
            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            return await connection.ExecuteScalarAsync<int>($"select count(1) from {table}");
        }

        public async Task<int> CountForItem(string table, ItemId itemId)
        {
            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            return await connection.ExecuteScalarAsync<int>(
                $"select count(1) from {table} where item_id = @ItemId;",
                new { ItemId = itemId.ToString() });
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
