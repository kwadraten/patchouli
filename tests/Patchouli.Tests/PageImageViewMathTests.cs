using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Core.View;
using Patchouli.Infrastructure.Rendering;

namespace Patchouli.Tests;

/// <summary>
/// Issue #11: view region math — box crops get a small margin clipped to the page,
/// and render DPI targets the region's resolution for the requested max edge.
/// </summary>
public sealed class PageImageViewMathTests
{
    [Fact]
    public void WithMargin_expands_box_by_margin_and_clips_to_page()
    {
        NormalizedBBox bbox = new(0.1, 0.2, 0.3, 0.4);

        NormalizedBBox crop = PageImageViewService.WithMargin(bbox);

        crop.X.Should().BeApproximately(0.08, 1e-9);
        crop.Y.Should().BeApproximately(0.18, 1e-9);
        crop.Width.Should().BeApproximately(0.34, 1e-9);
        crop.Height.Should().BeApproximately(0.44, 1e-9);
        crop.Validate().IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void WithMargin_never_leaves_the_page()
    {
        NormalizedBBox bbox = new(0.0, 0.0, 0.99, 0.99);

        NormalizedBBox crop = PageImageViewService.WithMargin(bbox);

        crop.X.Should().Be(0);
        crop.Y.Should().Be(0);
        (crop.X + crop.Width).Should().BeLessThanOrEqualTo(1);
        (crop.Y + crop.Height).Should().BeLessThanOrEqualTo(1);
        crop.Validate().IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void WithMargin_keeps_positive_size_for_edge_boxes()
    {
        NormalizedBBox bbox = new(0.99, 0.99, 0.01, 0.01);

        NormalizedBBox crop = PageImageViewService.WithMargin(bbox);

        crop.Width.Should().BePositive();
        crop.Height.Should().BePositive();
        crop.Validate().IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void ChooseDpi_targets_region_resolution_not_whole_page()
    {
        // A4-ish page in points; a small region should render at higher DPI than the page.
        Page page = FakePage(595, 842);
        var region = new NormalizedBBox(0.1, 0.1, 0.2, 0.2);

        int regionDpi = PageImageViewService.ChooseDpi(page, region, 2048);
        int pageDpi = PageImageViewService.ChooseDpi(page, new NormalizedBBox(0, 0, 1, 1), 2048);

        regionDpi.Should().BeGreaterThan(pageDpi);
        regionDpi.Should().BeInRange(72, 600);
        pageDpi.Should().BeInRange(72, 600);
    }

    [Fact]
    public void ChooseDpi_clamps_to_supported_range()
    {
        Page page = FakePage(595, 842);

        // Absurdly small region would demand enormous DPI.
        int tiny = PageImageViewService.ChooseDpi(page, new NormalizedBBox(0.5, 0.5, 0.001, 0.001), 2048);
        // Enormous max edge on a full page would demand tiny DPI.
        int huge = PageImageViewService.ChooseDpi(page, new NormalizedBBox(0, 0, 1, 1), 8192);

        tiny.Should().Be(600);
        huge.Should().Be(72);
    }

    [Fact]
    public void ChooseDpi_falls_back_when_page_size_unknown()
    {
        Page page = FakePage(null, null);

        PageImageViewService.ChooseDpi(page, new NormalizedBBox(0, 0, 1, 1), 2048).Should().Be(200);
    }

    [Theory]
    [InlineData(0, 2048, false)]
    [InlineData(-1, 2048, false)]
    [InlineData(1, 63, false)]
    [InlineData(1, 8193, false)]
    [InlineData(1, 64, true)]
    [InlineData(1, 2048, true)]
    [InlineData(1, 8192, true)]
    public void PageImageViewRequest_validate_bounds(int pageIndex, int maxEdge, bool expectedOk)
    {
        var request = new PageImageViewRequest(
            DocumentInstanceId.New(), pageIndex, null, null,
            false, PageImageViewContext.Page, maxEdge);

        Result result = request.Validate();

        result.IsSuccess.Should().Be(expectedOk);
    }

    [Fact]
    public void View_result_constants_match_protocol_contract()
    {
        PageImageViewService.MaxPngBytes.Should().Be(8L * 1024 * 1024);
        PageImageViewService.MaxPixels.Should().Be(16_777_216);
        PageImageViewRequest.DefaultMaxEdge.Should().Be(2048);
    }

    private static Page FakePage(double? width, double? height) =>
        new(
            PageId.New(),
            DocumentInstanceId.New(),
            0, "1", width, height,
            0, "pdfium", width, height, "v1", null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
