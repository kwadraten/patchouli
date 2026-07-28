using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;

namespace Patchouli.Tests;

public sealed class DocumentBoxOverlapDetectorTests
{
    [Fact]
    public void Detects_significant_partial_overlap_between_siblings()
    {
        DocumentBox first = Box(new NormalizedBBox(0.1, 0.1, 0.4, 0.4));
        DocumentBox second = Box(new NormalizedBBox(0.3, 0.3, 0.4, 0.4));

        IReadOnlyList<DocumentBoxOverlap> overlaps = DocumentBoxOverlapDetector.Detect([first, second]);

        DocumentBoxOverlap overlap = overlaps.Should().ContainSingle().Subject;
        overlap.Intersection.X.Should().BeApproximately(0.3, 1e-9);
        overlap.Intersection.Y.Should().BeApproximately(0.3, 1e-9);
        overlap.Intersection.Width.Should().BeApproximately(0.2, 1e-9);
        overlap.Intersection.Height.Should().BeApproximately(0.2, 1e-9);
    }

    [Fact]
    public void Ignores_overlap_below_the_significance_threshold()
    {
        DocumentBox first = Box(new NormalizedBBox(0.1, 0.1, 0.4, 0.4));
        DocumentBox second = Box(new NormalizedBBox(0.45, 0.45, 0.4, 0.4));

        DocumentBoxOverlapDetector.Detect([first, second]).Should().BeEmpty();
    }

    [Fact]
    public void Reports_full_containment_between_siblings_for_explicit_handling()
    {
        DocumentBox container = Box(new NormalizedBBox(0.1, 0.1, 0.6, 0.6));
        DocumentBox contained = Box(new NormalizedBBox(0.2, 0.2, 0.2, 0.2));

        IReadOnlyList<DocumentBoxOverlap> overlaps = DocumentBoxOverlapDetector.Detect([container, contained]);

        overlaps.Should().ContainSingle();
    }

    [Fact]
    public void Ignores_suppressed_logical_page_and_overlap_compatible_boxes()
    {
        DocumentBox ordinary = Box(new NormalizedBBox(0.1, 0.1, 0.4, 0.4));
        DocumentBox suppressed = Box(new NormalizedBBox(0.2, 0.2, 0.4, 0.4), suppressed: true);
        DocumentBox logicalPage = Box(new NormalizedBBox(0.2, 0.2, 0.4, 0.4), DocumentBoxType.LogicalPage);
        DocumentBox annotation = Box(new NormalizedBBox(0.2, 0.2, 0.4, 0.4), "annotation");

        DocumentBoxOverlapDetector.Detect([ordinary, suppressed, logicalPage, annotation]).Should().BeEmpty();
    }

    [Fact]
    public void Ignores_boxes_in_different_parent_groups()
    {
        DocumentBoxId parentA = DocumentBoxId.New();
        DocumentBoxId parentB = DocumentBoxId.New();
        DocumentBox first = Box(new NormalizedBBox(0.1, 0.1, 0.4, 0.4), parentBoxId: parentA);
        DocumentBox second = Box(new NormalizedBBox(0.2, 0.2, 0.4, 0.4), parentBoxId: parentB);

        DocumentBoxOverlapDetector.Detect([first, second]).Should().BeEmpty();
    }

    [Fact]
    public void Reports_every_overlapping_pair_within_one_sibling_group()
    {
        DocumentBox first = Box(new NormalizedBBox(0.1, 0.1, 0.4, 0.4));
        DocumentBox second = Box(new NormalizedBBox(0.2, 0.2, 0.4, 0.4));
        DocumentBox third = Box(new NormalizedBBox(0.3, 0.3, 0.4, 0.4));

        IReadOnlyList<DocumentBoxOverlap> overlaps = DocumentBoxOverlapDetector.Detect([first, second, third]);

        overlaps.Should().HaveCount(3);
    }

    private static DocumentBox Box(
        NormalizedBBox bbox,
        string boxType = DocumentBoxType.Text,
        DocumentBoxId? parentBoxId = null,
        bool suppressed = false)
    {
        DocumentTreeRevisionId revisionId = DocumentTreeRevisionId.New();
        return new DocumentBox(
            revisionId,
            DocumentBoxId.New(),
            DocumentInstanceId.New(),
            PageId.New(),
            parentBoxId,
            null,
            boxType,
            null,
            null,
            bbox,
            boxType == DocumentBoxType.LogicalPage ? null : new TextBoxPayload("text"),
            null,
            null,
            null,
            suppressed);
    }
}
