using System.Text;
using Dapper;
using FluentAssertions;
using LiteratureApp.Core.Bibliography;
using LiteratureApp.Core.Documents;
using LiteratureApp.Core.Ids;
using LiteratureApp.Core.Layout;
using LiteratureApp.Core.Results;
using LiteratureApp.Evidence;
using LiteratureApp.Infrastructure.Bibliography;
using LiteratureApp.Infrastructure.Documents;
using LiteratureApp.Infrastructure.Evidence;
using LiteratureApp.Infrastructure.Layout;
using LiteratureApp.Infrastructure.LibraryIdentity;
using LiteratureApp.Infrastructure.Migrations;
using LiteratureApp.Infrastructure.Search;
using LiteratureApp.Search;
using Microsoft.Data.Sqlite;

namespace LiteratureApp.Tests;

public sealed class EvidenceReferenceServiceTests
{
    [Fact] public async Task CreateFromSearchUnit_generates_evref_v1() { await using var c = await EvidenceTestContext.CreateWithUnitAsync("Pinned text"); var record = await c.Evidence.CreateFromSearchUnitAsync(c.UnitId); record.Value.EvidenceRefId.Should().StartWith("evref:v1:"); }
    [Fact] public async Task Evref_round_trip_decodes_all_required_ids() { await using var c = await EvidenceTestContext.CreateWithUnitAsync("Pinned text"); var record = await c.Evidence.CreateFromSearchUnitAsync(c.UnitId); var decoded = EvidenceReferenceCodec.Decode(record.Value.EvidenceRefId); decoded.Value.LibraryId.Should().Be(c.LibraryId); decoded.Value.DocumentInstanceId.Should().Be(c.DocumentInstanceId); decoded.Value.PageId.Should().Be(c.PageId); decoded.Value.SearchUnitId.Should().Be(c.UnitId); decoded.Value.LayoutRevisionId.Should().Be(c.RevisionId); }
    [Fact] public async Task Decode_invalid_prefix_returns_invalid_ref() { await using var c = await EvidenceTestContext.CreateAsync(); var result = await c.Evidence.ResolveAsync("evidence://v1/bad"); result.Value.Status.Should().Be(EvidenceResolutionStatus.InvalidRef); }
    [Fact] public async Task Decode_invalid_payload_returns_invalid_ref() { await using var c = await EvidenceTestContext.CreateAsync(); var result = await c.Evidence.ResolveAsync("evref:v1:not-valid"); result.Value.Status.Should().Be(EvidenceResolutionStatus.InvalidRef); }
    [Fact] public async Task Evref_payload_does_not_contain_local_path_or_secret() { await using var c = await EvidenceTestContext.CreateWithUnitAsync("Pinned text"); await c.Connection().ExecuteAsync("insert into file_assets (file_asset_id, library_id, original_path, file_name, size_bytes, status, created_at, updated_at) values (@Id,@Lib,@Path,'secret.pdf',0,'missing',@Now,@Now);", new { Id = FileAssetId.New().ToString(), Lib = c.LibraryId.ToString(), Path = "/Users/me/provider-secret/cache/original.pdf", Now = DateTimeOffset.UtcNow.ToString("O") }); var record = await c.Evidence.CreateFromSearchUnitAsync(c.UnitId); record.Value.EvidenceRefId.Should().NotContain("/Users").And.NotContain("secret").And.NotContain("cache").And.NotContain("original.pdf"); Encoding.UTF8.GetString(Convert.FromBase64String(Pad(record.Value.EvidenceRefId["evref:v1:".Length..].Replace('-', '+').Replace('_', '/')))).Should().NotContain("/Users").And.NotContain("secret"); }
    [Fact] public async Task Resolve_pinned_returns_pinned_text() { await using var c = await EvidenceTestContext.CreateWithUnitAsync("Pinned text"); var record = await c.Evidence.CreateFromSearchUnitAsync(c.UnitId); var resolved = await c.Evidence.ResolveAsync(record.Value.EvidenceRefId); resolved.Value.Status.Should().Be(EvidenceResolutionStatus.FoundPinned); resolved.Value.PinnedText.Should().Be("Pinned text"); }
    [Fact] public async Task Pinned_text_does_not_change_after_search_unit_text_update() { await using var c = await EvidenceTestContext.CreateWithUnitAsync("Pinned text"); var record = await c.Evidence.CreateFromSearchUnitAsync(c.UnitId); await c.UpdateUnitTextAsync("Changed text"); var resolved = await c.Evidence.ResolveAsync(record.Value.EvidenceRefId); resolved.Value.PinnedText.Should().Be("Pinned text"); }
    [Fact] public async Task CreateMarkdown_uses_pinned_text() { await using var c = await EvidenceTestContext.CreateWithUnitAsync("Pinned text"); var record = await c.Evidence.CreateFromSearchUnitAsync(c.UnitId); await c.UpdateUnitTextAsync("Changed text"); var markdown = await c.Evidence.CreateMarkdownAsync(record.Value.EvidenceRefId); markdown.Value.Markdown.Should().Contain("Pinned text").And.NotContain("Changed text"); }
    [Fact] public async Task CreateMarkdown_includes_source_title_page_and_evref() { await using var c = await EvidenceTestContext.CreateWithUnitAsync("Pinned text"); var record = await c.Evidence.CreateFromSearchUnitAsync(c.UnitId); var markdown = await c.Evidence.CreateMarkdownAsync(record.Value.EvidenceRefId); markdown.Value.Markdown.Should().Contain("《Evidence Item》").And.Contain("p. 1").And.Contain(record.Value.EvidenceRefId); }
    [Fact] public async Task Resolve_returns_library_mismatch_for_other_library() { await using var c = await EvidenceTestContext.CreateWithUnitAsync("Pinned text"); var record = await c.Evidence.CreateFromSearchUnitAsync(c.UnitId); await using var other = await EvidenceTestContext.CreateAsync(); var resolved = await other.Evidence.ResolveAsync(record.Value.EvidenceRefId); resolved.Value.Status.Should().Be(EvidenceResolutionStatus.LibraryMismatch); }
    [Fact] public async Task Resolve_returns_not_found_for_missing_record() { await using var c = await EvidenceTestContext.CreateWithUnitAsync("Pinned text"); var evref = EvidenceReferenceCodec.Encode(new EvidenceReference(c.LibraryId, c.DocumentInstanceId, c.PageId, c.UnitId, c.RevisionId.ToString(), c.RevisionId.ToString(), c.RevisionId)).Value; var resolved = await c.Evidence.ResolveAsync(evref); resolved.Value.Status.Should().Be(EvidenceResolutionStatus.NotFound); }
    [Fact] public async Task Resolve_current_returns_current_search_unit_text_for_active_ref() { await using var c = await EvidenceTestContext.CreateWithUnitAsync("Pinned text"); var record = await c.Evidence.CreateFromSearchUnitAsync(c.UnitId); await c.UpdateUnitTextAsync("Current text"); var resolved = await c.Evidence.ResolveAsync(record.Value.EvidenceRefId, EvidenceResolutionMode.Current); resolved.Value.CurrentText.Should().Be("Current text"); }
    [Fact] public async Task Compare_detects_text_change() { await using var c = await EvidenceTestContext.CreateWithUnitAsync("Pinned text"); var record = await c.Evidence.CreateFromSearchUnitAsync(c.UnitId); await c.UpdateUnitTextAsync("Changed"); var resolved = await c.Evidence.ResolveAsync(record.Value.EvidenceRefId, EvidenceResolutionMode.Compare); resolved.Value.HasTextChanged.Should().BeTrue(); }
    [Fact] public async Task Compare_detects_layout_revision_change() { await using var c = await EvidenceTestContext.CreateWithUnitAsync("Pinned text"); var record = await c.Evidence.CreateFromSearchUnitAsync(c.UnitId); var newRevision = await c.Layout.CreateLayoutRevisionAsync(c.DocumentInstanceId, LayoutRevisionSource.Manual); await c.Connection().ExecuteAsync("update search_units set layout_revision_id=@Revision where unit_id=@Unit;", new { Revision = newRevision.Value.LayoutRevisionId.ToString(), Unit = c.UnitId.ToString() }); var resolved = await c.Evidence.ResolveAsync(record.Value.EvidenceRefId, EvidenceResolutionMode.Compare); resolved.Value.HasLayoutChanged.Should().BeTrue(); }
    [Fact] public async Task Compare_detects_bbox_revision_change() { await using var c = await EvidenceTestContext.CreateWithUnitAsync("Pinned text"); var record = await c.Evidence.CreateFromSearchUnitAsync(c.UnitId); await c.Connection().ExecuteAsync("update search_units set bbox_revision_id='bbox-v2' where unit_id=@Unit;", new { Unit = c.UnitId.ToString() }); var resolved = await c.Evidence.ResolveAsync(record.Value.EvidenceRefId, EvidenceResolutionMode.Compare); resolved.Value.HasBboxChanged.Should().BeTrue(); }
    [Fact] public async Task MarkSuperseded_sets_status_and_successor() { await using var c = await EvidenceTestContext.CreateWithUnitAsync("First"); var first = await c.Evidence.CreateFromSearchUnitAsync(c.UnitId); var secondUnit = await c.CreateAdditionalUnitAsync("Second", 2); var second = await c.Evidence.CreateFromSearchUnitAsync(secondUnit); await c.Evidence.MarkSupersededAsync(first.Value.EvidenceRefId, second.Value.EvidenceRefId, EvidenceSuccessorReason.TextUpdated); (await c.RecordStatusAsync(first.Value.EvidenceRefId)).Should().Be(EvidenceRecordStatus.Superseded); }
    [Fact] public async Task Resolve_pinned_superseded_returns_superseded_and_successor_refs_without_auto_adopting() { await using var c = await EvidenceTestContext.CreateWithUnitAsync("First"); var first = await c.Evidence.CreateFromSearchUnitAsync(c.UnitId); var second = await c.Evidence.CreateFromSearchUnitAsync(await c.CreateAdditionalUnitAsync("Second", 2)); await c.Evidence.MarkSupersededAsync(first.Value.EvidenceRefId, second.Value.EvidenceRefId, EvidenceSuccessorReason.TextUpdated); var resolved = await c.Evidence.ResolveAsync(first.Value.EvidenceRefId); resolved.Value.Status.Should().Be(EvidenceResolutionStatus.Superseded); resolved.Value.SuccessorEvidenceRefs.Should().Equal(second.Value.EvidenceRefId); resolved.Value.PinnedText.Should().Be("First"); }
    [Fact] public async Task Resolve_current_follows_successor_chain() { await using var c = await EvidenceTestContext.CreateWithUnitAsync("First"); var first = await c.Evidence.CreateFromSearchUnitAsync(c.UnitId); var second = await c.Evidence.CreateFromSearchUnitAsync(await c.CreateAdditionalUnitAsync("Second", 2)); await c.Evidence.MarkSupersededAsync(first.Value.EvidenceRefId, second.Value.EvidenceRefId, EvidenceSuccessorReason.TextUpdated); var resolved = await c.Evidence.ResolveAsync(first.Value.EvidenceRefId, EvidenceResolutionMode.Current); resolved.Value.Status.Should().Be(EvidenceResolutionStatus.FoundCurrent); resolved.Value.CurrentText.Should().Be("Second"); resolved.Value.ChainSummary.Should().Contain(first.Value.EvidenceRefId); }
    [Fact] public async Task Resolve_current_stops_at_max_chain_depth_with_warning() { await using var c = await EvidenceTestContext.CreateWithUnitAsync("0"); var firstRecord = await c.Evidence.CreateFromSearchUnitAsync(c.UnitId); var previous = firstRecord; for (var i = 1; i <= 21; i++) { var next = await c.Evidence.CreateFromSearchUnitAsync(await c.CreateAdditionalUnitAsync(i.ToString(), i + 1)); await c.Evidence.MarkSupersededAsync(previous.Value.EvidenceRefId, next.Value.EvidenceRefId, EvidenceSuccessorReason.Manual); previous = next; } var resolved = await c.Evidence.ResolveAsync(firstRecord.Value.EvidenceRefId, EvidenceResolutionMode.Current); resolved.Value.Warning.Should().Contain("maximum depth"); }
    [Fact] public async Task Tombstone_returns_tombstoned() { await using var c = await EvidenceTestContext.CreateWithUnitAsync("Pinned"); var record = await c.Evidence.CreateFromSearchUnitAsync(c.UnitId); await c.Evidence.TombstoneAsync(record.Value.EvidenceRefId, "removed"); (await c.Evidence.ResolveAsync(record.Value.EvidenceRefId)).Value.Status.Should().Be(EvidenceResolutionStatus.Tombstoned); }
    [Fact] public async Task Purge_returns_purged_and_does_not_resurrect_text() { await using var c = await EvidenceTestContext.CreateWithUnitAsync("Sensitive"); var record = await c.Evidence.CreateFromSearchUnitAsync(c.UnitId); await c.Evidence.PurgeAsync(record.Value.EvidenceRefId, "purge"); var resolved = await c.Evidence.ResolveAsync(record.Value.EvidenceRefId); resolved.Value.Status.Should().Be(EvidenceResolutionStatus.Purged); resolved.Value.PinnedText.Should().Be("[purged]"); }
    [Fact] public async Task CreateFromSearchUnit_after_SearchLibrary_result_works() { await using var c = await EvidenceTestContext.CreateWithUnitAsync("needle text"); await c.Rebuilder.RebuildFtsForLibraryAsync(); var search = await c.Search.SearchLibraryAsync(new SearchRequest("needle")); var unit = search.Value.Results.Single().MatchedUnits.Single().UnitId; var record = await c.Evidence.CreateFromSearchUnitAsync(unit); record.Value.EvidenceRefId.Should().StartWith("evref:v1:"); }
    [Fact] public async Task SearchLibrary_default_does_not_mass_create_evidence_refs() { await using var c = await EvidenceTestContext.CreateWithUnitAsync("needle text"); await c.Rebuilder.RebuildFtsForLibraryAsync(); await c.Search.SearchLibraryAsync(new SearchRequest("needle")); (await c.CountEvidenceRecordsAsync()).Should().Be(0); }
    [Fact] public async Task MigrationRunner_applies_evidence_migration() { await using var db = TemporarySqliteDatabase.Create(); await new MigrationRunner(db.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync(); await using var cn = db.ConnectionFactory.CreateConnection(); await cn.OpenAsync(); (await cn.ExecuteScalarAsync<int>("select count(1) from sqlite_master where name in ('evidence_ref_records','evidence_successors');")).Should().Be(2); }
    [Fact] public async Task Foreign_keys_prevent_orphan_evidence_record() { await using var c = await EvidenceTestContext.CreateAsync(); var act = async () => await c.Connection().ExecuteAsync("insert into evidence_ref_records (evidence_record_id,evidence_ref_id,library_id,document_instance_id,page_id,unit_id,text_revision_id,bbox_revision_id,layout_revision_id,pinned_text,source_title,page_index,status,created_at) values (@Id,'evref:v1:abc',@Lib,@Doc,@Page,@Unit,'t','b',@Rev,'x','title',0,'active',@Now);", new { Id = Guid.NewGuid().ToString("D"), Lib = c.LibraryId.ToString(), Doc = DocumentInstanceId.New().ToString(), Page = PageId.New().ToString(), Unit = SearchUnitId.New().ToString(), Rev = LayoutRevisionId.New().ToString(), Now = DateTimeOffset.UtcNow.ToString("O") }); await act.Should().ThrowAsync<SqliteException>(); }
    [Fact] public async Task EvidenceMarkdown_uses_pinned_not_current_text() { await using var c = await EvidenceTestContext.CreateWithUnitAsync("Pinned"); var record = await c.Evidence.CreateFromSearchUnitAsync(c.UnitId); await c.UpdateUnitTextAsync("Current"); var markdown = await c.Evidence.CreateMarkdownAsync(record.Value.EvidenceRefId); markdown.Value.Markdown.Should().Contain("Pinned").And.NotContain("Current"); }
    [Fact] public async Task Evidence_ref_does_not_expose_file_asset_original_path() { await using var c = await EvidenceTestContext.CreateWithUnitAsync("Pinned"); var record = await c.Evidence.CreateFromSearchUnitAsync(c.UnitId); record.Value.EvidenceRefId.Should().NotContain("original_path").And.NotContain("/tmp/"); }
    [Fact] public async Task Evidence_ref_does_not_expose_resolved_path() { await using var c = await EvidenceTestContext.CreateWithUnitAsync("Pinned"); var record = await c.Evidence.CreateFromSearchUnitAsync(c.UnitId); record.Value.EvidenceRefId.Should().NotContain("resolved_path").And.NotContain("file://"); }
    [Fact] public async Task Evidence_ref_does_not_expose_provider_or_model_path() { await using var c = await EvidenceTestContext.CreateWithUnitAsync("Pinned"); var record = await c.Evidence.CreateFromSearchUnitAsync(c.UnitId); record.Value.EvidenceRefId.Should().NotContain("provider").And.NotContain("model_path").And.NotContain("secret"); }

    private static string Pad(string value) => value + new string('=', (4 - value.Length % 4) % 4);

    private sealed class EvidenceTestContext : IAsyncDisposable
    {
        private EvidenceTestContext(TemporarySqliteDatabase database, FixedClock clock, LibraryId libraryId, DocumentInstanceId documentInstanceId, PageId pageId, LayoutRevisionId revisionId, SearchUnitId unitId)
        {
            Database = database; Clock = clock; LibraryId = libraryId; DocumentInstanceId = documentInstanceId; PageId = pageId; RevisionId = revisionId; UnitId = unitId;
            Layout = new LayoutTreeService(database.ConnectionFactory, clock);
            Builder = new SearchUnitBuilder(database.ConnectionFactory, clock);
            Rebuilder = new SearchIndexRebuilder(database.ConnectionFactory, clock);
            Search = new SqliteSearchService(database.ConnectionFactory);
            Evidence = new EvidenceReferenceService(database.ConnectionFactory, clock);
        }
        public TemporarySqliteDatabase Database { get; }
        public FixedClock Clock { get; }
        public LibraryId LibraryId { get; }
        public DocumentInstanceId DocumentInstanceId { get; }
        public PageId PageId { get; }
        public LayoutRevisionId RevisionId { get; }
        public SearchUnitId UnitId { get; private set; }
        public LayoutTreeService Layout { get; }
        public SearchUnitBuilder Builder { get; }
        public SearchIndexRebuilder Rebuilder { get; }
        public SqliteSearchService Search { get; }
        public EvidenceReferenceService Evidence { get; }

        public static async Task<EvidenceTestContext> CreateAsync()
        {
            var db = TemporarySqliteDatabase.Create();
            var clock = new FixedClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await new MigrationRunner(db.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            var library = new LibraryIdentityService(db.ConnectionFactory, clock);
            var createdLibrary = await library.CreateLibraryAsync("Evidence Library");
            var item = await new ItemService(db.ConnectionFactory, library, clock).CreateItemAsync("book", "Evidence Item");
            var doc = await new DocumentInstanceService(db.ConnectionFactory, clock).AttachDocumentInstanceAsync(item.Value.ItemId, null, DocumentInstanceType.PrimaryScan);
            var page = await new PageService(db.ConnectionFactory, clock).CreatePageAsync(doc.Value.DocumentInstanceId, 0, "1", null, null, 0, CoordinateBasis.NormalizedPage, null, null, "renderer-v1", null);
            var layout = new LayoutTreeService(db.ConnectionFactory, clock);
            var revision = await layout.CreateLayoutRevisionAsync(doc.Value.DocumentInstanceId, LayoutRevisionSource.Mock, makeCurrent: true);
            return new EvidenceTestContext(db, clock, createdLibrary.Value.LibraryId, doc.Value.DocumentInstanceId, page.Value.PageId, revision.Value.LayoutRevisionId, default);
        }

        public static async Task<EvidenceTestContext> CreateWithUnitAsync(string text)
        {
            var c = await CreateAsync();
            c.UnitId = await c.CreateAdditionalUnitAsync(text, 1);
            return c;
        }

        public async Task<SearchUnitId> CreateAdditionalUnitAsync(string text, int readingOrder)
        {
            await Layout.AddNodeAsync(RevisionId, PageId, null, LayoutNodeType.Paragraph, new NormalizedBBox(0.01 * readingOrder, 0.01 * readingOrder, 0.001, 0.001), text, TextPolicy.Own, readingOrder, LayoutNodeSource.Mock);
            await Builder.RebuildForDocumentInstanceAsync(DocumentInstanceId);
            var id = await Connection().ExecuteScalarAsync<string>("select unit_id from search_units where resolved_text=@Text order by created_at desc limit 1;", new { Text = text });
            return SearchUnitId.Parse(id!);
        }

        public async Task UpdateUnitTextAsync(string text)
            => await Connection().ExecuteAsync("update search_units set resolved_text=@Text where unit_id=@Unit;", new { Text = text, Unit = UnitId.ToString() });
        public async Task<string?> RecordStatusAsync(string evref)
            => await Connection().ExecuteScalarAsync<string?>("select status from evidence_ref_records where evidence_ref_id=@Ref;", new { Ref = evref });
        public async Task<int> CountEvidenceRecordsAsync()
            => await Connection().ExecuteScalarAsync<int>("select count(1) from evidence_ref_records;");
        public SqliteConnection Connection() { var c = Database.ConnectionFactory.CreateConnection(); c.Open(); return c; }
        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }
}
