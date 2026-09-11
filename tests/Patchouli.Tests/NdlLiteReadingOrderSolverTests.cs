using FluentAssertions;
using Patchouli.Infrastructure.Ocr.NdlLite;

namespace Patchouli.Tests;

public sealed class NdlLiteReadingOrderSolverTests
{
    [Fact]
    public void Solve_returns_empty_for_no_boxes()
    {
        NdlLiteReadingOrderSolver.Solve(Array.Empty<NdlLiteBox>()).Should().BeEmpty();
    }

    [Fact]
    public void Solve_orders_vertical_columns_right_to_left_and_top_to_bottom()
    {
        NdlLiteBox[] boxes =
        [
            new(10, 10, 30, 110), // left column, top
            new(10, 120, 30, 220), // left column, bottom
            new(60, 10, 80, 110), // right column, top
            new(60, 120, 80, 220) // right column, bottom
        ];

        int[] ranks = NdlLiteReadingOrderSolver.Solve(boxes);

        ranks.Should().HaveCount(4);
        ranks[2].Should().BeLessThan(ranks[0]);
        ranks[3].Should().BeLessThan(ranks[1]);
        ranks[2].Should().BeLessThan(ranks[3]);
        ranks[0].Should().BeLessThan(ranks[1]);
    }

    [Fact]
    public void Solve_orders_horizontal_lines_top_to_bottom()
    {
        NdlLiteBox[] boxes =
        [
            new(10, 100, 110, 120),
            new(10, 10, 110, 30),
            new(10, 55, 110, 75)
        ];

        int[] ranks = NdlLiteReadingOrderSolver.Solve(boxes);

        ranks[1].Should().BeLessThan(ranks[2]);
        ranks[2].Should().BeLessThan(ranks[0]);
    }

    [Fact]
    public void CalcIou_uses_the_upstream_inclusive_span_formula()
    {
        // Identical 10x10 boxes: intersection = 10 * 10, areas = 10 * 10, union = 100.
        NdlLiteReadingOrderSolver.CalcIou(new NdlLiteBox(0, 0, 9, 9), new NdlLiteBox(0, 0, 9, 9))
            .Should().BeApproximately(1.0, 1e-9);

        // A 5x5 box inside a 10x10 target: intersection 25, box area 25, target area 100,
        // union 100, so the inclusive formula gives 0.25.
        NdlLiteReadingOrderSolver.CalcIou(new NdlLiteBox(0, 0, 4, 4), new NdlLiteBox(0, 0, 9, 9))
            .Should().BeApproximately(0.25, 1e-9);
    }

    [Fact]
    public void Normalize_repairs_inverted_boxes_without_swapping_their_origin()
    {
        // Upstream clamps X1/Y1 up to X0/Y0, so an inverted box collapses instead
        // of being reordered into a valid rectangle.
        NdlLiteBox[] normalized = NdlLiteReadingOrderSolver.Normalize(
            [new NdlLiteBox(30, 40, 10, 20), new NdlLiteBox(0, 0, 100, 100)], 1.0, 0.25);

        normalized[0].X0.Should().Be(normalized[0].X1);
        normalized[0].Y0.Should().Be(normalized[0].Y1);
        normalized[0].X0.Should().BePositive();
    }

    [Fact]
    public void CalcMinSpan_returns_the_longest_run_of_the_minimum_value()
    {
        (int start, int end, double score) = NdlLiteReadingOrderSolver.CalcMinSpan([5, 5, 1, 1, 1, 5]);

        start.Should().Be(2);
        end.Should().Be(5);
        score.Should().BeApproximately(-0.2, 1e-9);
    }

    [Fact]
    public void CalcMinSpan_handles_single_sample_histograms()
    {
        NdlLiteReadingOrderSolver.CalcMinSpan([7]).Should().Be((0, 1, 0.0));
    }
}
