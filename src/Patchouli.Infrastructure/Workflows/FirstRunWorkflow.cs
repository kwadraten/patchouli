using Patchouli.Core.Import;
using Patchouli.Core.Library;

namespace Patchouli.Infrastructure.Workflows;

public sealed class FirstRunWorkflow
{
    private readonly ILibraryIdentityService _libraryIdentityService;
    private readonly PdfDiscoveryService _pdfDiscoveryService;
    private readonly PdfImportWorkflow _pdfImportWorkflow;

    public FirstRunWorkflow(
        ILibraryIdentityService libraryIdentityService,
        PdfDiscoveryService pdfDiscoveryService,
        PdfImportWorkflow pdfImportWorkflow)
    {
        _libraryIdentityService = libraryIdentityService;
        _pdfDiscoveryService = pdfDiscoveryService;
        _pdfImportWorkflow = pdfImportWorkflow;
    }

    public async Task<FirstRunWorkflowState> CreateLibraryAsync(
        string displayName, CancellationToken cancellationToken = default)
    {
        var result = await _libraryIdentityService.CreateLibraryAsync(displayName, cancellationToken);
        if (result.IsFailure)
            return new FirstRunWorkflowState(FirstRunStep.Library, result.ErrorMessage!, null,
                null, null, null, null, result.ErrorMessage, false);

        return new FirstRunWorkflowState(FirstRunStep.Scan, "资料库已创建。请选择要扫描的 PDF 文件夹。",
            null, result.Value.LibraryId.ToString(), null, null, null, null, false);
    }

    public async Task<FirstRunWorkflowState> ScanDirectoryAsync(
        string scanRoot, CancellationToken cancellationToken = default)
    {
        var result = await _pdfDiscoveryService.ScanDirectoryAsync(scanRoot, cancellationToken);
        if (result.Candidates.Count == 0)
            return new FirstRunWorkflowState(FirstRunStep.Scan,
                "未在指定目录中找到 PDF 文件。", null,
                null, null, null, null, "未找到 PDF 文件。", false);

        return new FirstRunWorkflowState(FirstRunStep.Import,
            $"找到 {result.TotalCount} 个候选项。正在将发现的文件导入为题录。",
            null, null, null, null, null, null, false);
    }

    public async Task<FirstRunWorkflowState> ImportPdfAsync(
        PdfImportRequest request, CancellationToken cancellationToken = default)
    {
        var importResult = await _pdfImportWorkflow.ImportPdfAsync(request, cancellationToken);
        if (!importResult.Success)
            return new FirstRunWorkflowState(FirstRunStep.Import, importResult.ErrorMessage ?? "导入失败。",
                request.PdfPath, null, null, null, null, importResult.ErrorMessage, false);

        return new FirstRunWorkflowState(FirstRunStep.MinerUConfig,
            "PDF 已导入。请配置 OCR Preset 后从题录菜单运行 OCR。",
            request.PdfPath, null, importResult.CreatedItemId,
            importResult.CreatedFileAssetId, importResult.CreatedDocumentInstanceId,
            null, false);
    }
}
