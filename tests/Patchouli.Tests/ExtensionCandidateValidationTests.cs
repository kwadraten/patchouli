using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Core.Search;
using Patchouli.Extensions.Contracts;

namespace Patchouli.Tests;

public sealed class ExtensionCandidateValidationTests
{
    [Fact]
    public void Ocr_candidate_must_match_the_page_and_stay_inside_the_input_bbox()
    {
        ExtensionPageIdentity page = Page();
        OcrInputId inputId = new("image-1");
        NormalizedBBox inputBox = new(0.2, 0.2, 0.4, 0.4);
        OcrJobPage issued = new(page, [new OcrJobInput(inputId, inputBox, "image/png")]);
        OcrExtensionBoxCandidate validBox = new(inputId, new DocumentBoxSeed(null, null, 0, "text", null,
            new NormalizedBBox(0.25, 0.25, 0.2, 0.2), new TextBoxPayload("recognized"), Confidence: 0.9));

        Result wrongPage = ExtensionCandidateValidation.ValidateOcr(issued,
            new OcrExtensionCandidate(page with { PageIndex = page.PageIndex + 1 }, [validBox],
                Recognized(issued)));
        Result wrongRevision = ExtensionCandidateValidation.ValidateOcr(issued,
            new OcrExtensionCandidate(page with { RevisionId = DocumentTreeRevisionId.New() }, [validBox],
                Recognized(issued)));
        Result outsideInput = ExtensionCandidateValidation.ValidateOcr(issued,
            new OcrExtensionCandidate(page,
            [
                validBox with
                {
                    Candidate = validBox.Candidate with { BBox = new NormalizedBBox(0.5, 0.5, 0.2, 0.2) }
                }
            ], Recognized(issued)));

        wrongPage.IsFailure.Should().BeTrue();
        wrongRevision.IsFailure.Should().BeTrue("a stale source revision is not the issued baseline");
        outsideInput.IsFailure.Should().BeTrue();
        ExtensionCandidateValidation.ValidateOcr(issued,
                new OcrExtensionCandidate(page, [validBox], Recognized(issued)))
            .IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Ocr_candidate_rejects_foreign_inputs_and_dangling_box_references()
    {
        ExtensionPageIdentity page = Page();
        OcrInputId inputId = new("image-1");
        OcrJobPage issued = new(page,
            [new OcrJobInput(inputId, new NormalizedBBox(0, 0, 1, 1), "image/png")]);
        DocumentBoxId head = DocumentBoxId.New();
        DocumentBoxId foreign = DocumentBoxId.New();
        OcrExtensionBoxCandidate box = new(inputId, new DocumentBoxSeed(head, null, 0, "text", null,
            new NormalizedBBox(0.1, 0.1, 0.2, 0.2), new TextBoxPayload("head")));

        ExtensionCandidateValidation.ValidateOcr(issued,
                new OcrExtensionCandidate(page,
                    [new OcrExtensionBoxCandidate(new OcrInputId("other"), box.Candidate)],
                    Recognized(issued)))
            .IsFailure.Should().BeTrue("a box must reference an input issued for this page");
        ExtensionCandidateValidation.ValidateOcr(issued,
                new OcrExtensionCandidate(page,
                [
                    box,
                    new OcrExtensionBoxCandidate(inputId, new DocumentBoxSeed(null, null, 1, "text", null,
                        new NormalizedBBox(0.1, 0.4, 0.2, 0.2), new TextBoxPayload("tail"),
                        ContinuesFromBoxId: foreign))
                ], Recognized(issued)))
            .IsFailure.Should().BeTrue("a continuation must resolve inside the job");
        ExtensionCandidateValidation.ValidateOcr(issued,
                new OcrExtensionCandidate(page,
                [
                    box,
                    new OcrExtensionBoxCandidate(inputId, new DocumentBoxSeed(null, head, 1, "text", null,
                        new NormalizedBBox(0.1, 0.4, 0.2, 0.2), new TextBoxPayload("tail")))
                ], Recognized(issued)))
            .IsSuccess.Should().BeTrue("a parent inside the same submission resolves");
        ExtensionCandidateValidation.ValidateOcr(issued,
                new OcrExtensionCandidate(page,
                [
                    box,
                    new OcrExtensionBoxCandidate(inputId, new DocumentBoxSeed(null, null, 1, "text", null,
                        new NormalizedBBox(0.1, 0.4, 0.2, 0.2), new TextBoxPayload("tail"),
                        ContinuesFromBoxId: head))
                ], Recognized(issued)))
            .IsSuccess.Should().BeTrue("a continuation inside the same submission resolves");
        ExtensionCandidateValidation.ValidateOcr(issued,
                new OcrExtensionCandidate(page,
                [
                    new OcrExtensionBoxCandidate(inputId, new DocumentBoxSeed(null, null, 0, "text", null,
                        new NormalizedBBox(0.1, 0.4, 0.2, 0.2), new TextBoxPayload("tail"),
                        ContinuesFromBoxId: head))
                ], Recognized(issued)),
                new HashSet<DocumentBoxId> { head })
            .IsSuccess.Should().BeTrue("a continuation may point at a box accepted earlier in the job");
        ExtensionCandidateValidation.ValidateOcr(issued,
                new OcrExtensionCandidate(page, [box], Recognized(issued)),
                new HashSet<DocumentBoxId> { head })
            .IsFailure.Should().BeTrue("redeclaring an id accepted earlier is a foreign claim");
    }

    [Fact]
    public void Ocr_candidate_input_outcomes_must_cover_every_issued_input_exactly_once()
    {
        ExtensionPageIdentity page = Page();
        OcrJobInput first = new(new OcrInputId("image-1"), new NormalizedBBox(0, 0, 1, 1), "image/png");
        OcrJobInput second = new(new OcrInputId("image-2"), new NormalizedBBox(0, 0, 0.5, 0.5), "image/png");
        OcrJobPage issued = new(page, [first, second]);
        OcrExtensionBoxCandidate box = new(first.Id, new DocumentBoxSeed(null, null, 0, "text", null,
            new NormalizedBBox(0.1, 0.1, 0.2, 0.2), new TextBoxPayload("text")));

        // Missing an issued input is not a full success — it is rejected.
        ExtensionCandidateValidation.ValidateOcr(issued,
                new OcrExtensionCandidate(page, [box],
                    [new OcrInputSettlement(first.Id, OcrInputSettlementState.Recognized)]))
            .IsFailure.Should().BeTrue("an unsettled issued input cannot pass as a full success");

        // Foreign and duplicate outcome ids are rejected.
        ExtensionCandidateValidation.ValidateOcr(issued,
            new OcrExtensionCandidate(page, [box],
            [
                new OcrInputSettlement(first.Id, OcrInputSettlementState.Recognized),
                new OcrInputSettlement(new OcrInputId("other"), OcrInputSettlementState.Recognized)
            ])).IsFailure.Should().BeTrue("foreign outcome ids are not issuable");
        ExtensionCandidateValidation.ValidateOcr(issued,
            new OcrExtensionCandidate(page, [box],
            [
                new OcrInputSettlement(first.Id, OcrInputSettlementState.Recognized),
                new OcrInputSettlement(first.Id, OcrInputSettlementState.Failed, "ocr_input_failed")
            ])).IsFailure.Should().BeTrue("duplicate outcome ids are invalid");

        // Failed needs a code; Recognized/Skipped must not carry one.
        ExtensionCandidateValidation.ValidateOcr(issued,
            new OcrExtensionCandidate(page, [box],
            [
                new OcrInputSettlement(first.Id, OcrInputSettlementState.Recognized),
                new OcrInputSettlement(second.Id, OcrInputSettlementState.Failed)
            ])).IsFailure.Should().BeTrue("a failed input must declare an error code");
        ExtensionCandidateValidation.ValidateOcr(issued,
            new OcrExtensionCandidate(page, [box],
            [
                new OcrInputSettlement(first.Id, OcrInputSettlementState.Recognized,
                    "ocr_input_failed", "bug"),
                new OcrInputSettlement(second.Id, OcrInputSettlementState.Recognized)
            ])).IsFailure.Should().BeTrue("a recognized input must not carry an error");

        // Boxes cannot attach to a failed or skipped input.
        ExtensionCandidateValidation.ValidateOcr(issued,
            new OcrExtensionCandidate(page, [box],
            [
                new OcrInputSettlement(first.Id, OcrInputSettlementState.Failed,
                    "ocr_input_failed", "engine died"),
                new OcrInputSettlement(second.Id, OcrInputSettlementState.Recognized)
            ])).IsFailure.Should().BeTrue("boxes attach only to recognized inputs");

        // A blank-successful input plus a failed sibling is a valid partial page.
        ExtensionCandidateValidation.ValidateOcr(issued,
            new OcrExtensionCandidate(page, [box],
            [
                new OcrInputSettlement(first.Id, OcrInputSettlementState.Recognized),
                new OcrInputSettlement(second.Id, OcrInputSettlementState.Failed,
                    "ocr_input_timeout", "TimeoutException")
            ])).IsSuccess.Should().BeTrue("recognized + failed is a valid partial submission");
        ExtensionCandidateValidation.ValidateOcr(issued,
            new OcrExtensionCandidate(page, [],
            [
                new OcrInputSettlement(first.Id, OcrInputSettlementState.Skipped),
                new OcrInputSettlement(second.Id, OcrInputSettlementState.Recognized)
            ])).IsSuccess.Should().BeTrue("an empty page of accounted inputs is valid");
    }

    [Fact]
    public void Retrieval_candidate_rejects_nonfinite_scores()
    {
        SearchMatchedUnit allowed = SearchUnit("allowed");
        RetrievalExtensionContext context = new("query",
            new ExtensionConfigSnapshot(new Dictionary<string, string>(), 0), [allowed], 4);
        foreach (double score in new[] { double.NaN, double.PositiveInfinity })
        {
            ExtensionCandidateValidation.ValidateRetrieval(context,
                    new RetrievalExtensionResult([new RetrievalExtensionMatch(allowed.UnitId, score)])).IsFailure
                .Should()
                .BeTrue();
        }
    }

    [Fact]
    public void Retrieval_candidate_rejects_foreign_and_duplicate_units()
    {
        SearchMatchedUnit allowed = SearchUnit("allowed");
        SearchMatchedUnit foreign = SearchUnit("foreign");
        RetrievalExtensionContext context = new("query",
            new ExtensionConfigSnapshot(new Dictionary<string, string>(), 0), [allowed], 4);
        ExtensionCandidateValidation.ValidateRetrieval(context,
                new RetrievalExtensionResult([new RetrievalExtensionMatch(foreign.UnitId, 0.5)])).IsFailure.Should()
            .BeTrue();
        ExtensionCandidateValidation.ValidateRetrieval(context, new RetrievalExtensionResult([
            new RetrievalExtensionMatch(allowed.UnitId, 0.7), new RetrievalExtensionMatch(allowed.UnitId, 0.6)
        ])).IsFailure.Should().BeTrue();
    }

    private static OcrInputSettlement[] Recognized(OcrJobPage page)
    {
        return page.Inputs
            .Select(input => new OcrInputSettlement(input.Id, OcrInputSettlementState.Recognized))
            .ToArray();
    }

    private static ExtensionPageIdentity Page()
    {
        return new ExtensionPageIdentity(DocumentInstanceId.New(), PageId.New(), DocumentTreeRevisionId.New(), 1);
    }

    private static SearchMatchedUnit SearchUnit(string text)
    {
        return new SearchMatchedUnit(SearchUnitId.New(), PageId.New(),
            DocumentBoxId.New(), text, "text", 0, DocumentTreeRevisionId.New(), true);
    }
}
