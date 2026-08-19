using Dapper;
using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Core.Search;

namespace Patchouli.Tests.S3Ocr;

public sealed class SearchUnitBatchTests
{
    [Fact]
    public async Task Bulk_search_unit_rebuild_is_batched_and_idempotent()
    {
        await using OcrPerfContext context = await OcrPerfContext.CreateAsync();
        const int unitCount = 1100;
        DocumentTreeRevision working = (await context.Trees.BeginWorkingRevisionAsync(
            context.Document.DocumentInstanceId,
            context.Pages[0].PageId,
            Boxes.LeafText(unitCount),
            DocumentTreeRevisionSource.Import)).Value;
        DocumentTreeRevision committed = (await context.Trees.CommitWorkingRevisionAsync(working.TreeRevisionId)).Value;
        committed.Status.Should().Be(DocumentTreeRevisionStatus.Committed);
        committed.TreeRevisionId.Should().Be(working.TreeRevisionId);

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
    public async Task Bulk_unit_rebuild_creates_search_units_without_evidence_side_effects()
    {
        await using OcrPerfContext context = await OcrPerfContext.CreateAsync();
        const int unitCount = 300;
        DocumentTreeRevision working = (await context.Trees.BeginWorkingRevisionAsync(
            context.Document.DocumentInstanceId,
            context.Pages[0].PageId,
            Boxes.LeafText(unitCount),
            DocumentTreeRevisionSource.Import)).Value;
        await context.Trees.CommitWorkingRevisionAsync(working.TreeRevisionId);
        await context.Units.RebuildForDocumentInstanceAsync(context.Document.DocumentInstanceId);

        (await context.CountAsync("select count(1) from search_units where status = 'current';")).Should().Be(unitCount);

        // EvidenceRef tables were dropped; no side effects should ever be written.
        (await context.CountAsync(
            "select count(1) from sqlite_master where type = 'table' and name in ('evidence_ref_records', 'evidence_successors');"))
            .Should().Be(0);

        // Re-create a working revision with fresh box ids, commit, and rebuild: search units are
        // refreshed for the new current revision without writing any evidence records.
        DocumentTreeRevision second = (await context.Trees.BeginWorkingRevisionAsync(
            context.Document.DocumentInstanceId,
            context.Pages[0].PageId,
            Boxes.LeafText(unitCount),
            DocumentTreeRevisionSource.Import)).Value;
        Result<DocumentTreeRevision> rebuilt = await context.Trees.CommitWorkingRevisionAsync(second.TreeRevisionId);
        rebuilt.IsSuccess.Should().BeTrue(rebuilt.ErrorMessage);
        Result units = await context.Units.RebuildForDocumentInstanceAsync(context.Document.DocumentInstanceId);
        units.IsSuccess.Should().BeTrue(units.ErrorMessage);

        (await context.CountAsync("select count(1) from search_units where status = 'current';")).Should().Be(unitCount);
        (await context.CountAsync("select count(1) from search_units where status = 'deleted';")).Should().Be(unitCount);
    }
}
