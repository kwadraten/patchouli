using Avalonia;
using FluentAssertions;
using Patchouli.Core.Layout;
using Patchouli.Reading;

namespace Patchouli.Tests;

public sealed class ReadingMediaGeometryTests
{
    [Fact]
    public void Computes_the_source_subrect_from_the_normalized_region()
    {
        (Rect source, Size destination) = ReadingMediaGeometry.ComputeDrawRects(
            new Size(200, 100), new NormalizedBBox(0.25, 0.5, 0.5, 0.5), 100, 240);

        source.Should().Be(new Rect(50, 50, 100, 50));
        destination.Width.Should().BeApproximately(100, 0.01);
        destination.Height.Should().BeApproximately(50, 0.01);
    }

    [Fact]
    public void Preserves_the_source_aspect_ratio_when_fitting()
    {
        // A tall source region in a wide content box is capped by height, not stretched.
        (Rect source, Size destination) = ReadingMediaGeometry.ComputeDrawRects(
            new Size(100, 100), new NormalizedBBox(0, 0, 1, 1), 400, 240);

        double sourceAspect = source.Width / source.Height;
        double destinationAspect = destination.Width / destination.Height;
        destinationAspect.Should().BeApproximately(sourceAspect, 0.01);
        destination.Height.Should().BeLessThanOrEqualTo(240 + 0.01);
    }

    [Fact]
    public void Caps_the_destination_height_and_scales_width_down_proportionally()
    {
        (Rect source, Size destination) = ReadingMediaGeometry.ComputeDrawRects(
            new Size(1000, 100), new NormalizedBBox(0, 0, 1, 1), 2000, 60);

        destination.Height.Should().BeApproximately(60, 0.01);
        destination.Width.Should().BeApproximately(600, 0.01);
    }

    [Fact]
    public void Trims_a_region_overhanging_the_page()
    {
        (double x, double y, double width, double height) =
            ReadingMediaGeometry.ClampRegion(new NormalizedBBox(0.8, 0.8, 0.5, 0.5));

        x.Should().BeApproximately(0.8, 0.001);
        y.Should().BeApproximately(0.8, 0.001);
        width.Should().BeApproximately(0.2, 0.001);
        height.Should().BeApproximately(0.2, 0.001);
    }

    [Theory]
    [InlineData(0, 0, 0, 1)]
    [InlineData(0, 0, 1, 0)]
    [InlineData(0, 0, -1, 1)]
    [InlineData(0, 0, 1, -1)]
    [InlineData(1, 1, 1, 1)]
    [InlineData(2, 2, 1, 1)]
    public void Degenerate_regions_fall_back_to_the_whole_page(
        double x, double y, double width, double height)
    {
        (double clampedX, double clampedY, double clampedWidth, double clampedHeight) =
            ReadingMediaGeometry.ClampRegion(new NormalizedBBox(x, y, width, height));

        clampedX.Should().Be(0);
        clampedY.Should().Be(0);
        clampedWidth.Should().Be(1);
        clampedHeight.Should().Be(1);
    }

    [Fact]
    public void Tolerates_an_invalid_image_size()
    {
        (Rect source, Size destination) = ReadingMediaGeometry.ComputeDrawRects(
            new Size(0, 0), new NormalizedBBox(0, 0, 1, 1), 100, 100);

        source.Width.Should().BeGreaterThan(0);
        source.Height.Should().BeGreaterThan(0);
        destination.Width.Should().BeGreaterThan(0);
        destination.Height.Should().BeGreaterThan(0);
    }
}
