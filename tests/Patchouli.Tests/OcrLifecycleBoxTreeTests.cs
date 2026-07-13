using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Layout;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Infrastructure.Search;
using Patchouli.Ocr;

namespace Patchouli.Tests;

public sealed class OcrLifecycleBoxTreeTests
{
    [Fact]
    public async Task Document_ocr_stages_each_physical_page_and_explicit_adoption_makes_them_current()
    {
        await using Context context = await Context.CreateAsync();
        OcrPreset preset = (await context.Presets.CreatePresetAsync(
            "Mock", null, OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", false)).Value;

        Result<OcrRun> run = await context.Coordinator.RunPresetOnDocumentAsync(
            context.Document.DocumentInstanceId, preset.PresetId);

        run.IsSuccess.Should().BeTrue(run.ErrorMessage);
        run.Value.State.Should().Be(OcrRunState.Completed);
        IReadOnlyList<OcrPageResult> results =
            (await context.Coordinator.ListPageResultsAsync(run.Value.OcrRunId)).Value;
        results.Should().HaveCount(2).And.OnlyContain(result =>
            result.State == OcrPageResultState.Succeeded && result.StagingTreeRevisionId.HasValue);
        foreach (Page page in context.Pages)
        {
            (await context.Trees.GetCurrentRevisionAsync(context.Document.DocumentInstanceId, page.PageId)).IsFailure
                .Should().BeTrue();
        }

        Result<OcrCandidateAdoption> adopted = await context.Coordinator.AdoptCandidateRunAsync(run.Value.OcrRunId);

        adopted.IsSuccess.Should().BeTrue(adopted.ErrorMessage);
        adopted.Value.AdoptedTreeRevisionIds.Should().HaveCount(2);
        foreach (Page page in context.Pages)
        {
            DocumentTreeRevision current =
                (await context.Trees.GetCurrentRevisionAsync(context.Document.DocumentInstanceId, page.PageId)).Value;
            current.Status.Should().Be(DocumentTreeRevisionStatus.Committed);
            current.IsCurrent.Should().BeTrue();
            current.Source.Should().Be(DocumentTreeRevisionSource.OcrAdopted);
            adopted.Value.AdoptedTreeRevisionIds.Should().Contain(current.TreeRevisionId);
        }
    }

    [Fact]
    public async Task Partially_failed_ocr_can_adopt_only_successful_page_candidates()
    {
        await using Context context = await Context.CreateAsync();
        OcrPreset preset = (await context.Presets.CreatePresetAsync(
            "Partially failing mock", null, OcrEngineIds.Mock, OcrModelIds.MockBasic, null,
            "{\"failPages\":[1]}", false)).Value;

        OcrRun run = (await context.Coordinator.RunPresetOnDocumentAsync(
            context.Document.DocumentInstanceId, preset.PresetId)).Value;
        IReadOnlyList<OcrPageResult> results =
            (await context.Coordinator.ListPageResultsAsync(run.OcrRunId)).Value;
        OcrPageResult succeeded = results.Single(result => result.State == OcrPageResultState.Succeeded);
        OcrPageResult failed = results.Single(result => result.State == OcrPageResultState.Failed);

        run.State.Should().Be(OcrRunState.CompletedWithErrors);
        (await context.Coordinator.AdoptCandidateRunAsync(run.OcrRunId, [failed.PageId])).IsFailure.Should().BeTrue();
        Result<OcrCandidateAdoption> adopted =
            await context.Coordinator.AdoptCandidateRunAsync(run.OcrRunId, [succeeded.PageId]);

        adopted.IsSuccess.Should().BeTrue(adopted.ErrorMessage);
        (await context.Trees.GetCurrentRevisionAsync(context.Document.DocumentInstanceId, succeeded.PageId)).IsSuccess
            .Should().BeTrue();
        (await context.Trees.GetCurrentRevisionAsync(context.Document.DocumentInstanceId, failed.PageId)).IsFailure
            .Should().BeTrue();
    }

    [Fact]
    public async Task Cancelling_a_pending_run_preserves_the_current_box_trees()
    {
        await using Context context = await Context.CreateAsync();
        OcrPreset preset = (await context.Presets.CreatePresetAsync(
            "Pending mock", null, OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", false)).Value;
        OcrRun pending = (await context.Coordinator.CreatePendingRunForTestAsync(
            context.Document.DocumentInstanceId, preset.PresetId)).Value;

        (await context.Coordinator.CancelRunAsync(pending.OcrRunId)).IsSuccess.Should().BeTrue();
        OcrRun cancelled = (await context.Coordinator.GetRunAsync(pending.OcrRunId)).Value;

        cancelled.State.Should().Be(OcrRunState.Cancelled);
        foreach (Page page in context.Pages)
        {
            (await context.Trees.GetCurrentRevisionAsync(context.Document.DocumentInstanceId, page.PageId)).IsFailure
                .Should().BeTrue();
        }
    }

    private sealed class Context : IAsyncDisposable
    {
        private readonly TemporarySqliteDatabase _database;

        private Context(
            TemporarySqliteDatabase database,
            DocumentInstance document,
            IReadOnlyList<Page> pages,
            IDocumentTreeService trees,
            IOcrPresetService presets,
            OcrRunCoordinator coordinator)
        {
            _database = database;
            Document = document;
            Pages = pages;
            Trees = trees;
            Presets = presets;
            Coordinator = coordinator;
        }

        public DocumentInstance Document { get; }
        public IReadOnlyList<Page> Pages { get; }
        public IDocumentTreeService Trees { get; }
        public IOcrPresetService Presets { get; }
        public OcrRunCoordinator Coordinator { get; }

        public static async Task<Context> CreateAsync()
        {
            TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
            FixedClock clock = new(DateTimeOffset.Parse("2026-07-13T00:00:00Z"));
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            LibraryIdentityService libraries = new(database.ConnectionFactory, clock);
            await libraries.CreateLibraryAsync("OCR lifecycle");
            ItemMetadata item = (await new ItemService(database.ConnectionFactory, libraries, clock)
                .CreateItemAsync("document", "OCR lifecycle")).Value;
            DocumentInstance document = (await new DocumentInstanceService(database.ConnectionFactory, clock)
                .AttachDocumentInstanceAsync(item.ItemId, null, DocumentInstanceType.PrimaryScan)).Value;
            Infrastructure.Layout.PageService pages = new(database.ConnectionFactory, clock);
            Page first = (await pages.CreatePageAsync(document.DocumentInstanceId, 0, "1", null, null, 0,
                CoordinateBasis.NormalizedPage, null, null, "test", null)).Value;
            Page second = (await pages.CreatePageAsync(document.DocumentInstanceId, 1, "2", null, null, 0,
                CoordinateBasis.NormalizedPage, null, null, "test", null)).Value;
            IDocumentTreeService trees = BoxTreeTestData.CreateService(database.ConnectionFactory, clock);
            SearchUnitBuilder search = new(database.ConnectionFactory, clock, new MarkdigMarkdownEngine());
            OcrRunCoordinator coordinator = new(
                database.ConnectionFactory,
                clock,
                new MockOcrEngine(),
                search,
                new OcrDocumentTreeImporter(trees));
            return new Context(
                database,
                document,
                [first, second],
                trees,
                new OcrPresetService(database.ConnectionFactory, libraries, clock),
                coordinator);
        }

        public ValueTask DisposeAsync()
        {
            return _database.DisposeAsync();
        }
    }
}
