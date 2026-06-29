using LiteratureApp.Core.Ids;
using LiteratureApp.Ocr;

namespace LiteratureApp.UI;

public sealed class PdfRenderViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    public string DocumentInstanceId { get; set; } = "";
    public string PageId { get; set; } = "";
    public string PresetId { get; set; } = "";
    public string Dpi { get; set; } = "200";
    public bool ForceRerender { get; set; }
    public string Output { get; set; } = "PDF rendering is MVP-only. Cache images are local and never synced or returned by MCP.";
    public string RendererStatus { get; set; } = "Renderer status has not been checked.";
    public AsyncCommand RenderCommand { get; }
    public AsyncCommand RunOcrCommand { get; }
    public AsyncCommand CheckRendererCommand { get; }
    public AsyncCommand CheckBBoxWarningsCommand { get; }

    public PdfRenderViewModel(MainWindowViewModel main)
    {
        _main = main;
        CheckRendererCommand = new AsyncCommand(async () =>
        {
            var status = await (await _main.ServicesAsync()).PageRenders.GetRendererAvailabilityAsync();
            RendererStatus = $"{status.RendererName}: {(status.IsAvailable ? "available" : "renderer_unavailable")} - {status.Message}";
            Raise(nameof(RendererStatus));
        });
        CheckBBoxWarningsCommand = new AsyncCommand(async () =>
        {
            var page = LiteratureApp.Core.Ids.PageId.Parse(PageId);
            var basis = await (await _main.ServicesAsync()).PageCoordinates.GetPageCoordinateBasisAsync(page);
            var warnings = await (await _main.ServicesAsync()).PageCoordinates.DetectBBoxWarningsAsync(page);
            Output = basis.IsSuccess ? $"basis={basis.Value.CoordinateBasis}; size={basis.Value.BasisWidth}x{basis.Value.BasisHeight}; rotation={basis.Value.Rotation}; renderer={basis.Value.RendererBasisVersion}; sourceHash={(basis.Value.SourceFileHash is null ? "absent" : "present")}\nWarnings: {(warnings.Count == 0 ? BBoxWarning.None : string.Join(", ", warnings))}" : $"ERROR {basis.ErrorCode}: {basis.ErrorMessage}";
            Raise(nameof(Output));
        });
        RenderCommand = new AsyncCommand(async () =>
        {
            if (!int.TryParse(Dpi, out var dpi)) { Output = "ERROR validation_failed: DPI must be an integer."; Raise(nameof(Output)); return; }
            var result = await (await _main.ServicesAsync()).PageRenders.RenderPageAsync(new PageRenderRequest(LiteratureApp.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId), LiteratureApp.Core.Ids.PageId.Parse(PageId), Dpi: dpi, Purpose: PageRenderPurpose.Preview, ForceRerender: ForceRerender));
            Output = result.IsSuccess ? $"{result.Value.Status}\n{result.Value.WidthPixels}x{result.Value.HeightPixels} @ {result.Value.Dpi} dpi\n{result.Value.CacheImagePath ?? result.Value.Warning}" : $"ERROR {result.ErrorCode}: {result.ErrorMessage}";
            Raise(nameof(Output));
        });
        RunOcrCommand = new AsyncCommand(async () =>
        {
            if (!int.TryParse(Dpi, out var dpi)) { Output = "ERROR validation_failed: DPI must be an integer."; Raise(nameof(Output)); return; }
            var result = await (await _main.ServicesAsync()).Ocr.RunPresetOnRenderedPdfPageAsync(LiteratureApp.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId), OcrPresetId.Parse(PresetId), LiteratureApp.Core.Ids.PageId.Parse(PageId), dpi);
            Output = result.IsSuccess ? $"Rendered PDF OCR run: {result.Value.OcrRunId}\n{result.Value.State}" : $"ERROR {result.ErrorCode}: {result.ErrorMessage}";
            Raise(nameof(Output));
            await _main.LogOperationAsync("run_rendered_pdf_ocr", Output);
        });
    }
}
