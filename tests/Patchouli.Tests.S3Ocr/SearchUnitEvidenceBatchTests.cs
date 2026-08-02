using Dapper;
using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Core.Evidence;
using Patchouli.Core.Search;

namespace Patchouli.Tests.S3Ocr;

public sealed class SearchUnitEvidenceBatchTests
{
    [Fact]
    public async Task Bulk_search_unit_rebuild_is_batched_and_idempotent()
    {
        await using OcrPerfContext context = await OcrPerfContext.CreateAsync();
        const int unitCount = 1100;
        DocumentTreeRevision staging = (await context.Trees.StagePageAsync(
            context.Document.DocumentInstanceId,
            context.Pages[0].PageId,
            Boxes.LeafText(unitCount))).Value;
        await context.Trees.AdoptStagingRevisionAsync(staging.TreeRevisionId);

        Result units = await context.Units.RebuildForDocumentInstanceAsync(context.Document.DocumentInstanceId);
        units.IsSuccess.Should().BeTrue(units.ErrorMessage);
        (await context.CountAsync(
            "select count(1) from search_units where status = 'current';")).Should().Be(unitCount);

        // Rebuilding the same committed revision must be idempotent.
        Result again = await context.Units.RebuildForDocumentInstanceAsync(context.Document.DocumentInstanceId);
        again.IsSuccess.Should().BeTrue(again.ErrorMessage);
        (await context.CountAsync(
            "select count(1) from search_units where status = 'current';")).Should().Be(unitCount);
        (await context.CountAsync("select count(1) from search_units;")).Should().Be(unitCount);

        Result fts = await context.Index.RebuildFtsForDocumentInstanceAsync(context.Document.DocumentInstanceId);
        fts.IsSuccess.Should().BeTrue(fts.ErrorMessage);
        SearchResultPage hit = (await context.Search.SearchLibraryAsync(new SearchRequest("line500"))).Value;
        hit.Results.Should().ContainSingle().Which.MatchedUnits.Single().Text.Should().Be("line500 unique");
    }

    [Fact]
    public async Task Bulk_unit_rebuild_links_evidence_successors_in_batch()
    {
        await using OcrPerfContext context = await OcrPerfContext.CreateAsync();
        const int unitCount = 300;
        DocumentTreeRevision first = (await context.Trees.StagePageAsync(
            context.Document.DocumentInstanceId,
            context.Pages[0].PageId,
            Boxes.LeafText(unitCount))).Value;
        await context.Trees.AdoptStagingRevisionAsync(first.TreeRevisionId);
        await context.Units.RebuildForDocumentInstanceAsync(context.Document.DocumentInstanceId);

        SearchUnitId[] unitIds = (await QueryUnitIdsAsync(context, unitCount)).ToArray();
        Result<IReadOnlyList<EvidenceReferenceCreateResult>> created =
            await context.Evidence.CreateFromSearchUnitsAsync(unitIds);
        created.IsSuccess.Should().BeTrue(created.ErrorMessage);
        created.Value.Should().OnlyContain(result => result.Result.IsSuccess);
        (await context.CountAsync("select count(1) from evidence_ref_records;")).Should().Be(unitCount);

        // Re-stage the same page with fresh box ids, adopt, and rebuild: predecessors match by
        // text and every active evidence record must be linked to a successor in one batch.
        DocumentTreeRevision second = (await context.Trees.StagePageAsync(
            context.Document.DocumentInstanceId,
            context.Pages[0].PageId,
            Boxes.LeafText(unitCount))).Value;
        await context.Trees.AdoptStagingRevisionAsync(second.TreeRevisionId);
        Result rebuilt = await context.Units.RebuildForDocumentInstanceAsync(context.Document.DocumentInstanceId);
        rebuilt.IsSuccess.Should().BeTrue(rebuilt.ErrorMessage);

        (await context.CountAsync("select count(1) from evidence_ref_records;")).Should().Be(unitCount * 2);
        (await context.CountAsync(
            "select count(1) from evidence_ref_records where status = 'superseded';")).Should().Be(unitCount);
        (await context.CountAsync(
            "select count(1) from evidence_successors where reason = 'layout_replaced';")).Should().Be(unitCount);
        (await context.CountAsync(
            "select count(1) from evidence_ref_records where status = 'active';")).Should().Be(unitCount);
    }

    private static async Task<IReadOnlyList<SearchUnitId>> QueryUnitIdsAsync(OcrPerfContext context, int count)
    {
        await using Microsoft.Data.Sqlite.SqliteConnection connection = context.OpenConnection();
        await connection.OpenAsync();
        string[] ids = (await connection.QueryAsync<string>(
            "select unit_id from search_units where status = 'current' order by ordinal;")).ToArray();
        ids.Should().HaveCount(count);
        return ids.Select(SearchUnitId.Parse).ToArray();
    }
}
