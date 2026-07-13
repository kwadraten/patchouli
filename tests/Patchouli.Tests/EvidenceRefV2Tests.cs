using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Library;
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

        public async Task<SearchUnitId> RebuildAndFindUnitAsync(string uniqueToken)
        {
            await Units.RebuildForDocumentInstanceAsync(Document.DocumentInstanceId);
            await _index.RebuildFtsForDocumentInstanceAsync(Document.DocumentInstanceId);
            return (await _search.SearchLibraryAsync(new SearchRequest(uniqueToken))).Value
                .Results.Single().MatchedUnits.Single().UnitId;
        }

        public ValueTask DisposeAsync()
        {
            return _database.DisposeAsync();
        }
    }
}
