using FluentAssertions;
using Patchouli.Ocr;

namespace Patchouli.Tests;

public sealed class RealPdfRendererTests
{
    [Fact]
    public async Task ProductionPageRenderService_uses_pdfium_renderer()
    {
        PdfiumPdfPageRenderer renderer = new();

        PdfRendererAvailability status = await renderer.CheckAvailabilityAsync();

        status.RendererName.Should().Be("PDFium");
        status.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public void PageRenderService_can_use_fake_renderer_in_tests()
    {
        new FakePdfPageRenderer().Should().BeAssignableTo<IPdfPageRenderer>();
    }

    [Fact]
    public async Task PdfiumRenderer_invalid_pdf_returns_render_failure()
    {
        string pdf = Path.GetTempFileName();
        string output = Path.ChangeExtension(Path.GetTempFileName(), ".png");
        await File.WriteAllTextAsync(pdf, "not a pdf");

        try
        {
            PdfiumPdfPageRenderer renderer = new();
            Func<Task<PdfPageRenderOutput>> action = () => renderer.RenderPageToPngAsync(pdf, 0, output, 200);
            await action.Should().ThrowAsync<Exception>();
        }
        finally
        {
            File.Delete(pdf);
            if (File.Exists(output))
            {
                File.Delete(output);
            }
        }
    }

    [Fact]
    public async Task PdfiumRenderer_invalid_page_index_returns_failure()
    {
        PdfiumPdfPageRenderer renderer = new();
        Func<Task<PdfPageRenderOutput>> action = () =>
            renderer.RenderPageToPngAsync("fixture.pdf", -1, Path.GetTempFileName(), 200);
        await action.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task PdfiumRenderer_outputs_png_file_and_basis_version()
    {
        string pdf = Path.Combine(Path.GetTempPath(), $"fixture-{Guid.NewGuid():N}.pdf");
        string output = Path.ChangeExtension(pdf, ".png");

        try
        {
            File.Copy(TestFixtures.RealThreePagePdf, pdf);
            PdfiumPdfPageRenderer renderer = new();

            PdfPageRenderOutput result = await renderer.RenderPageToPngAsync(pdf, 0, output, 100);

            File.Exists(output).Should().BeTrue();
            result.WidthPixels.Should().BeGreaterThan(0);
            result.HeightPixels.Should().BeGreaterThan(0);
            result.RendererBasisVersion.Should().Be($"pdfium-{PdfiumDocumentEngine.Version}-dpi100");
        }
        finally
        {
            if (File.Exists(pdf))
            {
                File.Delete(pdf);
            }

            if (File.Exists(output))
            {
                File.Delete(output);
            }
        }
    }

    [Fact]
    public async Task PdfiumRenderer_outputs_bgra_pixel_buffer_for_preview()
    {
        PdfiumPdfPageRenderer renderer = new();

        PdfPagePixelBufferOutput raster = await renderer.RenderPageToBgraBytesAsync(TestFixtures.RealThreePagePdf, 0,
            100);

        raster.WidthPixels.Should().BeGreaterThan(0);
        raster.HeightPixels.Should().BeGreaterThan(0);
        raster.Stride.Should().BeGreaterThanOrEqualTo(raster.WidthPixels * 4);
        raster.BgraBytes.Length.Should().Be(raster.Stride * raster.HeightPixels);
        renderer.Should().BeAssignableTo<IPdfPagePixelBufferRenderer>();
    }

    [Fact]
    public void Production_services_wire_pdfium_pdf_renderer()
    {
        File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "AppServices.cs")).Should()
            .Contain("PdfiumPdfPageRenderer");
    }

    [Fact]
    public void Pdf_workspace_uses_pdfium_pixel_buffer_without_png_decode()
    {
        string workspace = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "ViewModels",
            "Ocr", "PdfWorkspaceViewModel.cs"));

        workspace.Should().Contain("RenderPageToBgraBytesAsync");
        workspace.Should().NotContain("RenderPageToPngBytesAsync");
    }

    [Fact]
    public void Production_pdf_packages_include_pdfium_only()
    {
        string packages = File.ReadAllText(TestPaths.FromRepositoryRoot("Directory.Packages.props"));
        packages.Should().Contain("PDFiumCore");
        packages.Should().NotContain("MuPDF.NET");
    }

    [Fact]
    public void Product_shell_uses_literature_manager_preview_not_full_pdf_viewer()
    {
        File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "PdfWorkspacePage.axaml"))
            .Should().Contain("工作台预览");
    }

    [Fact]
    public void Native_poppler_process_renderer_is_not_used_anymore()
    {
        IEnumerable<string> files = Directory
            .EnumerateFiles(TestPaths.FromRepositoryRoot("src"), "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                string[] segments = Path.GetRelativePath(TestPaths.RepositoryRoot, path)
                    .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return !segments.Contains("bin") && !segments.Contains("obj");
            });
        string content = string.Join('\n', files.Select(File.ReadAllText));
        content.Should().NotContain("ExternalProcessPdfPageRenderer");
        content.Should().NotContain("pdftoppm");
        content.Should().NotContain("pdfinfo");
        content.Should().NotContain("Poppler");
    }
}
