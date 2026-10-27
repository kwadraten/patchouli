using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Ocr;

namespace Patchouli.Tests;

public sealed class OcrNestedCandidateTests
{
    [Fact]
    public async Task Extension_declared_logical_container_and_typed_child_reach_shared_importer_without_flattening()
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        FixedClock clock = new(DateTimeOffset.Parse("2026-10-25T00:00:00Z"));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        LibraryIdentityService libraries = new(database.ConnectionFactory, clock);
        await libraries.CreateLibraryAsync("Nested OCR candidate");
        ItemMetadata item = (await new ItemService(database.ConnectionFactory, libraries, clock)
            .CreateItemAsync("book", "Hierarchy")).Value;
        DocumentInstance document = (await new DocumentInstanceService(
                database.ConnectionFactory, clock)
            .AttachDocumentInstanceAsync(item.ItemId, null, DocumentInstanceType.PrimaryScan)).Value;
        Page page = (await new PageService(database.ConnectionFactory, clock).CreatePageAsync(
            document.DocumentInstanceId, 0, "1", 100, 100, 0,
            CoordinateBasis.NormalizedPage, 100, 100, "generated", null)).Value;
        DocumentTreeService trees = BoxTreeTestData.CreateService(database.ConnectionFactory, clock);
        DocumentTreeRevision baseline = await BoxTreeTestData.CommitTextAsync(database.ConnectionFactory, clock,
            document.DocumentInstanceId, page.PageId, "before");
        DocumentBoxId containerId = DocumentBoxId.New();
        DocumentBoxId tableId = DocumentBoxId.New();
        NormalizedBBox full = new(0, 0, 1, 1);
        NormalizedBBox tableBox = new(.1, .1, .8, .2);
        OcrJobInput input = new(new OcrInputId("full-page"), full, "image/png");
        OcrJobPage issued = new(new ExtensionPageIdentity(document.DocumentInstanceId, page.PageId,
            baseline.TreeRevisionId, 0), [input]);
        TableBoxPayload table = new("| A | B |\n| --- | --- |\n| 1 | 2 |");
        OcrExtensionCandidate candidate = new(issued.Page,
        [
            new OcrExtensionBoxCandidate(input.Id, new DocumentBoxSeed(containerId, null, 0,
                DocumentBoxType.LogicalPage, null, full, null)),
            new OcrExtensionBoxCandidate(input.Id, new DocumentBoxSeed(tableId, containerId, 0,
                DocumentBoxType.Table, null, tableBox, table))
        ], [new OcrInputSettlement(input.Id, OcrInputSettlementState.Recognized)]);
        ExtensionCandidateValidation.ValidateOcr(issued, candidate).IsSuccess.Should().BeTrue();
        OcrDocumentTreeCandidate imported = new([
            new OcrPageCandidate(page.PageId, 0,
            [
                new OcrBoxCandidate(DocumentBoxType.LogicalPage, null, 0, null, full, null, null, false,
                    PreassignedBoxId: containerId),
                new OcrBoxCandidate(DocumentBoxType.Table, null, 0, table, tableBox, null, null, false,
                    containerId, tableId)
            ])
        ], []);
        Result<OcrDocumentTreeImportResult> working = await new OcrDocumentTreeImporter(trees).BeginWorkingAsync(
            new OcrDocumentTreeImportRequest(document.DocumentInstanceId, imported,
                ExpectedParentTreeRevisionIds: new Dictionary<PageId, DocumentTreeRevisionId>
                    { [page.PageId] = baseline.TreeRevisionId }));
        working.IsSuccess.Should().BeTrue(working.ErrorMessage);
        IReadOnlyList<DocumentBox>
            boxes = (await trees.ListBoxesAsync(working.Value.WorkingRevisionIds.Single())).Value;
        boxes.Should().HaveCount(2);
        boxes.Single(box => box.BoxId == tableId).ParentBoxId.Should().Be(containerId);
        boxes.Single(box => box.BoxId == tableId).Payload.Should().Be(table);
        (await trees.GetCurrentRevisionAsync(document.DocumentInstanceId, page.PageId)).Value.TreeRevisionId
            .Should().Be(baseline.TreeRevisionId, "candidate staging never adopts by itself");
    }
}
