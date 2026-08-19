using FluentAssertions;
using Patchouli.Infrastructure.Ocr.NdlKoten;
using SkiaSharp;

namespace Patchouli.Tests;

public sealed class ReadingOrderSolverTests
{
    [Fact]
    public void Solve_returns_empty_for_no_boxes()
    {
        int[] ranks = ReadingOrderSolver.Solve(Array.Empty<Box>());

        ranks.Should().BeEmpty();
    }

    [Fact]
    public void Solve_orders_vertical_columns_right_to_left()
    {
        // Two vertical columns: right column boxes have larger x.
        // Each box is taller than wide to be treated as vertical.
        Box[] boxes =
        [
            new(10, 10, 30, 110), // left column, top
            new(10, 120, 30, 220), // left column, bottom
            new(60, 10, 80, 110), // right column, top
            new(60, 120, 80, 220) // right column, bottom
        ];

        int[] ranks = ReadingOrderSolver.Solve(boxes);

        ranks.Should().HaveCount(4);
        // Right column should come before left column.
        ranks[2].Should().BeLessThan(ranks[0]);
        ranks[3].Should().BeLessThan(ranks[1]);
        // Within each column, top should come before bottom.
        ranks[2].Should().BeLessThan(ranks[3]);
        ranks[0].Should().BeLessThan(ranks[1]);
    }

    [Fact]
    public void Solve_orders_horizontal_lines_top_to_bottom()
    {
        Box[] boxes =
        [
            new(10, 100, 110, 120), // bottom line
            new(10, 10, 110, 30), // top line
            new(10, 55, 110, 75) // middle line
        ];

        int[] ranks = ReadingOrderSolver.Solve(boxes);

        ranks.Should().HaveCount(3);
        ranks[1].Should().BeLessThan(ranks[2]);
        ranks[2].Should().BeLessThan(ranks[0]);
    }

    [Fact]
    public void Pipeline_preserves_solver_order_for_vertical_page()
    {
        LineDetection[] detections =
        [
            new(new Box(10, 10, 30, 110), 0.9f, 1), // left column
            new(new Box(60, 10, 80, 110), 0.9f, 1) // right column
        ];

        IReadOnlyList<LineDetection> ordered = NdlKotenOcrPipeline.OrderDetections(detections);

        ordered.Select(static detection => detection.Box).Should().Equal(
            new Box(60, 10, 80, 110),
            new Box(10, 10, 30, 110));
    }

    [Fact]
    public void Recognizer_rotates_vertical_line_counter_clockwise()
    {
        using SKBitmap source = new(2, 3);
        source.SetPixel(0, 0, SKColors.Red);
        source.SetPixel(1, 0, SKColors.Green);
        source.SetPixel(0, 2, SKColors.Blue);
        source.SetPixel(1, 2, SKColors.Yellow);

        using SKBitmap rotated = ParseqRecognizer.RotateVerticalLineCounterClockwise(source);

        rotated.Width.Should().Be(3);
        rotated.Height.Should().Be(2);
        rotated.GetPixel(0, 0).Should().Be(SKColors.Green);
        rotated.GetPixel(2, 0).Should().Be(SKColors.Yellow);
        rotated.GetPixel(0, 1).Should().Be(SKColors.Red);
        rotated.GetPixel(2, 1).Should().Be(SKColors.Blue);
    }
}
