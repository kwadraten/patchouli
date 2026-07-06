using FluentAssertions;
using Patchouli.Ocr;

namespace Patchouli.Tests;

public sealed class RealPdfRendererTests
{
    [Fact]
    public async Task ProductionPageRenderService_uses_mupdf_net_renderer()
    {
        var renderer = new MuPdfNetPdfPageRenderer();

        var status = await renderer.CheckAvailabilityAsync();

        status.RendererName.Should().Be("MuPDF.NET");
        status.IsAvailable.Should().BeTrue();
    }

    [Fact] public void PageRenderService_can_use_fake_renderer_in_tests() => new FakePdfPageRenderer().Should().BeAssignableTo<IPdfPageRenderer>();

    [Fact]
    public async Task MuPdfNetRenderer_invalid_pdf_returns_render_failure()
    {
        var pdf = Path.GetTempFileName();
        var output = Path.ChangeExtension(Path.GetTempFileName(), ".png");
        await File.WriteAllTextAsync(pdf, "not a pdf");

        try
        {
            var renderer = new MuPdfNetPdfPageRenderer();
            var action = () => renderer.RenderPageToPngAsync(pdf, 0, output, 200);
            await action.Should().ThrowAsync<Exception>();
        }
        finally
        {
            File.Delete(pdf);
            if (File.Exists(output)) File.Delete(output);
        }
    }

    [Fact]
    public async Task MuPdfNetRenderer_invalid_page_index_returns_failure()
    {
        var renderer = new MuPdfNetPdfPageRenderer();
        var action = () => renderer.RenderPageToPngAsync("fixture.pdf", -1, Path.GetTempFileName(), 200);
        await action.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task MuPdfNetRenderer_outputs_png_file_and_basis_version()
    {
        var pdf = Path.Combine(Path.GetTempPath(), $"fixture-{Guid.NewGuid():N}.pdf");
        var output = Path.ChangeExtension(pdf, ".png");

        try
        {
            File.Copy(TestFixtures.RealThreePagePdf, pdf);
            var renderer = new MuPdfNetPdfPageRenderer();

            var result = await renderer.RenderPageToPngAsync(pdf, 0, output, 100);

            File.Exists(output).Should().BeTrue();
            result.WidthPixels.Should().BeGreaterThan(0);
            result.HeightPixels.Should().BeGreaterThan(0);
            result.RendererBasisVersion.Should().Be("mupdf-net-dpi100");
        }
        finally
        {
            if (File.Exists(pdf)) File.Delete(pdf);
            if (File.Exists(output)) File.Delete(output);
        }
    }

    [Fact] public void Production_services_wire_mupdf_net_pdf_renderer() => File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "AppServices.cs")).Should().Contain("MuPdfNetPdfPageRenderer");
    [Fact] public void Production_pdf_packages_include_pdf4llm_and_mupdf_net() => File.ReadAllText(TestPaths.FromRepositoryRoot("Directory.Packages.props")).Should().Contain("PDF4LLM").And.Contain("MuPDF.NET");
    [Fact] public void Product_shell_uses_literature_manager_preview_not_full_pdf_viewer() => File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "PdfWorkspacePage.axaml")).Should().Contain("工作台预览");

    [Fact]
    public void Native_poppler_process_renderer_is_not_used_anymore()
    {
        var files = Directory.EnumerateFiles(TestPaths.FromRepositoryRoot("src"), "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                var segments = Path.GetRelativePath(TestPaths.RepositoryRoot, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return !segments.Contains("bin") && !segments.Contains("obj");
            });
        var content = string.Join('\n', files.Select(File.ReadAllText));
        content.Should().NotContain("ExternalProcessPdfPageRenderer");
        content.Should().NotContain("pdftoppm");
        content.Should().NotContain("pdfinfo");
        content.Should().NotContain("Poppler");
    }
}
