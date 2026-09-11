using FluentAssertions;
using Patchouli.Infrastructure.Ocr.NdlLite;

namespace Patchouli.Tests;

public sealed class NdlLiteLayoutOrdererTests
{
    [Fact]
    public void Order_sorts_lines_inside_a_text_block_from_top_to_bottom()
    {
        NdlLiteDetection[] detections =
        [
            TextBlock(0, 0, 200, 200),
            Line(10, 40, 190, 60),
            Line(10, 10, 190, 30)
        ];

        IReadOnlyList<NdlLiteDetection> ordered = NdlLiteLayoutOrderer.Order(detections);

        ordered.Should().HaveCount(2);
        ordered[0].Box.Y0.Should().Be(10);
        ordered[1].Box.Y0.Should().Be(40);
    }

    [Fact]
    public void Order_sorts_vertical_lines_inside_a_text_block_from_right_to_left()
    {
        NdlLiteDetection[] detections =
        [
            TextBlock(0, 0, 200, 300),
            Line(60, 10, 80, 110),
            Line(120, 10, 140, 110)
        ];

        IReadOnlyList<NdlLiteDetection> ordered = NdlLiteLayoutOrderer.Order(detections);

        ordered.Should().HaveCount(2);
        ordered[0].Box.X0.Should().Be(120);
        ordered[1].Box.X0.Should().Be(60);
    }

    [Fact]
    public void Order_drops_lines_below_the_official_confidence_threshold_and_keeps_the_boundary()
    {
        NdlLiteDetection[] detections =
        [
            Line(0, 0, 100, 20, 0.05f),
            Line(0, 30, 100, 50, NdlLiteLayoutOrderer.LineConfidenceThreshold)
        ];

        IReadOnlyList<NdlLiteDetection> ordered = NdlLiteLayoutOrderer.Order(detections);

        ordered.Should().ContainSingle().Which.Confidence.Should().Be(0.1f);
    }

    [Fact]
    public void Order_folds_a_nested_text_block_into_its_parent()
    {
        // The child block is listed first so its line is adopted by the child,
        // then the child is folded into the containing parent.
        NdlLiteDetection[] detections =
        [
            TextBlock(50, 50, 200, 200),
            TextBlock(0, 0, 300, 300),
            Line(60, 60, 190, 80)
        ];

        IReadOnlyList<NdlLiteDetection> ordered = NdlLiteLayoutOrderer.Order(detections);

        ordered.Should().ContainSingle();
        ordered[0].ClassName.Should().Be("line_main");
        ordered[0].Box.Should().Be(new NdlLiteBox(60, 60, 190, 80));
    }

    [Fact]
    public void Order_synthesizes_a_line_main_for_an_empty_text_block()
    {
        NdlLiteDetection[] detections = [TextBlock(10, 10, 110, 60)];

        IReadOnlyList<NdlLiteDetection> ordered = NdlLiteLayoutOrderer.Order(detections);

        ordered.Should().ContainSingle();
        ordered[0].ClassName.Should().Be("line_main");
        ordered[0].ClassIndex.Should().Be(NdlLiteClassNames.LineMainIndex);
        ordered[0].Box.Should().Be(new NdlLiteBox(10, 10, 110, 60));
    }

    [Fact]
    public void Order_keeps_the_higher_confidence_line_when_two_lines_overlap()
    {
        NdlLiteDetection[] detections =
        [
            TextBlock(0, 0, 300, 300),
            Line(10, 10, 110, 50, 0.7f),
            Line(12, 12, 108, 48, 0.9f)
        ];

        IReadOnlyList<NdlLiteDetection> ordered = NdlLiteLayoutOrderer.Order(detections);

        ordered.Should().ContainSingle().Which.Confidence.Should().Be(0.9f);
    }

    [Fact]
    public void OverlapsEnough_uses_the_smaller_box_overlap_not_classic_iou()
    {
        // Classic IoU of a 50x50 box in a 100x40 box is 2000 / 4500 = 0.44, but the
        // official check_iou measures 2000 / 2500 = 0.8 of the smaller box.
        NdlLiteDetection small = Line(0, 0, 50, 50);
        NdlLiteDetection boundary = Line(0, 0, 100, 40);
        NdlLiteDetection inside = Line(0, 0, 100, 45);

        NdlLiteLayoutOrderer.OverlapsEnough(boundary, small).Should().BeFalse("the ratio is exactly 0.8");
        NdlLiteLayoutOrderer.OverlapsEnough(inside, small).Should().BeTrue("the ratio is 0.9");
    }

    [Fact]
    public void Order_returns_an_empty_sequence_for_no_detections()
    {
        NdlLiteLayoutOrderer.Order(Array.Empty<NdlLiteDetection>()).Should().BeEmpty();
    }

    [Fact]
    public void Order_keeps_independent_lines_and_text_block_lines()
    {
        NdlLiteDetection[] detections =
        [
            TextBlock(0, 0, 200, 120),
            Line(10, 10, 190, 30),
            Line(300, 10, 480, 30)
        ];

        IReadOnlyList<NdlLiteDetection> ordered = NdlLiteLayoutOrderer.Order(detections);

        ordered.Should().HaveCount(2);
        ordered.Select(static detection => detection.Box).Should().Contain(new NdlLiteBox(10, 10, 190, 30));
        ordered.Select(static detection => detection.Box).Should().Contain(new NdlLiteBox(300, 10, 480, 30));
    }

    private static NdlLiteDetection Line(int x0, int y0, int x1, int y1, float confidence = 0.9f)
    {
        return new NdlLiteDetection(NdlLiteClassNames.LineMainIndex, "line_main", confidence,
            new NdlLiteBox(x0, y0, x1, y1), 100.0f);
    }

    private static NdlLiteDetection TextBlock(int x0, int y0, int x1, int y1, float confidence = 0.9f)
    {
        return new NdlLiteDetection(NdlLiteClassNames.TextBlockIndex, "text_block", confidence,
            new NdlLiteBox(x0, y0, x1, y1), 100.0f);
    }
}
