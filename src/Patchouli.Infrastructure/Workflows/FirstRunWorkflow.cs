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

    public async Task<FirstRunImportResult> ScanAndImportAsync(
        string scanRoot,
        string? libraryId,
        CancellationToken cancellationToken = default,
        Action<int?, int?, string, string?>? progress = null)
    {
        var normalizedRoot = Path.GetFullPath(scanRoot);
        progress?.Invoke(null, null, "正在扫描 PDF 目录。", $"扫描目录：{normalizedRoot}");
        var operationId = await TryStartInitialRootScanAsync(normalizedRoot, cancellationToken);
        var scan = await _pdfDiscoveryService.ScanDirectoryAsync(scanRoot, cancellationToken);
        if (scan.Candidates.Count == 0)
        {
            await TryFailInitialRootScanAsync(
                operationId,
                AppErrorCodes.NotFound,
                "No PDF files were found in the selected folder.",
                "Initial PDF root scan found no PDFs.",
                ["Choose a different PDF folder", "Add PDFs to the folder and retry"],
                cancellationToken);
            progress?.Invoke(0, 0, "未找到可导入的 PDF。", "扫描完成：所选目录中没有 PDF 文件。");
            return new FirstRunImportResult(
                new FirstRunWorkflowState(FirstRunStep.Scan, "未在指定目录中找到 PDF 文件。", null,
                    libraryId, null, null, null, "未找到 PDF 文件。", false),
                scan, 0, 0);
        }

        var importedCount = 0;
        var failedCount = 0;
        string? lastDocumentInstanceId = null;
        string? lastPdfPath = null;
        progress?.Invoke(0, scan.Candidates.Count, $"已找到 {scan.Candidates.Count} 个 PDF，准备导入。", null);

        for (var index = 0; index < scan.Candidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = scan.Candidates[index];
            progress?.Invoke(index, scan.Candidates.Count, $"正在导入：{candidate.FileName}", candidate.Path);
            await TryUpdateInitialRootScanAsync(
                operationId,
                index,
                scan.Candidates.Count,
                $"Importing {candidate.FileName}.",
                cancellationToken);
            var importState = await ImportPdfAsync(
                new PdfImportRequest(candidate.Path, null, null, candidate.PageCount),
                cancellationToken);
            if (string.IsNullOrWhiteSpace(importState.LastError))
            {
                importedCount++;
                lastDocumentInstanceId = importState.CreatedDocumentInstanceId;
                lastPdfPath = candidate.Path;
            }
            else
            {
                failedCount++;
            }

            progress?.Invoke(
                index + 1,
                scan.Candidates.Count,
                string.IsNullOrWhiteSpace(importState.LastError)
                    ? $"已导入：{candidate.FileName}"
                    : $"导入失败：{candidate.FileName}",
                importState.LastError);
        }

        var state = importedCount > 0
            ? new FirstRunWorkflowState(
                FirstRunStep.MinerUConfig,
                failedCount == 0
                    ? $"已导入 {importedCount} 个 PDF 题录。配置 MinerU token 后，请从题录右键菜单运行 OCR。"
                    : $"已导入 {importedCount} 个 PDF 题录；{failedCount} 个文件未能导入。配置 MinerU token 后，请从题录右键菜单运行 OCR。",
                lastPdfPath, libraryId, null, null, lastDocumentInstanceId, null, false)
            : new FirstRunWorkflowState(
                FirstRunStep.Scan, "扫描到了 PDF 文件，但没有任何文件成功导入。", null,
                libraryId, null, null, null, "没有任何 PDF 文件被成功导入。", false);

        if (importedCount > 0)
        {
            await TryUpdateInitialRootScanAsync(
                operationId,
                scan.Candidates.Count,
                scan.Candidates.Count,
                $"Imported {importedCount} of {scan.TotalCount} PDF candidate(s).",
                cancellationToken);
            await TryCompleteInitialRootScanAsync(
                operationId,
                $"Imported {importedCount} of {scan.TotalCount} PDF candidate(s).",
                cancellationToken);
        }
        else
        {
            await TryFailInitialRootScanAsync(
                operationId,
                AppErrorCodes.InvalidState,
                "No discovered PDF files could be imported.",
                "Initial PDF import failed.",
                ["Review the PDF files and retry"],
                cancellationToken);
        }

        progress?.Invoke(
            scan.Candidates.Count,
            scan.Candidates.Count,
            importedCount > 0 ? "PDF 导入完成。" : "PDF 导入未完成。",
            $"成功 {importedCount} 个，失败 {failedCount} 个。");

        return new FirstRunImportResult(state, scan, importedCount, failedCount);
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

    private async Task TryUpdateInitialRootScanAsync(
        BlockingOperationId? operationId,
        int current,
        int total,
        string progressLabel,
        CancellationToken cancellationToken)
    {
        if (_blockingOperations is null || operationId is null)
            return;

        try
        {
            await _blockingOperations.UpdateProgressAsync(
                operationId.Value, current, total, progressLabel,
                cancellationToken: cancellationToken);
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

public sealed record FirstRunImportResult(
    FirstRunWorkflowState State,
    PdfScanResult ScanResult,
    int ImportedCount,
    int FailedCount) : IOperationOutcome
{
    public bool IsSuccess => string.IsNullOrWhiteSpace(State.LastError);
    public string? ErrorMessage => State.LastError;
}
