using LiteratureApp.Core.Import;
using LiteratureApp.Core.Library;
using LiteratureApp.Core.Results;
using LiteratureApp.Infrastructure.Ocr.MinerU;
using LiteratureApp.Infrastructure.Search;
using LiteratureApp.Ocr.MinerU;
using LiteratureApp.Search;

namespace LiteratureApp.Infrastructure.Workflows;

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

        return new FirstRunWorkflowState(FirstRunStep.Scan, "Library created. Select a PDF folder to scan.",
            null, result.Value.LibraryId.ToString(), null, null, null, null, false);
    }

    public async Task<FirstRunWorkflowState> ScanDirectoryAsync(
        string scanRoot, CancellationToken cancellationToken = default)
    {
        var result = await _pdfDiscoveryService.ScanDirectoryAsync(scanRoot, cancellationToken);
        if (result.Candidates.Count == 0)
            return new FirstRunWorkflowState(FirstRunStep.Scan,
                "No PDF files found in the specified directory.", null,
                null, null, null, null, "No PDF files found.", false);

        return new FirstRunWorkflowState(FirstRunStep.Import,
            $"Found {result.TotalCount} PDF candidate(s). Importing discovered files as library items.",
            null, null, null, null, null, null, false);
    }

    public async Task<FirstRunWorkflowState> ImportPdfAsync(
        PdfImportRequest request, CancellationToken cancellationToken = default)
    {
        var importResult = await _pdfImportWorkflow.ImportPdfAsync(request, cancellationToken);
        if (!importResult.Success)
            return new FirstRunWorkflowState(FirstRunStep.Import, importResult.ErrorMessage ?? "Import failed.",
                request.PdfPath, null, null, null, null, importResult.ErrorMessage, false);

        return new FirstRunWorkflowState(FirstRunStep.MinerUConfig,
            "PDF imported. Configure MinerU token for OCR extraction.",
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
                    downloadResult.ErrorMessage ?? "MinerU extraction failed.",
                    pdfPath, null, null, null, documentInstanceId, downloadResult.ErrorMessage, false);

            var importRequest = new MinerUImportRequest(
                downloadResult.Value.ZipPath, documentInstanceId, null);

            var importResult = await _minerUResultImporter.ImportResultZipAsync(importRequest, cancellationToken);
            if (importResult.IsFailure)
                return new FirstRunWorkflowState(FirstRunStep.Extract,
                    importResult.ErrorMessage ?? "Failed to import MinerU results.",
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
            "Enter a MinerU API token before running OCR extraction.",
            pdfPath,
            null,
            null,
            null,
            documentInstanceId,
            "MinerU API token is required before OCR extraction can start.",
            false);

    public async Task<FirstRunWorkflowState> RebuildSearchIndexAsync(
        string documentInstanceIdStr, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(documentInstanceIdStr, out var docGuid))
            return new FirstRunWorkflowState(FirstRunStep.Index, "Invalid document instance ID.",
                null, null, null, null, documentInstanceIdStr, "Invalid document instance ID.", false);

        var docId = new Core.Ids.DocumentInstanceId(docGuid);

        var rebuildUnits = await _searchUnitBuilder.RebuildForDocumentInstanceAsync(docId, cancellationToken);
        if (rebuildUnits.IsFailure)
            return new FirstRunWorkflowState(FirstRunStep.Index,
                rebuildUnits.ErrorMessage ?? "Failed to rebuild search units.",
                null, null, null, null, documentInstanceIdStr, rebuildUnits.ErrorMessage, false);

        var rebuildFts = await _searchIndexRebuilder.RebuildFtsForDocumentInstanceAsync(docId, cancellationToken);
        if (rebuildFts.IsFailure)
            return new FirstRunWorkflowState(FirstRunStep.Index,
                rebuildFts.ErrorMessage ?? "Failed to rebuild FTS index.",
                null, null, null, null, documentInstanceIdStr, rebuildFts.ErrorMessage, false);

        return new FirstRunWorkflowState(FirstRunStep.McpVerify,
            "Search index rebuilt. Verifying MCP search...",
            null, null, null, null, documentInstanceIdStr, null, false);
    }

    public async Task<FirstRunWorkflowState> VerifyMcpSearchAsync(
        string documentInstanceIdStr, string? searchTerm = null, CancellationToken cancellationToken = default)
    {
        var result = await _mcpVerificationService.VerifyAsync(documentInstanceIdStr, searchTerm, cancellationToken);

        if (result.IsFailure)
            return new FirstRunWorkflowState(FirstRunStep.McpVerify,
                result.ErrorMessage ?? "MCP verification failed.",
                null, null, null, null, documentInstanceIdStr, result.ErrorMessage, false);

        var verified = result.Value;
        return new FirstRunWorkflowState(FirstRunStep.Complete,
            verified.IsSearchable
                ? $"MCP verification passed. Found {verified.MatchedUnitCount} searchable unit(s)."
                : "MCP verification: document indexed but search returned no results.",
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
