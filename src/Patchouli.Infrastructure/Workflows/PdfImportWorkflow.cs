using Patchouli.Core.Bibliography;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Import;
using Patchouli.Core.Layout;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.Import;

namespace Patchouli.Infrastructure.Workflows;

public sealed class PdfImportWorkflow
{
    internal const double DefaultMaxFailedPageRatio = 0.2;

    private readonly ImportBatchWriter _batchWriter;
    private readonly IPdfMetadataReader _pdfMetadataReader;
    private readonly IClock _clock;
    private readonly ILibraryIdentityService _libraryIdentityService;
    private readonly IFileFingerprintService _fingerprintService;
    private readonly IItemTypeInferenceService? _itemTypeInferenceService;
    private readonly IHostActivityTracker? _activityTracker;
    private readonly CancellationToken _hostLifetime;
    private readonly IPdfPageInfoReader? _pageInfoReader;
    private readonly double _maxFailedPageRatio;

    public PdfImportWorkflow(
        ImportBatchWriter batchWriter,
        IPdfMetadataReader pdfMetadataReader,
        IClock clock,
        ILibraryIdentityService libraryIdentityService,
        IFileFingerprintService? fingerprintService = null,
        IItemTypeInferenceService? itemTypeInferenceService = null,
        IHostActivityTracker? activityTracker = null,
        CancellationToken hostLifetime = default,
        IPdfPageInfoReader? pageInfoReader = null,
        double? maxFailedPageRatio = null)
    {
        _batchWriter = batchWriter;
        _pdfMetadataReader = pdfMetadataReader;
        _clock = clock;
        _libraryIdentityService = libraryIdentityService;
        _fingerprintService = fingerprintService ?? new FileFingerprintService();
        _itemTypeInferenceService = itemTypeInferenceService;
        _activityTracker = activityTracker;
        _hostLifetime = hostLifetime;
        _pageInfoReader = pageInfoReader;
        _maxFailedPageRatio = maxFailedPageRatio ?? DefaultMaxFailedPageRatio;
    }

    public async Task<PdfImportResult> ImportPdfAsync(
        PdfImportRequest request,
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _hostLifetime);
        cancellationToken = linkedCancellation.Token;
        if (!File.Exists(request.PdfPath))
        {
            return new PdfImportResult(false, "PDF file was not found at the specified path.", null, null, null, null);
        }

        int? pageCount = request.PageCount ??
                         await _pdfMetadataReader.GetPageCountAsync(request.PdfPath, cancellationToken);

        if (pageCount is null or <= 0)
        {
            return new PdfImportResult(false, "Could not determine page count for this PDF.", null, null, null, null);
        }

        using IActivityScope? activity = _activityTracker?.BeginScope(
            "导入 PDF",
            HostActivityKind.Import,
            Path.GetFileName(request.PdfPath));

        Result<LibraryMetadata> libraryResult =
            await _libraryIdentityService.GetCurrentLibraryAsync(cancellationToken);
        if (libraryResult.IsFailure)
        {
            return new PdfImportResult(false, libraryResult.ErrorMessage, null, null, null, null);
        }

        string normalizedPath = Path.GetFullPath(request.PdfPath);
        Result<FileFingerprint> fingerprintResult =
            await _fingerprintService.GetFileMetadataAsync(normalizedPath, cancellationToken);
        if (fingerprintResult.IsFailure)
        {
            return new PdfImportResult(false, fingerprintResult.ErrorMessage, null, null, null, null);
        }

        IReadOnlyList<PdfPageInfoResult>? pageInfoResults = _pageInfoReader is null
            ? null
            : await _pageInfoReader.GetPageInfosAsync(normalizedPath, cancellationToken);

        DateTimeOffset now = _clock.UtcNow.ToUniversalTime();
        FileFingerprint fingerprint = fingerprintResult.Value;
        DocumentInstanceId documentInstanceId = DocumentInstanceId.New();
        List<PdfImportPageFailure> failures = [];
        List<PdfImportPagePlaceholder> placeholders = [];
        List<Page> pages = new(pageCount.Value);
        for (int pageIndex = 0; pageIndex < pageCount.Value; pageIndex++)
        {
            PdfPageInfoResult? infoResult = pageInfoResults is { Count: > 0 } && pageIndex < pageInfoResults.Count
                ? pageInfoResults[pageIndex]
                : null;
            if (infoResult is { Success: true, Info: { } info })
            {
                pages.Add(CreatePage(documentInstanceId, pageIndex, info.Width, info.Height, info.Rotation, now));
                continue;
            }

            pages.Add(CreatePage(documentInstanceId, pageIndex, null, null, 0, now));
            if (infoResult is { Success: false })
            {
                string reason = string.IsNullOrWhiteSpace(infoResult.ErrorMessage)
                    ? "Page info could not be read."
                    : infoResult.ErrorMessage;
                failures.Add(new PdfImportPageFailure(pageIndex, reason));
                placeholders.Add(new PdfImportPagePlaceholder(
                    pageIndex, DocumentBoxDiagnosticCodes.ImportPagePlaceholder, reason));
            }
        }

        int failedCount = failures.Count;
        if (failedCount > 0 && (double)failedCount / pageCount.Value > _maxFailedPageRatio)
        {
            double ratio = (double)failedCount / pageCount.Value;
            return new PdfImportResult(
                false,
                $"Import failed: {failedCount} of {pageCount.Value} pages failed " +
                $"(failure ratio {ratio:0.###} exceeds the maximum failed page ratio {_maxFailedPageRatio:0.###}).",
                null, null, null, null,
                pageCount.Value, failedCount, failures);
        }

        FileAsset fileAsset = new(
            FileAssetService.CreateFileAssetId(fingerprint.FullBlake3),
            libraryResult.Value.LibraryId,
            normalizedPath,
            fingerprint.FileName,
            fingerprint.SizeBytes,
            fingerprint.MtimeUtc,
            fingerprint.QuickHash,
            fingerprint.FullBlake3,
            null,
            null,
            FileAssetStatus.Available,
            now,
            now);

        string title = !string.IsNullOrWhiteSpace(request.Title)
            ? request.Title.Trim()
            : Path.GetFileNameWithoutExtension(request.PdfPath);

        string? creatorsJson = !string.IsNullOrWhiteSpace(request.Authors)
            ? $@"[{{""name"":""{request.Authors.Trim()}""}}]"
            : null;

        (ItemMetadata item, IReadOnlyList<ItemCreatorInput> creators, IReadOnlyList<ItemDateInput> dates) =
            ItemService.CreateItemMetadata(
                libraryResult.Value.LibraryId,
                new CreateItemRequest("general", title, CreatorsJson: creatorsJson),
                now);

        DocumentInstance documentInstance = new(
            documentInstanceId,
            item.ItemId,
            fileAsset.FileAssetId,
            title,
            DocumentInstanceType.PrimaryScan,
            true,
            DocumentInstanceStatus.Active,
            now,
            now);

        PdfImportBatch batch = new(
            now,
            fileAsset,
            normalizedPath,
            item,
            creators,
            dates,
            documentInstance,
            pages,
            placeholders);

        Result commitResult = await _batchWriter.CommitAsync(batch, cancellationToken);
        if (commitResult.IsFailure)
        {
            return new PdfImportResult(
                false, commitResult.ErrorMessage, null, null, null, null,
                pageCount.Value, failedCount, failures);
        }

        if (_itemTypeInferenceService is not null)
        {
            (string SuggestedType, double Confidence, string EvidenceSummary)? inferredType =
                InferTypeFromFileName(request.PdfPath);
            if (inferredType is not null)
            {
                await _itemTypeInferenceService.SuggestAsync(
                    item.ItemId,
                    inferredType.Value.SuggestedType,
                    inferredType.Value.Confidence,
                    ItemTypeInferenceSources.FileNameHeuristic,
                    inferredType.Value.EvidenceSummary,
                    cancellationToken);
            }
        }

        return new PdfImportResult(
            true, null,
            failedCount > 0 ? "imported_with_page_failures" : "imported",
            item.ItemId.ToString(),
            fileAsset.FileAssetId.ToString(),
            documentInstance.DocumentInstanceId.ToString(),
            pageCount.Value, failedCount, failures);
    }

    private static Page CreatePage(
        DocumentInstanceId documentInstanceId,
        int pageIndex,
        double? width,
        double? height,
        int rotation,
        DateTimeOffset now)
    {
        return new Page(
            PageId.New(),
            documentInstanceId,
            pageIndex,
            $"Page {pageIndex + 1}",
            width,
            height,
            rotation,
            "normalized",
            null,
            null,
            "import",
            null,
            now,
            now);
    }

    private static (string SuggestedType, double Confidence, string EvidenceSummary)? InferTypeFromFileName(
        string pdfPath)
    {
        string fileName = Path.GetFileNameWithoutExtension(pdfPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        string normalized = fileName.Trim().ToLowerInvariant();
        if (normalized.Contains("thesis", StringComparison.Ordinal))
        {
            return ("thesis", 0.92, "Filename contains 'thesis'.");
        }

        if (normalized.Contains("patent", StringComparison.Ordinal))
        {
            return ("patent", 0.92, "Filename contains 'patent'.");
        }

        if (normalized.Contains("proceeding", StringComparison.Ordinal)
            || normalized.Contains("conference", StringComparison.Ordinal))
        {
            return ("paper-conference", 0.8, "Filename suggests conference proceedings.");
        }

        return null;
    }
}
