using Patchouli.Core.Documents;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Core.Search;
using Patchouli.Extensions.Contracts;
using Patchouli.Ocr;

namespace Patchouli.Host.Extensions;

public sealed record ExtensionOcrStage(
    string ExtensionId,
    string ExtensionFingerprint,
    ExtensionPageIdentity Page,
    OcrExtensionCandidate ExtensionCandidate,
    OcrDocumentTreeCandidate TreeCandidate,
    OcrDocumentTreeImportResult Working);

public sealed record ExtensionRankedSearchMatch(
    SearchMatchedUnit Evidence,
    double Score,
    string? Explanation);

public sealed record ExtensionRetrievalResult(
    string ExtensionId,
    string ExtensionFingerprint,
    SearchResultPage Search,
    IReadOnlyList<ExtensionRankedSearchMatch> Matches);

/// <summary>Explicit host entrypoints for OCR review staging and search reranking.</summary>
public sealed class ExtensionDomainService
{
    private const int MaximumCandidateLimit = 500;
    private const int MaximumResultLimit = 100;
    private static readonly NormalizedBBox FullPage = new(0, 0, 1, 1);

    private readonly TranslationExtensionCatalog _extensions;
    private readonly IPageService _pages;
    private readonly IPageRenderService _renders;
    private readonly IDocumentTreeService _trees;
    private readonly IOcrDocumentTreeImporter _ocrImporter;
    private readonly ISearchService _search;

    public ExtensionDomainService(TranslationExtensionCatalog extensions, IPageService pages,
        IPageRenderService renders, IDocumentTreeService trees, IOcrDocumentTreeImporter ocrImporter,
        ISearchService search)
    {
        _extensions = extensions;
        _pages = pages;
        _renders = renders;
        _trees = trees;
        _ocrImporter = ocrImporter;
        _search = search;
    }

    public async Task<Result<ExtensionRetrievalResult>> RankSearchAsync(string extensionId, SearchRequest request,
        int candidateLimit = 100, int resultLimit = 20, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (candidateLimit is < 1 or > MaximumCandidateLimit || resultLimit is < 1 or > MaximumResultLimit ||
            resultLimit > candidateLimit)
        {
            return Failure<ExtensionRetrievalResult>(AppErrorCodes.ValidationFailed,
                $"Candidate limit must be 1..{MaximumCandidateLimit}; result limit must be 1..{MaximumResultLimit} and no greater than the candidate limit.");
        }

        try
        {
            await using ExtensionDomainRun run = await AcquireAsync(extensionId, ExtensionKind.Retrieval,
                cancellationToken).ConfigureAwait(false);
            Result<SearchResultPage> searched = await _search.SearchLibraryAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (searched.IsFailure)
            {
                return Failure<ExtensionRetrievalResult>(searched.ErrorCode!, searched.ErrorMessage!);
            }

            SearchMatchedUnit[] pool = searched.Value.Results.SelectMany(page => page.MatchedUnits)
                .DistinctBy(unit => unit.UnitId).Take(candidateLimit).ToArray();
            Result<RetrievalExtensionResult> ranked = await run.RankAsync(new RetrievalExtensionContext(
                    request.Query, run.Config, Array.AsReadOnly(pool), resultLimit), cancellationToken)
                .ConfigureAwait(false);
            if (ranked.IsFailure)
            {
                return Failure<ExtensionRetrievalResult>(ranked.ErrorCode!, ranked.ErrorMessage!);
            }

            Dictionary<SearchUnitId, SearchMatchedUnit> evidenceById = pool.ToDictionary(unit => unit.UnitId);
            ExtensionRankedSearchMatch[] matches = ranked.Value.Matches.Select(match =>
                new ExtensionRankedSearchMatch(evidenceById[match.UnitId], match.Score, match.Explanation)).ToArray();
            return Result<ExtensionRetrievalResult>.Success(new ExtensionRetrievalResult(extensionId,
                run.Fingerprint, searched.Value, Array.AsReadOnly(matches)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ExtensionDomainUnavailableException exception)
        {
            return Failure<ExtensionRetrievalResult>(exception.Code, exception.Message);
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "host.extensions.retrieval-rank"))
        {
            return Failure<ExtensionRetrievalResult>(AppErrorCodes.InvalidState,
                $"Retrieval extension ranking failed: {exception.Message}");
        }
    }

    public async Task<Result<ExtensionOcrStage>> RecognizeAndStageAsync(string extensionId,
        DocumentInstanceId documentId, PageId pageId, NormalizedBBox? region = null, int dpi = 200,
        CancellationToken cancellationToken = default)
    {
        if (dpi is < 36 or > 1200)
        {
            return Failure<ExtensionOcrStage>(AppErrorCodes.ValidationFailed, "OCR DPI must be between 36 and 1200.");
        }

        NormalizedBBox inputRegion = region ?? FullPage;
        Result regionValidation = inputRegion.Validate();
        if (regionValidation.IsFailure)
        {
            return Failure<ExtensionOcrStage>(regionValidation.ErrorCode!, regionValidation.ErrorMessage!);
        }

        try
        {
            await using ExtensionDomainRun run = await AcquireAsync(extensionId, ExtensionKind.Ocr,
                cancellationToken).ConfigureAwait(false);
            Result<Page> pageResult = await _pages.GetPageAsync(pageId, cancellationToken).ConfigureAwait(false);
            if (pageResult.IsFailure)
            {
                return Failure<ExtensionOcrStage>(pageResult.ErrorCode!, pageResult.ErrorMessage!);
            }

            Page page = pageResult.Value;
            if (page.DocumentInstanceId != documentId)
            {
                return Failure<ExtensionOcrStage>(AppErrorCodes.ValidationFailed,
                    "The page does not belong to the requested document instance.");
            }

            Result<DocumentTreeRevision> revisionResult = await _trees.GetCurrentRevisionAsync(documentId, pageId,
                cancellationToken).ConfigureAwait(false);
            if (revisionResult.IsFailure)
            {
                return Failure<ExtensionOcrStage>(revisionResult.ErrorCode!, revisionResult.ErrorMessage!);
            }

            Result<string> imagePathResult = await _renders.RenderRegionPngAsync(documentId, pageId, inputRegion,
                dpi, cancellationToken).ConfigureAwait(false);
            if (imagePathResult.IsFailure)
            {
                return Failure<ExtensionOcrStage>(imagePathResult.ErrorCode!, imagePathResult.ErrorMessage!);
            }

            byte[] imageBytes = await File.ReadAllBytesAsync(imagePathResult.Value, cancellationToken)
                .ConfigureAwait(false);
            ExtensionPageIdentity identity = new(documentId, pageId, revisionResult.Value.TreeRevisionId,
                page.PageIndex);
            ExtensionOcrInput input = new(new OcrInputId(Guid.NewGuid().ToString("N")), inputRegion,
                "image/png", imageBytes);

            Result<OcrExtensionCandidate> recognized = await run.RecognizeAsync(
                new OcrExtensionContext(identity, [input], run.Config), cancellationToken).ConfigureAwait(false);
            if (recognized.IsFailure)
            {
                return Failure<ExtensionOcrStage>(recognized.ErrorCode!, recognized.ErrorMessage!);
            }

            OcrDocumentTreeCandidate treeCandidate = new(
            [
                new OcrPageCandidate(pageId, page.PageIndex,
                    recognized.Value.Boxes.Select(box =>
                    {
                        DocumentBoxSeed seed = box.Candidate;
                        return new OcrBoxCandidate(seed.BoxType, seed.SubType, seed.SourceOrder,
                            seed.Payload!, seed.BBox, seed.HeadingLevel, seed.Confidence, seed.Suppressed,
                            seed.ParentBoxId, seed.BoxId, seed.ContinuesFromBoxId, seed.CodeLanguage,
                            ContinuationState: seed.ContinuationState);
                    }).ToArray())
            ], []);
            Result treeValidation = treeCandidate.Validate();
            if (treeValidation.IsFailure)
            {
                return Failure<ExtensionOcrStage>(treeValidation.ErrorCode!, treeValidation.ErrorMessage!);
            }

            OcrDocumentTreeImportRequest importRequest = new(documentId, treeCandidate,
                $"extension:{extensionId}",
                new Dictionary<PageId, DocumentTreeRevisionId> { [pageId] = identity.RevisionId });
            Result<OcrDocumentTreeImportResult> working = await _ocrImporter.BeginWorkingAsync(importRequest,
                cancellationToken).ConfigureAwait(false);
            return working.IsFailure
                ? Failure<ExtensionOcrStage>(working.ErrorCode!, working.ErrorMessage!)
                : Result<ExtensionOcrStage>.Success(new ExtensionOcrStage(extensionId, run.Fingerprint, identity,
                    recognized.Value, treeCandidate, working.Value));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ExtensionDomainUnavailableException exception)
        {
            return Failure<ExtensionOcrStage>(exception.Code, exception.Message);
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "host.extensions.ocr-stage"))
        {
            return Failure<ExtensionOcrStage>(AppErrorCodes.InvalidState,
                $"OCR extension staging failed: {exception.Message}");
        }
    }

    private async Task<ExtensionDomainRun> AcquireAsync(string extensionId, ExtensionKind expectedKind,
        CancellationToken cancellationToken)
    {
        await _extensions.InitializeAsync(cancellationToken).ConfigureAwait(false);
        IExtensionLease? lease = await _extensions.Manager.AcquireAsync(extensionId, cancellationToken)
            .ConfigureAwait(false);
        if (lease is null)
        {
            throw new ExtensionDomainUnavailableException(AppErrorCodes.NotFound,
                "The requested extension is not active.");
        }

        try
        {
            if (lease.Descriptor.Kind != expectedKind)
            {
                throw new ExtensionDomainUnavailableException(AppErrorCodes.UnsupportedOperation,
                    $"The selected extension is {lease.Descriptor.Kind}; {expectedKind} is required.");
            }

            ExtensionConfigSnapshot config = ExtensionConfiguration.Resolve(lease.ConfigFields, lease.Config);
            IExtensionResources capabilities = (_extensions.Resources is HostExtensionResources host
                ? host.ForDomain(expectedKind.ToString().ToLowerInvariant())
                : _extensions.Resources).ForRun(config);
            return new ExtensionDomainRun(lease, capabilities, config);
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static Result<T> Failure<T>(string code, string message)
    {
        return Result<T>.Failure(code, message);
    }

    private sealed class ExtensionDomainUnavailableException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
}
