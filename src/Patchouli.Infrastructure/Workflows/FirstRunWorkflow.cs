using Patchouli.Core.Import;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Operations;
using Patchouli.Core.Results;
using Patchouli.Core.Files;
using Patchouli.Infrastructure.Files;

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
        Result<LibraryMetadata> result =
            await _libraryIdentityService.CreateLibraryAsync(displayName, cancellationToken);
        if (result.IsFailure)
        {
            return new FirstRunWorkflowState(FirstRunStep.Library, result.ErrorMessage!, null,
                null, null, null, null, result.ErrorMessage, false);
        }

        return new FirstRunWorkflowState(FirstRunStep.Scan, "资料库已创建。请选择要扫描的 PDF 文件夹。",
            null, result.Value.LibraryId.ToString(), null, null, null, null, false);
    }

    public async Task<FirstRunWorkflowState> ScanDirectoryAsync(
        SelectedFileSearchRoot selectedRoot, CancellationToken cancellationToken = default)
    {
        string normalizedRoot = Path.GetFullPath(selectedRoot.DisplayPath);
        BlockingOperationId? scanOperationId = await TryStartInitialRootScanAsync(normalizedRoot, cancellationToken);
        PdfScanResult result = await _pdfDiscoveryService.ScanDirectoryAsync(selectedRoot, cancellationToken);
        FirstRunWorkflowState? scanFailure = await MapIncompleteScanAsync(scanOperationId, result,
            cancellationToken);
        if (scanFailure is not null)
        {
            return scanFailure;
        }

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
        SelectedFileSearchRoot selectedRoot,
        string? libraryId,
        CancellationToken cancellationToken = default,
        Action<int?, int?, string, string?>? progress = null)
    {
        string normalizedRoot = Path.GetFullPath(selectedRoot.DisplayPath);
        progress?.Invoke(null, null, "正在扫描 PDF 目录。", $"扫描目录：{normalizedRoot}");
        BlockingOperationId? operationId = await TryStartInitialRootScanAsync(normalizedRoot, cancellationToken);
        try
        {
            PdfScanResult scan = await _pdfDiscoveryService.ScanDirectoryAsync(selectedRoot, cancellationToken);
            FirstRunWorkflowState? scanFailure = await MapIncompleteScanAsync(operationId, scan, cancellationToken);
            if (scanFailure is not null)
            {
                return new FirstRunImportResult(scanFailure with { CreatedLibraryId = libraryId }, scan, 0, 0);
            }

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

            int importedCount = 0;
            int failedCount = 0;
            string? lastDocumentInstanceId = null;
            string? lastPdfPath = null;
            IReadOnlyList<PdfCandidate> orderedCandidates = FileLocalityClassifier
                .OrderForImport(scan.Candidates, static c => c.Readiness, static c => c.FileName)
                .ToArray();
            progress?.Invoke(0, orderedCandidates.Count, $"已找到 {orderedCandidates.Count} 个 PDF，准备导入。", null);

            for (int index = 0; index < orderedCandidates.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PdfCandidate candidate = orderedCandidates[index];
                FileLocalityAssessment locality = _pdfDiscoveryService.Assess(candidate.Path);
                if (locality.Readiness == FileLocalityReadiness.CloudUnready)
                {
                    progress?.Invoke(index, orderedCandidates.Count, $"正在下载云端文件：{candidate.FileName}",
                        candidate.Path);
                    Result materialized =
                        await _pdfDiscoveryService.EnsureAvailableAsync(candidate.Path, cancellationToken);
                    locality = _pdfDiscoveryService.Assess(candidate.Path);
                    if (materialized.IsFailure)
                    {
                        failedCount++;
                        progress?.Invoke(index + 1, orderedCandidates.Count, $"云端文件下载失败：{candidate.FileName}",
                            materialized.ErrorMessage ?? locality.Reason);
                        continue;
                    }
                }

                string tier = locality.Readiness == FileLocalityReadiness.LocalReady ? "local" : "cloud";
                progress?.Invoke(index, orderedCandidates.Count, $"正在导入 ({tier})：{candidate.FileName}",
                    candidate.Path);
                await TryUpdateInitialRootScanAsync(
                    operationId,
                    index,
                    orderedCandidates.Count,
                    $"Importing {candidate.FileName} ({tier}).",
                    cancellationToken);
                FirstRunWorkflowState importState = await ImportPdfAsync(
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
                    orderedCandidates.Count,
                    string.IsNullOrWhiteSpace(importState.LastError)
                        ? $"已导入：{candidate.FileName}"
                        : $"导入失败：{candidate.FileName}",
                    importState.LastError);
            }

            int skippedPathCount = (scan.SkippedDirectories?.Count ?? 0) + (scan.SkippedFiles?.Count ?? 0);
            FirstRunWorkflowState state = importedCount > 0
                ? new FirstRunWorkflowState(
                    FirstRunStep.MinerUConfig,
                    scan.ScanStatus == FileSearchRootScanStatuses.Partial
                        ? $"目录扫描不完整（跳过 {skippedPathCount} 个路径），已导入发现的 {importedCount} 个 PDF 题录。配置 MinerU token 后，请从题录右键菜单运行 OCR。"
                        : failedCount == 0
                            ? $"已导入 {importedCount} 个 PDF 题录。配置 MinerU token 后，请从题录右键菜单运行 OCR。"
                            : $"已导入 {importedCount} 个 PDF 题录；{failedCount} 个文件未能导入。配置 MinerU token 后，请从题录右键菜单运行 OCR。",
                    lastPdfPath, libraryId, null, null, lastDocumentInstanceId, null, false)
                : new FirstRunWorkflowState(
                    FirstRunStep.Scan, "扫描到了 PDF 文件，但没有任何文件成功导入。", null,
                    libraryId, null, null, null, "没有任何 PDF 文件被成功导入。", false);

            if (importedCount > 0)
            {
                string completionDetail = scan.ScanStatus == FileSearchRootScanStatuses.Partial
                    ? $"Scan incomplete ({skippedPathCount} path(s) skipped); imported {importedCount} of " +
                      $"{scan.TotalCount} discovered PDF candidate(s)."
                    : $"Imported {importedCount} of {scan.TotalCount} PDF candidate(s).";
                await TryUpdateInitialRootScanAsync(
                    operationId,
                    scan.Candidates.Count,
                    scan.Candidates.Count,
                    completionDetail,
                    cancellationToken);
                await TryCompleteInitialRootScanAsync(
                    operationId,
                    completionDetail,
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (_blockingOperations is not null && operationId is not null)
            {
                await _blockingOperations.CancelAsync(
                    operationId.Value,
                    "Initial PDF root scan/import was cancelled.",
                    ["Retry the scan"],
                    CancellationToken.None);
            }

            throw;
        }
    }

    public async Task<FirstRunWorkflowState> ImportPdfAsync(
        PdfImportRequest request, CancellationToken cancellationToken = default)
    {
        PdfImportResult importResult = await _pdfImportWorkflow.ImportPdfAsync(request, cancellationToken);
        if (!importResult.Success)
        {
            return new FirstRunWorkflowState(FirstRunStep.Import, importResult.ErrorMessage ?? "导入失败。",
                request.PdfPath, null, null, null, null, importResult.ErrorMessage, false);
        }

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
            Result<BlockingOperation> started = await _blockingOperations.StartAsync(
                BlockingOperationTypes.InitialRootScan,
                BlockingOperationScopeTypes.FileSearchRoot,
                scanRoot,
                true,
                "Scanning initial PDF root.",
                nextActions: ["Choose a different PDF folder", "Add PDFs to the folder and retry"],
                cancellationToken: cancellationToken);
            return started.IsSuccess ? started.Value.OperationId : null;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.first-run-workflow", "complete-initial-root-scan"))
        {
            return null;
        }
    }

    private async Task<FirstRunWorkflowState?> MapIncompleteScanAsync(BlockingOperationId? operationId,
        PdfScanResult scan, CancellationToken cancellationToken)
    {
        if (scan.ScanStatus == FileSearchRootScanStatuses.Complete)
        {
            return null;
        }

        if (scan.ScanStatus == FileSearchRootScanStatuses.Cancelled)
        {
            if (_blockingOperations is not null && operationId is not null)
            {
                await _blockingOperations.CancelAsync(operationId.Value, "Initial PDF root scan was cancelled.",
                    ["Retry the scan"], CancellationToken.None);
            }

            return new FirstRunWorkflowState(FirstRunStep.Scan, "扫描已取消。", null, null, null, null, null,
                "扫描已取消。", false);
        }

        if (scan.ScanStatus == FileSearchRootScanStatuses.Partial && scan.Candidates.Count > 0)
        {
            // A partial scan (iCloud placeholders skipped, a directory timed out, ...) still
            // imports the discovered candidates; skipped paths are picked up by later rescans.
            return null;
        }

        string code = scan.ScanStatus == FileSearchRootScanStatuses.Partial
            ? "scan_partial"
            : scan.RootStatus switch
            {
                FileSearchRootStatuses.AccessDenied => "access_denied",
                FileSearchRootStatuses.AuthorizationRequired => "authorization_required",
                FileSearchRootStatuses.Offline => AppErrorCodes.NotFound,
                _ => "scan_failed"
            };
        string message = scan.ScanStatus == FileSearchRootScanStatuses.Partial
            ? "The selected folder could only be scanned partially."
            : "The selected folder could not be scanned.";
        await TryFailInitialRootScanAsync(operationId, code, message, message,
            ["Review inaccessible paths and retry", "Choose a different PDF folder"], CancellationToken.None);
        return new FirstRunWorkflowState(FirstRunStep.Scan,
            scan.ScanStatus == FileSearchRootScanStatuses.Partial ? "目录扫描不完整，未导入任何文件。" : "目录扫描失败。",
            null, null, null, null, null, message, false);
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
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.first-run-workflow", "update-initial-root-scan"))
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
        {
            return;
        }

        try
        {
            await _blockingOperations.UpdateProgressAsync(
                operationId.Value, current, total, progressLabel,
                cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.first-run-workflow", "fail-initial-root-scan"))
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
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.first-run-workflow", "fail-initial-root-scan"))
        {
            _ = exception;
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
    public bool IsCancelled => ScanResult.ScanStatus == FileSearchRootScanStatuses.Cancelled;
    public string? ErrorMessage => State.LastError;
}
