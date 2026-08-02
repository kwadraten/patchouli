using Dapper;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Core.Evidence;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Evidence;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Search;
using Patchouli.Core.Search;
using Microsoft.Data.Sqlite;

namespace Patchouli.Performance;

public sealed record PerformanceFixtureState(
    string DatabasePath,
    LibraryId LibraryId,
    IReadOnlyList<ItemId> ItemIds,
    IReadOnlyList<DocumentInstanceId> DocumentIds,
    IReadOnlyList<PageId> PageIds,
    string? EvidenceRefId,
    long TotalItems,
    long TotalPages,
    long TotalBoxes,
    long TotalSearchUnits);

/// <summary>
/// Seeds a deterministic, privacy-safe synthetic Library: <c>items</c> items, each with
/// <c>pagesPerItem</c> pages and <c>boxesPerPage</c> text boxes, plus search units and an FTS
/// index. All content is synthetic and stable for a given seed; no real documents, paths,
/// EvidenceRefs, or secrets are involved.
/// </summary>
public static class PerformanceFixture
{
    public static async Task<PerformanceFixtureState> SeedAsync(
        CountingConnectionFactory connectionFactory,
        string migrationsDirectory,
        long seed,
        int items,
        int pagesPerItem,
        int boxesPerPage,
        CancellationToken cancellationToken)
    {
        IClock clock = new FixedClock(new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero)
            .AddTicks(seed % TimeSpan.TicksPerDay));

        await using (SqliteConnection admin = connectionFactory.CreateConnection())
        {
            await admin.OpenAsync(cancellationToken);
            await admin.ExecuteScalarAsync<string>("PRAGMA journal_mode=WAL;");
        }

        await new MigrationRunner(connectionFactory, migrationsDirectory).RunAsync();

        LibraryIdentityService libraryService = new(connectionFactory, clock);
        Result<LibraryMetadata> createdLibrary = await libraryService.CreateLibraryAsync("patchouli perf fixture");
        ThrowIfFailed(createdLibrary, "create library");

        ItemService itemsService = new(connectionFactory, libraryService, clock);
        DocumentInstanceService documentsService = new(connectionFactory, clock);
        PageService pagesService = new(connectionFactory, clock);
        DocumentTreeService treeService = new(connectionFactory, clock, new MarkdigMarkdownEngine());
        SearchUnitBuilder searchUnits = new(connectionFactory, clock);

        List<ItemId> itemIds = new(items);
        List<DocumentInstanceId> documentIds = new(items);
        List<PageId> pageIds = new(items * pagesPerItem);

        for (int itemIndex = 0; itemIndex < items; itemIndex++)
        {
            Result<ItemMetadata> item = await itemsService.CreateItemAsync(
                "book", $"perf-item-{itemIndex:0000}", cancellationToken: cancellationToken);
            ThrowIfFailed(item, $"create item {itemIndex}");
            itemIds.Add(item.Value.ItemId);

            Result<DocumentInstance> document = await documentsService.AttachDocumentInstanceAsync(
                item.Value.ItemId, null, DocumentInstanceType.PrimaryScan,
                $"perf-document-{itemIndex:0000}", true, cancellationToken);
            ThrowIfFailed(document, $"attach document {itemIndex}");
            documentIds.Add(document.Value.DocumentInstanceId);

            for (int pageIndex = 0; pageIndex < pagesPerItem; pageIndex++)
            {
                Result<Page> page = await pagesService.CreatePageAsync(
                    document.Value.DocumentInstanceId, pageIndex, (pageIndex + 1).ToString(), null, null, 0,
                    CoordinateBasis.NormalizedPage, null, null, "patchouli-perf", null, cancellationToken);
                ThrowIfFailed(page, $"create page {itemIndex}/{pageIndex}");
                pageIds.Add(page.Value.PageId);

                await StageAndAdoptAsync(treeService, document.Value.DocumentInstanceId, page.Value.PageId,
                    boxesPerPage, itemIndex, pageIndex, cancellationToken);
            }

            Result units = await searchUnits.RebuildForDocumentInstanceAsync(
                document.Value.DocumentInstanceId, cancellationToken);
            ThrowIfFailed(units, $"rebuild search units {itemIndex}");
        }

        Result index = await new SearchIndexRebuilder(connectionFactory, clock)
            .RebuildFtsForLibraryAsync(cancellationToken);
        ThrowIfFailed(index, "rebuild FTS");

        string? evidenceRefId = null;
        await using (SqliteConnection connection = connectionFactory.CreateConnection())
        {
            await connection.OpenAsync(cancellationToken);
            string? firstUnit = await connection.ExecuteScalarAsync<string>(
                "select unit_id from search_units where status = 'current' order by ordinal, unit_id limit 1;");
            if (!string.IsNullOrWhiteSpace(firstUnit))
            {
                EvidenceReferenceService evidenceService = new(connectionFactory, clock);
                Result<EvidenceRefRecord> evidenceRecord = await evidenceService.CreateFromSearchUnitAsync(
                    SearchUnitId.Parse(firstUnit), cancellationToken);
                if (evidenceRecord.IsSuccess)
                {
                    evidenceRefId = evidenceRecord.Value.EvidenceRefId;
                }
            }
        }

        long totalPages = (long)items * pagesPerItem;
        long totalBoxes = totalPages * boxesPerPage;
        return new PerformanceFixtureState(
            connectionFactory.DatabasePath, createdLibrary.Value.LibraryId, itemIds, documentIds, pageIds,
            evidenceRefId, items, totalPages, totalBoxes, totalBoxes);
    }

    private static async Task StageAndAdoptAsync(
        DocumentTreeService treeService,
        DocumentInstanceId documentId,
        PageId pageId,
        int boxesPerPage,
        int itemIndex,
        int pageIndex,
        CancellationToken cancellationToken)
    {
        DocumentBoxSeed[] seeds = new DocumentBoxSeed[boxesPerPage];
        for (int boxIndex = 0; boxIndex < boxesPerPage; boxIndex++)
        {
            double top = 0.04 + 0.9 * boxIndex / Math.Max(1, boxesPerPage);
            seeds[boxIndex] = new DocumentBoxSeed(
                null, null, boxIndex, DocumentBoxType.Text, null, null,
                new NormalizedBBox(0.04, top, 0.92, Math.Max(0.004, 0.88 / boxesPerPage)),
                new TextBoxPayload($"perf text item {itemIndex:0000} page {pageIndex:00} box {boxIndex:00}"),
                null);
        }

        Result<DocumentTreeRevision> staging = await treeService.StagePageAsync(
            documentId, pageId, seeds, DocumentTreeRevisionSource.Import, cancellationToken: cancellationToken);
        ThrowIfFailed(staging, "stage revision");
        Result<DocumentTreeRevision> committed = await treeService.AdoptStagingRevisionAsync(
            staging.Value.TreeRevisionId, cancellationToken);
        ThrowIfFailed(committed, "adopt revision");
    }

    private static void ThrowIfFailed<T>(Result<T> result, string operation)
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException(
                $"Fixture seeding failed during {operation}: {result.ErrorCode} {result.ErrorMessage}");
        }
    }

    private static void ThrowIfFailed(Result result, string operation)
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException(
                $"Fixture seeding failed during {operation}: {result.ErrorCode} {result.ErrorMessage}");
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
