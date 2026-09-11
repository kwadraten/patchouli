using FluentAssertions;
using Patchouli.Infrastructure.Ocr.NdlKoten;

namespace Patchouli.Tests;

public sealed class NdlKotenOcrPipelineTests
{
    [Fact]
    public void Recognition_worker_cap_matches_the_official_four_worker_thread_pool()
    {
        // The reference ocr.py recognizes crops via ThreadPoolExecutor(max_workers=4).
        NdlKotenOcrPipeline.RecognitionWorkerCount.Should().Be(4);
    }

    [Fact]
    public void FilterDetections_drops_boxes_narrower_or_shorter_than_five_pixels()
    {
        LineDetection[] detections =
        [
            new(new Box(10, 10, 13, 110), 0.9f, 1), // width 3
            new(new Box(20, 20, 120, 24), 0.8f, 1), // height 4
            new(new Box(200, 200, 300, 220), 0.7f, 1)
        ];

        IReadOnlyList<LineDetection> result = NdlKotenOcrPipeline.FilterDetections(detections);

        result.Should().ContainSingle()
            .Which.Box.Should().Be(new Box(200, 200, 300, 220));
    }

    [Fact]
    public void FilterDetections_keeps_higher_confidence_near_duplicate_and_drops_lower_one()
    {
        LineDetection[] detections =
        [
            new(new Box(100, 100, 200, 200), 0.7f, 1),
            new(new Box(102, 102, 198, 198), 0.95f, 1)
        ];

        IReadOnlyList<LineDetection> result = NdlKotenOcrPipeline.FilterDetections(detections);

        result.Should().ContainSingle()
            .Which.Confidence.Should().Be(0.95f);
    }

    [Fact]
    public void FilterDetections_keeps_adjacent_vertical_columns()
    {
        LineDetection[] detections =
        [
            new(new Box(10, 10, 30, 110), 0.8f, 1), // left column
            new(new Box(60, 10, 80, 110), 0.9f, 1), // right column
            new(new Box(10, 120, 30, 220), 0.7f, 1)
        ];

        IReadOnlyList<LineDetection> result = NdlKotenOcrPipeline.FilterDetections(detections);

        result.Should().HaveCount(3);
        result.Select(static d => d.Box).Should().Equal(
            new Box(10, 10, 30, 110),
            new Box(60, 10, 80, 110),
            new Box(10, 120, 30, 220));
    }

    [Fact]
    public void FilterDetections_returns_survivors_in_original_input_order()
    {
        // The lower-confidence near-duplicate appears first in the input but is
        // dropped; the remaining boxes must still come out in input order.
        LineDetection[] detections =
        [
            new(new Box(100, 100, 200, 200), 0.6f, 1), // dropped: duplicate of the next box
            new(new Box(102, 102, 198, 198), 0.9f, 1),
            new(new Box(300, 300, 400, 320), 0.8f, 1)
        ];

        IReadOnlyList<LineDetection> result = NdlKotenOcrPipeline.FilterDetections(detections);

        result.Select(static d => d.Box).Should().Equal(
            new Box(102, 102, 198, 198),
            new Box(300, 300, 400, 320));
    }

    [Fact]
    public void FilterDetections_boundary_keeps_five_pixel_boxes_and_drops_iou_exactly_seven_tenths()
    {
        // A = (0,0,10,10) area 100; B = (3,0,10,10) intersects in 7x10 = 70 with area 70,
        // so IoU = 70 / (100 + 70 - 70) = 0.7 exactly.
        LineDetection[] detections =
        [
            new(new Box(10, 10, 15, 30), 0.95f, 1), // width exactly 5
            new(new Box(40, 40, 60, 45), 0.9f, 1), // height exactly 5
            new(new Box(0, 0, 10, 10), 0.8f, 1),
            new(new Box(3, 0, 10, 10), 0.7f, 1) // IoU exactly 0.7 -> dropped
        ];

        IReadOnlyList<LineDetection> result = NdlKotenOcrPipeline.FilterDetections(detections);

        result.Select(static d => d.Box).Should().Equal(
            new Box(10, 10, 15, 30),
            new Box(40, 40, 60, 45),
            new Box(0, 0, 10, 10));
    }
}
