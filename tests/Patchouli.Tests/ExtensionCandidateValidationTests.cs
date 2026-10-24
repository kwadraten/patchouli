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
        OcrExtensionContext context = new(page,
            [new ExtensionOcrInput(inputId, inputBox, "image/png", new byte[] { 1, 2, 3 })],
            new ExtensionConfigSnapshot(new Dictionary<string, string>(), 0));
        OcrExtensionBoxCandidate validBox = new(inputId, new DocumentBoxSeed(null, null, 0, "text", null,
            new NormalizedBBox(0.25, 0.25, 0.2, 0.2), new TextBoxPayload("recognized"), Confidence: 0.9));

        Result wrongPage = ExtensionCandidateValidation.ValidateOcr(context,
            new OcrExtensionCandidate(page with { PageIndex = page.PageIndex + 1 }, [validBox]));
        Result outsideInput = ExtensionCandidateValidation.ValidateOcr(context,
            new OcrExtensionCandidate(page,
            [
                validBox with
                {
                    Candidate = validBox.Candidate with { BBox = new NormalizedBBox(0.5, 0.5, 0.2, 0.2) }
                }
            ]));

        wrongPage.IsFailure.Should().BeTrue();
        outsideInput.IsFailure.Should().BeTrue();
        ExtensionCandidateValidation.ValidateOcr(context, new OcrExtensionCandidate(page, [validBox]))
            .IsSuccess.Should().BeTrue();
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
