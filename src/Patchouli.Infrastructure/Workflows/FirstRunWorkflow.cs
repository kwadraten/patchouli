using Patchouli.Core.Import;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Ocr.MinerU;
using Patchouli.Infrastructure.Search;
using Patchouli.Ocr.MinerU;
using Patchouli.Search;

namespace Patchouli.Infrastructure.Workflows;

public sealed class FirstRunWorkflow
{
    private readonly ILibraryIdentityService _libraryIdentityService;
    private readonly PdfDiscoveryService _pdfDiscoveryService;
    private readonly PdfImportWorkflow _pdfImportWorkflow;
    private readonly IMinerUResultImporter _minerUResultImporter;
    private readonly ISearchUnitBuilder _searchUnitBuilder;
    private readonly ISearchIndexRebuilder _searchIndexRebuilder;
    private readonly McpVerificationService _mcpVerificationService;
    private readonly Func<MinerUConfiguration, IMinerUClient> _minerUClientFactory;

    public FirstRunWorkflow(
        ILibraryIdentityService libraryIdentityService,
        PdfDiscoveryService pdfDiscoveryService,
        PdfImportWorkflow pdfImportWorkflow,
        IMinerUResultImporter minerUResultImporter,
        ISearchUnitBuilder searchUnitBuilder,
        ISearchIndexRebuilder searchIndexRebuilder,
        McpVerificationService mcpVerificationService,
        Func<MinerUConfiguration, IMinerUClient>? minerUClientFactory = null)
    {
        _libraryIdentityService = libraryIdentityService;
        _pdfDiscoveryService = pdfDiscoveryService;
        _pdfImportWorkflow = pdfImportWorkflow;
        _minerUResultImporter = minerUResultImporter;
        _searchUnitBuilder = searchUnitBuilder;
        _searchIndexRebuilder = searchIndexRebuilder;
        _mcpVerificationService = mcpVerificationService;
        _minerUClientFactory = minerUClientFactory ?? CreateMinerUClient;
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
            "PDF 已导入。请配置 MinerU token 以进行 OCR 识别。",
            request.PdfPath, null, importResult.CreatedItemId,
            importResult.CreatedFileAssetId, importResult.CreatedDocumentInstanceId,
            null, false);
    }

    public async Task<FirstRunWorkflowState> RunMinerUExtractionAsync(
        MinerUConfiguration config,
        string pdfPath,
        string cacheDirectory,
        string documentInstanceId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(config.Token))
            return RequireMinerUTokenState(pdfPath, documentInstanceId);

        var client = _minerUClientFactory(config);
        try
        {
            var downloader = new MinerUResultDownloader(client);

            var downloadResult = await downloader.UploadAndExtractAsync(
                pdfPath, cacheDirectory, cancellationToken);

            if (downloadResult.IsFailure)
                return new FirstRunWorkflowState(FirstRunStep.Extract,
                    downloadResult.ErrorMessage ?? "MinerU 提取失败。",
                    pdfPath, null, null, null, documentInstanceId, downloadResult.ErrorMessage, false);

            var importRequest = new MinerUImportRequest(
                downloadResult.Value.ZipPath, documentInstanceId, null);

            var importResult = await _minerUResultImporter.ImportResultZipAsync(importRequest, cancellationToken);
            if (importResult.IsFailure)
                return new FirstRunWorkflowState(FirstRunStep.Extract,
                    importResult.ErrorMessage ?? "无法导入 MinerU 结果。",
                    pdfPath, null, null, null, documentInstanceId, importResult.ErrorMessage, false);
        }
        finally
        {
            if (client is IDisposable disposable)
                disposable.Dispose();
        }

        var indexState = await RebuildSearchIndexAsync(documentInstanceId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(indexState.LastError))
            return indexState;

        return await VerifyMcpSearchAsync(documentInstanceId, cancellationToken: cancellationToken);
    }

    private static FirstRunWorkflowState RequireMinerUTokenState(string? pdfPath, string? documentInstanceId) =>
        new(
            FirstRunStep.MinerUConfig,
            "在运行 OCR 提取之前，请输入 MinerU API token。",
            pdfPath,
            null,
            null,
            null,
            documentInstanceId,
            "开始 OCR 提取之前必须提供 MinerU API token。",
            false);

    public async Task<FirstRunWorkflowState> RebuildSearchIndexAsync(
        string documentInstanceIdStr, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(documentInstanceIdStr, out var docGuid))
            return new FirstRunWorkflowState(FirstRunStep.Index, "无效的文档实例 ID。",
                null, null, null, null, documentInstanceIdStr, "无效的文档实例 ID。", false);

        var docId = new Core.Ids.DocumentInstanceId(docGuid);

        var rebuildUnits = await _searchUnitBuilder.RebuildForDocumentInstanceAsync(docId, cancellationToken);
        if (rebuildUnits.IsFailure)
            return new FirstRunWorkflowState(FirstRunStep.Index,
                rebuildUnits.ErrorMessage ?? "重建搜索单元失败。",
                null, null, null, null, documentInstanceIdStr, rebuildUnits.ErrorMessage, false);

        var rebuildFts = await _searchIndexRebuilder.RebuildFtsForDocumentInstanceAsync(docId, cancellationToken);
        if (rebuildFts.IsFailure)
            return new FirstRunWorkflowState(FirstRunStep.Index,
                rebuildFts.ErrorMessage ?? "重建全文搜索索引失败。",
                null, null, null, null, documentInstanceIdStr, rebuildFts.ErrorMessage, false);

        return new FirstRunWorkflowState(FirstRunStep.McpVerify,
            "搜索索引已重建。正在验证 MCP 搜索...",
            null, null, null, null, documentInstanceIdStr, null, false);
    }

    public async Task<FirstRunWorkflowState> VerifyMcpSearchAsync(
        string documentInstanceIdStr, string? searchTerm = null, CancellationToken cancellationToken = default)
    {
        var result = await _mcpVerificationService.VerifyAsync(documentInstanceIdStr, searchTerm, cancellationToken);

        if (result.IsFailure)
            return new FirstRunWorkflowState(FirstRunStep.McpVerify,
                result.ErrorMessage ?? "MCP 验证失败。",
                null, null, null, null, documentInstanceIdStr, result.ErrorMessage, false);

        var verified = result.Value;
        return new FirstRunWorkflowState(FirstRunStep.Complete,
            verified.IsSearchable
                ? $"MCP 验证通过。找到 {verified.MatchedUnitCount} 个可搜索单元。"
                : "MCP 验证：文档已建立索引，但搜索未返回任何结果。",
            null, null, null, null, documentInstanceIdStr, null, true);
    }

    private static IMinerUClient CreateMinerUClient(MinerUConfiguration config)
    {
        var options = new MinerUOptions
        {
            Token = config.Token,
            BaseUrl = config.BaseUrl ?? "https://mineru.net",
            ModelVersion = config.ModelVersion ?? "vlm",
            IsOcr = config.IsOcr,
            EnableTable = config.EnableTable,
            EnableFormula = config.EnableFormula
        };

        return new MinerUClient(options);
    }
}
