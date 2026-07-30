using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Evidence;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Evidence;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Search;
using Patchouli.Search;

namespace Patchouli.Tests;

public sealed class EvidenceRefV2Tests
{
    [Fact]
    public async Task Concurrent_creation_returns_the_same_record()
    {
        await using Context context = await Context.CreateAsync();
        await context.CommitTextAsync("concurrent batch evidence");
        SearchUnitId unitId = await context.RebuildAndFindUnitAsync("concurrent batch");

        Result<EvidenceRefRecord>[] results = await Task.WhenAll(
            context.Evidence.CreateFromSearchUnitAsync(unitId),
            context.Evidence.CreateFromSearchUnitAsync(unitId));

        results.Should().OnlyContain(result => result.IsSuccess);
        results.Select(result => result.Value.EvidenceRecordId).Distinct().Should().ContainSingle();
        (await context.CountEvidenceRecordsAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Batch_creation_preserves_order_duplicates_existing_records_and_missing_units()
    {
        await using Context context = await Context.CreateAsync();
        await context.CommitTextAsync("first batch evidence");
        SearchUnitId firstUnitId = await context.RebuildAndFindUnitAsync("first batch");
        EvidenceRefRecord existing = (await context.Evidence.CreateFromSearchUnitAsync(firstUnitId)).Value;
        await context.EditCurrentBoxAsync("second batch evidence");
        SearchUnitId secondUnitId = await context.RebuildAndFindUnitAsync("second batch");
        SearchUnitId missingUnitId = SearchUnitId.New();

        Result<IReadOnlyList<EvidenceReferenceCreateResult>> result =
            await context.Evidence.CreateFromSearchUnitsAsync(
                [secondUnitId, missingUnitId, firstUnitId, secondUnitId]);

        result.IsSuccess.Should().BeTrue();
        result.Value.Select(item => item.SearchUnitId).Should()
            .Equal(secondUnitId, missingUnitId, firstUnitId, secondUnitId);
        result.Value[0].Result.Value.PinnedText.Should().Be("second batch evidence");
        result.Value[1].Result.IsFailure.Should().BeTrue();
        result.Value[1].Result.ErrorCode.Should().Be(AppErrorCodes.NotFound);
        result.Value[1].Result.ErrorMessage.Should().Be("Search unit was not found.");
        result.Value[2].Result.Value.EvidenceRecordId.Should().Be(existing.EvidenceRecordId);
        result.Value[2].Result.Value.EvidenceRefId.Should().Be(existing.EvidenceRefId);
        result.Value[2].Result.Value.PinnedText.Should().Be(existing.PinnedText);
        result.Value[3].Result.Value.Should().Be(result.Value[0].Result.Value);
        (await context.CountEvidenceRecordsAsync()).Should().Be(2);
    }

    [Fact]
    public async Task V2_reference_pins_original_box_text_and_follows_its_successor_when_current()
    {
        await using Context context = await Context.CreateAsync();
        DocumentTreeRevision initial = await context.CommitTextAsync("original evidence text");
        SearchUnitId unitId = await context.RebuildAndFindUnitAsync("original");
        EvidenceRefRecord record = (await context.Evidence.CreateFromSearchUnitAsync(unitId)).Value;

        EvidenceReference decoded = EvidenceReferenceCodec.Decode(record.EvidenceRefId).Value;
        decoded.TreeRevisionId.Should().Be(initial.TreeRevisionId);
        decoded.BoxId.Should().Be(record.BoxId);
        record.EvidenceRefId.Should().StartWith("evref:v2:").And.NotContain("original evidence text");

        await context.EditCurrentBoxAsync("corrected evidence text");
        await context.RebuildAndFindUnitAsync("corrected");

        EvidenceResolutionResult pinned = (await context.Evidence.ResolveAsync(record.EvidenceRefId)).Value;
        EvidenceResolutionResult current =
            (await context.Evidence.ResolveAsync(record.EvidenceRefId, EvidenceResolutionMode.Current)).Value;
        EvidenceResolutionResult compared =
            (await context.Evidence.ResolveAsync(record.EvidenceRefId, EvidenceResolutionMode.Compare)).Value;

        pinned.Status.Should().Be(EvidenceResolutionStatus.Superseded);
        pinned.PinnedText.Should().Be("original evidence text");
        current.Status.Should().Be(EvidenceResolutionStatus.FoundCurrent);
        current.CurrentText.Should().Be("corrected evidence text");
        compared.HasTextChanged.Should().BeTrue();
        compared.HasLayoutChanged.Should().BeTrue();
    }

    [Fact]
    public async Task Rebuild_links_changed_box_id_and_reports_bbox_drift()
    {
        await using Context context = await Context.CreateAsync();
        await context.CommitTextAsync("stable evidence text");
        SearchUnitId unitId = await context.RebuildAndFindUnitAsync("stable");
        EvidenceRefRecord record = (await context.Evidence.CreateFromSearchUnitAsync(unitId)).Value;

        DocumentBoxId replacementId = DocumentBoxId.New();
        await context.ReplaceCurrentBoxAsync(replacementId, "stable evidence text", new NormalizedBBox(.2, .2, .7, .1));
        await context.RebuildAndFindUnitAsync("stable");

        EvidenceResolutionResult current =
            (await context.Evidence.ResolveAsync(record.EvidenceRefId, EvidenceResolutionMode.Current)).Value;

        current.Status.Should().Be(EvidenceResolutionStatus.FoundCurrent);
        current.HasBboxChanged.Should().BeTrue();
        EvidenceReferenceCodec.Decode(current.EvidenceRefId).Value.BoxId.Should().Be(replacementId);
        current.ChainSummary.Should().NotBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deletion_creates_canonical_revision_and_rebuild_cannot_resurrect_content(bool purge)
    {
        await using Context context = await Context.CreateAsync();
        DocumentTreeRevision original = await context.CommitTextAsync("deleted unique evidence");
        SearchUnitId unitId = await context.RebuildAndFindUnitAsync("deleted unique");
        EvidenceRefRecord record = (await context.Evidence.CreateFromSearchUnitAsync(unitId)).Value;

        if (purge)
        {
            (await context.Evidence.PurgeAsync(record.EvidenceRefId, "privacy request")).IsSuccess.Should().BeTrue();
        }
        else
        {
            (await context.Evidence.TombstoneAsync(record.EvidenceRefId, "source retracted")).IsSuccess.Should()
                .BeTrue();
        }

        DocumentTreeRevision current =
            (await context.Trees.GetCurrentRevisionAsync(context.Document.DocumentInstanceId, context.Page.PageId))
            .Value;
        current.ParentTreeRevisionId.Should().Be(original.TreeRevisionId);
        (await context.Trees.ListBoxesAsync(original.TreeRevisionId)).Value.Single().Suppressed.Should().BeFalse();
        IReadOnlyList<DocumentBox> currentBoxes = (await context.Trees.ListBoxesAsync(current.TreeRevisionId)).Value;
        if (purge)
        {
            currentBoxes.Should().BeEmpty();
        }
        else
        {
            currentBoxes.Should().ContainSingle().Which.Suppressed.Should().BeTrue();
        }

        (await context.Units.RebuildForDocumentInstanceAsync(context.Document.DocumentInstanceId)).IsSuccess
            .Should().BeTrue();
        await context.RebuildIndexAsync();
        (await context.SearchAsync("deleted unique")).Value.Results.Should().BeEmpty();
        (await context.GetSearchUnitStatusAsync(unitId)).Should().Be(purge
            ? SearchUnitStatus.Deleted
            : SearchUnitStatus.Hidden);
    }

    private sealed class Context : IAsyncDisposable
    {
        private readonly TemporarySqliteDatabase _database;
        private readonly SearchIndexRebuilder _index;
        private readonly SqliteSearchService _search;

        private Context(
            TemporarySqliteDatabase database,
            DocumentInstance document,
            Page page,
            DocumentTreeService trees,
            SearchUnitBuilder units,
            EvidenceReferenceService evidence,
            SearchIndexRebuilder index,
            SqliteSearchService search)
        {
            _database = database;
            Document = document;
            Page = page;
            Trees = trees;
            Units = units;
            Evidence = evidence;
            _index = index;
            _search = search;
        }

        public DocumentInstance Document { get; }
        public Page Page { get; }
        public DocumentTreeService Trees { get; }
        public SearchUnitBuilder Units { get; }
        public EvidenceReferenceService Evidence { get; }

        public static async Task<Context> CreateAsync()
        {
            TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
            FixedClock clock = new(DateTimeOffset.Parse("2026-07-13T00:00:00Z"));
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            LibraryIdentityService libraries = new(database.ConnectionFactory, clock);
            await libraries.CreateLibraryAsync("Evidence v2");
            ItemMetadata item = (await new ItemService(database.ConnectionFactory, libraries, clock)
                .CreateItemAsync("document", "Evidence v2")).Value;
            DocumentInstance document = (await new DocumentInstanceService(database.ConnectionFactory, clock)
                .AttachDocumentInstanceAsync(item.ItemId, null, DocumentInstanceType.PrimaryScan)).Value;
            Page page = (await new Infrastructure.Layout.PageService(database.ConnectionFactory, clock)
                .CreatePageAsync(document.DocumentInstanceId, 0, "1", null, null, 0,
                    CoordinateBasis.NormalizedPage, null, null, "test", null)).Value;
            MarkdigMarkdownEngine markdown = new();
            DocumentTreeService trees = new(database.ConnectionFactory, clock, markdown);
            SearchUnitBuilder units = new(database.ConnectionFactory, clock, markdown);
            return new Context(
                database,
                document,
                page,
                trees,
                units,
                new EvidenceReferenceService(database.ConnectionFactory, clock),
                new SearchIndexRebuilder(database.ConnectionFactory, clock),
                new SqliteSearchService(database.ConnectionFactory));
        }

        public async Task<DocumentTreeRevision> CommitTextAsync(string text)
        {
            DocumentTreeRevision staged = (await Trees.StagePageAsync(
                Document.DocumentInstanceId,
                Page.PageId,
                [
                    new DocumentBoxSeed(null, null, 0, DocumentBoxType.Text, null, null,
                        new NormalizedBBox(.1, .1, .8, .1), new TextBoxPayload(text))
                ])).Value;
            return (await Trees.AdoptStagingRevisionAsync(staged.TreeRevisionId)).Value;
        }

        public async Task EditCurrentBoxAsync(string text)
        {
            PageEditSession edit = (await Trees.BeginPageEditAsync(Document.DocumentInstanceId, Page.PageId)).Value;
            DocumentBox box = (await Trees.ListBoxesAsync(edit.DraftRevisionId)).Value.Single();
            await Trees.UpdateLeafAsync(
                edit.SessionId,
                new UpdateLeafCommand(box.BoxId, DocumentBoxType.Text, new TextBoxPayload(text)));
            (await Trees.CommitPageEditAsync(edit.SessionId)).IsSuccess.Should().BeTrue();
        }

        public async Task ReplaceCurrentBoxAsync(DocumentBoxId boxId, string text, NormalizedBBox bbox)
        {
            DocumentTreeRevision current =
                (await Trees.GetCurrentRevisionAsync(Document.DocumentInstanceId, Page.PageId)).Value;
            DocumentTreeRevision staged = (await Trees.StagePageAsync(
                Document.DocumentInstanceId,
                Page.PageId,
                [
                    new DocumentBoxSeed(boxId, null, 0, DocumentBoxType.Text, null, null, bbox,
                        new TextBoxPayload(text))
                ],
                parentTreeRevisionId: current.TreeRevisionId)).Value;
            (await Trees.AdoptStagingRevisionAsync(staged.TreeRevisionId)).IsSuccess.Should().BeTrue();
        }

        public async Task<SearchUnitId> RebuildAndFindUnitAsync(string uniqueToken)
        {
            await Units.RebuildForDocumentInstanceAsync(Document.DocumentInstanceId);
            await _index.RebuildFtsForDocumentInstanceAsync(Document.DocumentInstanceId);
            return (await _search.SearchLibraryAsync(new SearchRequest(uniqueToken))).Value
                .Results.Single().MatchedUnits.Single().UnitId;
        }

        public Task<Result<SearchResultPage>> SearchAsync(string query)
        {
            return _search.SearchLibraryAsync(new SearchRequest(query));
        }

        public Task<Result> RebuildIndexAsync()
        {
            return _index.RebuildFtsForDocumentInstanceAsync(Document.DocumentInstanceId);
        }

        public async Task<string?> GetSearchUnitStatusAsync(SearchUnitId unitId)
        {
            await using SqliteConnection connection = _database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            return await connection.ExecuteScalarAsync<string?>(
                "select status from search_units where unit_id = @UnitId;", new { UnitId = unitId.ToString() });
        }

        public async Task<int> CountEvidenceRecordsAsync()
        {
            await using SqliteConnection connection = _database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            return await connection.ExecuteScalarAsync<int>("select count(1) from evidence_ref_records;");
        }

        public ValueTask DisposeAsync()
        {
            return _database.DisposeAsync();
        }
    }
}
