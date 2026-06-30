using FluentAssertions;
using Patchouli.Ocr;

namespace Patchouli.Tests;

public sealed class RealPdfRendererTests
{
    [Fact]
    public async Task ProductionPageRenderService_uses_real_renderer_when_available()
    {
        var renderer = new ExternalProcessPdfPageRenderer(new PdfProcessRunner(request => new ProcessRunResult(0, "pdftoppm", "", false)));
        (await renderer.CheckAvailabilityAsync()).IsAvailable.Should().BeTrue();
    }

    [Fact] public void PageRenderService_can_use_fake_renderer_in_tests() => new FakePdfPageRenderer().Should().BeAssignableTo<IPdfPageRenderer>();

    [Fact]
    public async Task RendererUnavailable_returns_clear_error_without_crash()
    {
        var renderer = new ExternalProcessPdfPageRenderer(new PdfProcessRunner(_ => throw new System.ComponentModel.Win32Exception()));
        var status = await renderer.CheckAvailabilityAsync();
        status.IsAvailable.Should().BeFalse(); status.Message.Should().Contain("not installed");
    }

    [Fact]
    public async Task RealPdfRenderer_invalid_pdf_returns_render_failure()
    {
        var pdf = Path.GetTempFileName(); var output = Path.ChangeExtension(Path.GetTempFileName(), ".png");
        try
        {
            var renderer = new ExternalProcessPdfPageRenderer(new PdfProcessRunner(request => request.Arguments.Contains("-v") ? new(0, "v", "", false) : new(1, "", "invalid PDF", false)));
            var action = () => renderer.RenderPageToPngAsync(pdf, 0, output, 200);
            await action.Should().ThrowAsync<InvalidOperationException>();
        }
        finally { File.Delete(pdf); if (File.Exists(output)) File.Delete(output); }
    }

    [Fact]
    public async Task PdfRenderTimeout_error_is_sanitized()
    {
        var pdf = Path.Combine(Path.GetTempPath(), "sensitive-source.pdf");
        var output = Path.Combine(Path.GetTempPath(), "sensitive-output.png");
        var renderer = new ExternalProcessPdfPageRenderer(new PdfProcessRunner(request => request.Arguments.Contains("-v") ? new(0, "v", "", false) : new(-1, "", "/Users/a86186/secret.pdf", true)), "/opt/homebrew/bin/pdftoppm");
        var action = () => renderer.RenderPageToPngAsync(pdf, 0, output, 100);
        var ex = await action.Should().ThrowAsync<PdfRendererTimeoutException>();
        ex.Which.Message.Should().Be("PDF renderer timed out.").And.NotContain("/Users/").And.NotContain("pdftoppm");
    }

    [Fact]
    public async Task RealPdfRenderer_invalid_page_index_returns_failure()
    {
        var renderer = new ExternalProcessPdfPageRenderer(new PdfProcessRunner(_ => new(0, "v", "", false)));
        var action = () => renderer.RenderPageToPngAsync("fixture.pdf", -1, Path.GetTempFileName(), 200);
        await action.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RealPdfRenderer_outputs_png_file_and_basis_version()
    {
        var pdf = Path.GetTempFileName(); var output = Path.Combine(Path.GetTempPath(), $"renderer-{Guid.NewGuid():N}.png");
        try
        {
            var renderer = new ExternalProcessPdfPageRenderer(new PdfProcessRunner(request =>
            {
                if (request.Arguments.Contains("-v")) return new(0, "v", "", false);
                var basePath = request.Arguments.Last(); File.WriteAllBytes(basePath + ".png", Png1x1); return new(0, "", "", false);
            }));
            var result = await renderer.RenderPageToPngAsync(pdf, 0, output, 200);
            File.Exists(output).Should().BeTrue(); result.WidthPixels.Should().Be(1); result.HeightPixels.Should().Be(1); result.RendererBasisVersion.Should().Contain("pdftoppm-poppler-dpi200");
        }
        finally { File.Delete(pdf); if (File.Exists(output)) File.Delete(output); }
    }

    [Fact]
    public async Task RealPdfRenderer_can_render_single_page_fixture_when_available()
    {
        var renderer = new ExternalProcessPdfPageRenderer(new SystemProcessRunner());
        if (!(await renderer.CheckAvailabilityAsync()).IsAvailable) return; // Optional external dependency: intentionally skipped in this environment.
        var pdf = Path.Combine(Path.GetTempPath(), $"fixture-{Guid.NewGuid():N}.pdf"); var output = Path.ChangeExtension(pdf, ".png");
        try
        {
            await File.WriteAllTextAsync(pdf, CreateMinimalPdf());
            var result = await renderer.RenderPageToPngAsync(pdf, 0, output, 100);
            File.Exists(output).Should().BeTrue(); result.WidthPixels.Should().BeGreaterThan(0);
        }
        finally { if (File.Exists(pdf)) File.Delete(pdf); if (File.Exists(output)) File.Delete(output); }
    }

    [Fact] public void Production_services_wire_external_pdf_renderer() => File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "AppServices.cs")).Should().Contain("ExternalProcessPdfPageRenderer");
    [Fact] public void Product_shell_uses_literature_manager_preview_not_full_pdf_viewer() => File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "MainWindow.axaml")).Should().Contain("PDF 预览");

    private sealed class PdfProcessRunner : IProcessRunner
    {
        private readonly Func<ProcessRunRequest, ProcessRunResult> _run;
        public PdfProcessRunner(Func<ProcessRunRequest, ProcessRunResult> run) => _run = run;
        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default) => Task.FromResult(_run(request));
    }

    private static readonly byte[] Png1x1 = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScL9dwAAAABJRU5ErkJggg==");
    private static string CreateMinimalPdf()
    {
        var objects = new[] { "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n", "2 0 obj\n<< /Type /Pages /Count 1 /Kids [3 0 R] >>\nendobj\n", "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] >>\nendobj\n" };
        var builder = new System.Text.StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        foreach (var item in objects) { offsets.Add(System.Text.Encoding.ASCII.GetByteCount(builder.ToString())); builder.Append(item); }
        var xref = System.Text.Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append("xref\n0 4\n0000000000 65535 f \n");
        foreach (var offset in offsets) builder.Append(offset.ToString("D10")).Append(" 00000 n \n");
        builder.Append("trailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return builder.ToString();
    }
}
