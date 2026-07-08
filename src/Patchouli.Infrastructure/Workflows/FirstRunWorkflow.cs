using Patchouli.Core.Import;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Operations;
using Patchouli.Core.Results;

namespace Patchouli.Infrastructure.Workflows;

public sealed class FirstRunWorkflow
{
    private readonly ILibraryIdentityService _libraryIdentityService;
    private readonly PdfDiscoveryService _pdfDiscoveryService;
    private readonly PdfImportWorkflow _pdfImportWorkflow;
    private readonly IBlockingOperationService? _blockingOperations;

    public FirstRunWorkflow(
        ILibraryIdentityService libraryIdentityService,
        PdfDiscoveryService pdfDiscoveryService,
        PdfImportWorkflow pdfImportWorkflow,
        IBlockingOperationService? blockingOperations = null)
    {
        _libraryIdentityService = libraryIdentityService;
        _pdfDiscoveryService = pdfDiscoveryService;
        _pdfImportWorkflow = pdfImportWorkflow;
        _blockingOperations = blockingOperations;
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
        var normalizedRoot = Path.GetFullPath(scanRoot);
        var scanOperationId = await TryStartInitialRootScanAsync(normalizedRoot, cancellationToken);
        var result = await _pdfDiscoveryService.ScanDirectoryAsync(scanRoot, cancellationToken);
        if (result.Candidates.Count == 0)
        {
            await TryFailInitialRootScanAsync(
                scanOperationId,
                AppErrorCodes.NotFound,
                "No PDF files were found in the selected folder.",
                "Initial PDF root scan found no PDFs.",
                ["Choose a different PDF folder", "Add PDFs to the folder and retry"],
                cancellationToken);
            return new FirstRunWorkflowState(FirstRunStep.Scan,
                "未在指定目录中找到 PDF 文件。", null,
                null, null, null, null, "未找到 PDF 文件。", false);
        }

        await TryCompleteInitialRootScanAsync(
            scanOperationId,
            $"Initial PDF root scan found {result.TotalCount} candidate(s).",
            cancellationToken);

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

    private async Task<BlockingOperationId?> TryStartInitialRootScanAsync(
        string scanRoot,
        CancellationToken cancellationToken)
    {
        if (_blockingOperations is null)
        {
            return null;
        }

        try
        {
            var started = await _blockingOperations.StartAsync(
                BlockingOperationTypes.InitialRootScan,
                BlockingOperationScopeTypes.FileSearchRoot,
                scanRoot,
                canCancel: true,
                progressLabel: "Scanning initial PDF root.",
                nextActions: ["Choose a different PDF folder", "Add PDFs to the folder and retry"],
                cancellationToken: cancellationToken);
            return started.IsSuccess ? started.Value.OperationId : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task TryCompleteInitialRootScanAsync(
        BlockingOperationId? operationId,
        string progressLabel,
        CancellationToken cancellationToken)
    {
        if (_blockingOperations is null || operationId is null)
        {
            return;
        }

        try
        {
            await _blockingOperations.CompleteAsync(
                operationId.Value,
                progressLabel,
                Array.Empty<string>(),
                cancellationToken);
        }
        catch
        {
        }
    }

    private async Task TryFailInitialRootScanAsync(
        BlockingOperationId? operationId,
        string errorCode,
        string errorMessage,
        string progressLabel,
        IReadOnlyList<string> nextActions,
        CancellationToken cancellationToken)
    {
        if (_blockingOperations is null || operationId is null)
        {
            return;
        }

        try
        {
            await _blockingOperations.FailAsync(
                operationId.Value,
                errorCode,
                errorMessage,
                progressLabel,
                nextActions,
                cancellationToken);
        }
        catch
        {
        }
    }
}
