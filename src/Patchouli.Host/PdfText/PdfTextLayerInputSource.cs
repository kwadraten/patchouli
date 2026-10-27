using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Rendering;
using Patchouli.Ocr;

namespace Patchouli.Host.PdfText;

/// <summary>Stable failure codes for the text-layer input path.</summary>
public static class PdfTextLayerErrorCodes
{
    public const string SourceUnavailable = "source_unavailable";
    public const string SourceChanged = "source_changed";
    public const string UnsupportedFile = "unsupported_file";
    public const string NativeExtractionFailed = "native_text_extraction_failed";
}

/// <summary>
/// What the runtime needs to freeze into a <see cref="PdfTextLayerContext"/> for one page.
/// Identity fields come from the caller's frozen run state; the input source verifies them
/// against persisted page/file facts instead of trusting the request.
/// </summary>
public sealed record PdfTextLayerContextRequest(
    DocumentInstanceId DocumentId,
    PageId PageId,
    DocumentTreeRevisionId RevisionId,
    int PageIndex,
    ExtensionConfigSnapshot Config,
    NormalizedBBox? Region = null,
    bool IncludePageImage = false,
    bool IncludeLayoutDetection = false,
    IProgress<ExtensionProgress>? Progress = null);

/// <summary>
/// Builds frozen <see cref="PdfTextLayerContext"/> inputs for PDF text-layer providers. The
/// source resolves the document's approved file asset through the existing file-resolution,
/// materialization and source-validation services, verifies page/document identity, rotation
/// and the coordinate baseline, and runs the native extractor — with no page rendering on
/// native-only paths. Lazy delegates for page images and layout detection are only attached
/// when the caller asks for them, so a pure-text page can never silently consume pixels.
/// </summary>
public sealed class PdfTextLayerInputSource
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IFileResolutionService _fileResolution;
    private readonly IFileMaterializationService _fileMaterialization;
    private readonly ISourceValidationService _sourceValidation;
    private readonly IPdfTextExtractor _textExtractor;
    private readonly IPageRenderService? _pageRenders;
    private readonly IDocumentLayoutDetector? _layoutDetector;

    public PdfTextLayerInputSource(
        SqliteConnectionFactory connectionFactory,
        IFileResolutionService fileResolution,
        IFileMaterializationService fileMaterialization,
        ISourceValidationService sourceValidation,
        IPdfTextExtractor textExtractor,
        IPageRenderService? pageRenders = null,
        IDocumentLayoutDetector? layoutDetector = null)
    {
        _connectionFactory = connectionFactory;
        _fileResolution = fileResolution;
        _fileMaterialization = fileMaterialization;
        _sourceValidation = sourceValidation;
        _textExtractor = textExtractor;
        _pageRenders = pageRenders;
        _layoutDetector = layoutDetector;
    }

    public async Task<Result<PdfTextLayerContext>> CreateContextAsync(PdfTextLayerContextRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using SqliteConnection connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        PageRow? page = await connection.QuerySingleOrDefaultAsync<PageRow>(
            """
            select page_id as PageId, document_instance_id as DocumentInstanceId,
                   page_index as PageIndex, rotation as Rotation, coordinate_basis as CoordinateBasis,
                   source_file_hash as SourceFileHash,
                   width as Width, height as Height,
                   basis_width as BasisWidth, basis_height as BasisHeight,
                   renderer_basis_version as RendererBasisVersion
            from pages where page_id=@Id;
            """, new { Id = request.PageId.ToString() });
        if (page is null)
        {
            return Result<PdfTextLayerContext>.Failure(AppErrorCodes.NotFound, "Page was not found.");
        }

        if (page.DocumentInstanceId != request.DocumentId.ToString())
        {
            return Result<PdfTextLayerContext>.Failure(AppErrorCodes.ValidationFailed,
                "Page does not belong to the document instance.");
        }

        if (page.PageIndex != request.PageIndex)
        {
            return Result<PdfTextLayerContext>.Failure(AppErrorCodes.ValidationFailed,
                "Persisted page index does not match the requested page index.");
        }

        if (!string.Equals(page.CoordinateBasis, CoordinateBasis.NormalizedPage, StringComparison.Ordinal))
        {
            return Result<PdfTextLayerContext>.Failure(AppErrorCodes.ValidationFailed,
                $"The page's coordinate basis '{page.CoordinateBasis}' is not normalized_page.");
        }

        string? assetId = await connection.ExecuteScalarAsync<string?>(
            "select file_asset_id from document_instances where document_instance_id=@Id;",
            new { Id = request.DocumentId.ToString() });
        if (assetId is null)
        {
            return Result<PdfTextLayerContext>.Failure(AppErrorCodes.NotFound,
                "Document instance has no source file asset.");
        }

        FileAssetId fileAssetId = FileAssetId.Parse(assetId);
        AssetRow? asset = await connection.QuerySingleOrDefaultAsync<AssetRow>(
            """
            select status as Status, original_path as OriginalPath, size_bytes as SizeBytes,
                   mtime_utc as MtimeUtc, quick_hash as QuickHash, full_blake3 as FullBlake3
            from file_assets where file_asset_id=@Id;
            """, new { Id = assetId });
        if (asset is null)
        {
            return Result<PdfTextLayerContext>.Failure(AppErrorCodes.NotFound,
                "File asset was not found.");
        }

        if (asset.Status == FileAssetStatus.Conflict)
        {
            return Result<PdfTextLayerContext>.Failure(AppErrorCodes.Conflict,
                "Source file conflict requires user confirmation before parsing the text layer.");
        }

        Result<FileResolutionResult> resolution = await _fileResolution.ResolveFileAsync(fileAssetId,
            ResolveFilePurpose.RunOcr, cancellationToken);
        if (resolution.IsFailure)
        {
            return Result<PdfTextLayerContext>.Failure(resolution.ErrorCode!, resolution.ErrorMessage!);
        }

        if (resolution.Value.Status == FileAssetStatus.Changed)
        {
            return Result<PdfTextLayerContext>.Failure(PdfTextLayerErrorCodes.SourceChanged,
                resolution.Value.Warning ??
                "Source file changed; existing coordinate bases may be bbox_basis_stale.");
        }

        if (resolution.Value.Status != FileAssetStatus.Available ||
            string.IsNullOrWhiteSpace(resolution.Value.ResolvedPath))
        {
            return Result<PdfTextLayerContext>.Failure(PdfTextLayerErrorCodes.SourceUnavailable,
                resolution.Value.Warning ?? "Source file is unavailable for text-layer parsing.");
        }

        string resolvedPath = resolution.Value.ResolvedPath!;
        if (!string.Equals(Path.GetExtension(resolvedPath), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return Result<PdfTextLayerContext>.Failure(PdfTextLayerErrorCodes.UnsupportedFile,
                "PDF text-layer parsing supports PDF file assets only.");
        }

        Result materialized = await _fileMaterialization.EnsureAvailableAsync(resolvedPath, cancellationToken);
        if (materialized.IsFailure)
        {
            return Result<PdfTextLayerContext>.Failure(materialized.ErrorCode!,
                materialized.ErrorMessage ?? "Source file is not available locally.");
        }

        SourceValidationRequest validationRequest = new(fileAssetId, resolvedPath, asset.OriginalPath,
            asset.SizeBytes, ParseMtime(asset.MtimeUtc), asset.QuickHash, asset.FullBlake3, asset.Status,
            "pdf-text-layer");
        SourceValidationResult validation = await _sourceValidation.EnsureValidatedAsync(validationRequest,
            cancellationToken);
        if (validation.Status != SourceValidationStatus.Current)
        {
            return Result<PdfTextLayerContext>.Failure(
                validation.Status == SourceValidationStatus.Unavailable
                    ? PdfTextLayerErrorCodes.SourceUnavailable
                    : PdfTextLayerErrorCodes.SourceChanged,
                validation.Warning ?? $"Source basis is not current: {validation.Status}.");
        }

        PdfiumTextPage extracted;
        try
        {
            extracted = await _textExtractor.ExtractTextPageAsync(resolvedPath, page.PageIndex,
                cancellationToken);
        }
        catch (PdfiumDocumentException exception)
        {
            return Result<PdfTextLayerContext>.Failure(PdfTextLayerErrorCodes.NativeExtractionFailed,
                $"Native text extraction failed: {exception.Message}");
        }

        List<PdfTextLayerDiagnostic> extractionDiagnostics = [];
        PdfPageBox mediaBox = extracted.MediaBox;
        PdfPageBox effectiveBox = extracted.EffectiveBox;
        double boundWidth = effectiveBox.Width;
        double boundHeight = effectiveBox.Height;
        if (boundWidth <= 0 || boundHeight <= 0)
        {
            return Result<PdfTextLayerContext>.Failure(PdfTextLayerErrorCodes.NativeExtractionFailed,
                $"The page's effective display box is degenerate ({boundWidth:0.##}×{boundHeight:0.##}pt).");
        }

        // The effective display frame — the crop box intersected with the media box, rotation
        // applied — is what the renderer rasterizes and what every reader shows. A page that
        // declares a crop box inside a larger media box is legal PDF and looks perfectly
        // normal; the host's normalized-page baseline therefore describes the effective
        // frame, never the raw media box.
        double displayWidth = extracted.RotationDegrees is 90 or 270 ? boundHeight : boundWidth;
        double displayHeight = extracted.RotationDegrees is 90 or 270 ? boundWidth : boundHeight;
        bool cropClipsMedia = Math.Abs(effectiveBox.Left - mediaBox.Left) > 0.5 ||
                              Math.Abs(effectiveBox.Bottom - mediaBox.Bottom) > 0.5 ||
                              Math.Abs(effectiveBox.Right - mediaBox.Right) > 0.5 ||
                              Math.Abs(effectiveBox.Top - mediaBox.Top) > 0.5;
        bool sizeDisagrees = extracted.PageWidthPoints > 0 && extracted.PageHeightPoints > 0 &&
                             (Math.Abs(extracted.PageWidthPoints - displayWidth) > 1 ||
                              Math.Abs(extracted.PageHeightPoints - displayHeight) > 1);
        if (cropClipsMedia)
        {
            extractionDiagnostics.Add(new PdfTextLayerDiagnostic(
                PdfTextLayerDiagnosticCodes.PageBoxMismatch,
                $"The page's crop box clips the media box: the effective display page is " +
                $"{displayWidth:0.##}×{displayHeight:0.##}pt inside a {mediaBox.Width:0.##}×" +
                $"{mediaBox.Height:0.##}pt media box; geometry uses the effective frame.",
                PdfTextLayerDiagnosticSeverity.Warning));
        }
        else if (sizeDisagrees)
        {
            extractionDiagnostics.Add(new PdfTextLayerDiagnostic(
                PdfTextLayerDiagnosticCodes.PageBoxMismatch,
                $"PDFium's reported page size ({extracted.PageWidthPoints:0.##}×" +
                $"{extracted.PageHeightPoints:0.##}pt) differs from the rotated media box " +
                $"({displayWidth:0.##}×{displayHeight:0.##}pt); geometry uses the effective frame.",
                PdfTextLayerDiagnosticSeverity.Warning));
        }

        // Persisted-basis reconciliation: the render pipeline's recorded page dimensions are
        // the host's normalized-page baseline — native geometry is only adopted on top of it
        // when it is consistent, and a baseline recorded against older bytes is ignored.
        bool basisStale = !string.IsNullOrEmpty(page.SourceFileHash) &&
                          !string.IsNullOrEmpty(validation.FullHash) &&
                          !string.Equals(page.SourceFileHash, validation.FullHash,
                              StringComparison.OrdinalIgnoreCase);
        if (basisStale)
        {
            extractionDiagnostics.Add(new PdfTextLayerDiagnostic(
                PdfTextLayerDiagnosticCodes.PageBasisStale,
                "The persisted page baseline was recorded against older source bytes; the " +
                "native basis is used and the render baseline should be refreshed.",
                PdfTextLayerDiagnosticSeverity.Warning));
        }

        int effectiveRotation = extracted.RotationDegrees;
        double? persistedAspect = page.BasisWidth > 0 && page.BasisHeight > 0
            ? page.BasisWidth / page.BasisHeight
            : page.Width > 0 && page.Height > 0
                ? page.Width / page.Height
                : null;
        if (!basisStale && persistedAspect is { } persisted)
        {
            double nativeAspect = displayWidth / displayHeight;
            double unrotatedAspect = boundWidth / boundHeight;
            if (Math.Abs(persisted - nativeAspect) <= 0.03)
            {
                // Consistent baseline: adopt the native rotation.
            }
            else if (extracted.RotationDegrees != 0 &&
                     Math.Abs(persisted - unrotatedAspect) <= 0.03)
            {
                // The persisted baseline predates the /Rotate handling — the rendered pixels
                // the normalized basis describes are un-rotated, so the host's display frame
                // for this page is the un-rotated user space.
                effectiveRotation = 0;
                displayWidth = boundWidth;
                displayHeight = boundHeight;
                extractionDiagnostics.Add(new PdfTextLayerDiagnostic(
                    PdfTextLayerDiagnosticCodes.RotationBasisReconciled,
                    $"Persisted page baseline matches the un-rotated page while the source " +
                    $"declares /Rotate {extracted.RotationDegrees}°; the host basis was " +
                    "reconciled to the persisted frame.",
                    PdfTextLayerDiagnosticSeverity.Warning));
            }
            else
            {
                return Result<PdfTextLayerContext>.Failure(
                    PdfTextLayerDiagnosticCodes.SourceBasisMismatch,
                    $"Persisted page aspect ({persisted:0.###}) matches neither the native " +
                    $"display page ({nativeAspect:0.###}) nor the un-rotated page " +
                    $"({unrotatedAspect:0.###}); the stored baseline cannot be trusted.");
            }
        }
        else if (extracted.RotationDegrees != page.Rotation)
        {
            extractionDiagnostics.Add(new PdfTextLayerDiagnostic(
                PdfTextLayerDiagnosticCodes.PageRotationMismatch,
                $"Persisted page rotation ({page.Rotation}°) differs from the source's native " +
                $"rotation ({extracted.RotationDegrees}°); native truth was used.",
                PdfTextLayerDiagnosticSeverity.Warning));
        }

        PdfiumTextPage basis = effectiveRotation == extracted.RotationDegrees
            ? extracted
            : extracted with { RotationDegrees = effectiveRotation };
        IReadOnlyList<PdfTextGlyph> glyphs = PdfTextCoordinates.ToDisplayGlyphs(basis);
        PdfTextPageGeometry geometry = new(displayWidth, displayHeight, effectiveRotation,
            validation.FullHash ?? page.SourceFileHash);
        PdfTextLayerContext context = new(
            new ExtensionPageIdentity(request.DocumentId, request.PageId, request.RevisionId,
                page.PageIndex),
            glyphs, geometry, request.Config, request.Region, extractionDiagnostics, request.Progress)
        {
            ReadPageImageAsync = request.IncludePageImage ? BuildPageImageReader(request) : null,
            DetectLayoutAsync = request.IncludeLayoutDetection ? BuildLayoutDetector() : null
        };
        return Result<PdfTextLayerContext>.Success(context);
    }

    /// <summary>
    /// Page images come through the host render service so the lazy path reuses the same
    /// approved-source validation and PNG cache as the rest of the pipeline. Supplied only for
    /// providers that declared <see cref="PdfTextLayerDefinition.RequiresPageImage"/>.
    /// </summary>
    private Func<int, CancellationToken, ValueTask<PdfTextPageImage>>? BuildPageImageReader(
        PdfTextLayerContextRequest request)
    {
        if (_pageRenders is null)
        {
            return null;
        }

        return async (dpi, token) =>
        {
            Result<PageRenderResult> rendered = await _pageRenders.RenderPageAsync(
                new PageRenderRequest(request.DocumentId, request.PageId, Dpi: dpi,
                    Purpose: PageRenderPurpose.Ocr), token);
            if (rendered.IsFailure)
            {
                throw new InvalidOperationException(
                    rendered.ErrorMessage ?? "The page image could not be rendered for the text-layer provider.");
            }

            if (rendered.Value.Status is not (PageRenderStatus.Rendered or PageRenderStatus.FromCache) ||
                rendered.Value.CacheImagePath is null)
            {
                throw new InvalidOperationException(
                    rendered.Value.Warning ?? "The page image could not be rendered for the text-layer provider.");
            }

            byte[] png = await File.ReadAllBytesAsync(rendered.Value.CacheImagePath, token);
            return new PdfTextPageImage(png, rendered.Value.WidthPixels, rendered.Value.HeightPixels,
                rendered.Value.Dpi);
        };
    }

    /// <summary>
    /// Routes layout detection through the host's existing shared detector; the provider picks
    /// the model id/path from its own configuration so the visual scheme stays selected.
    /// </summary>
    private Func<PdfTextPageImage, string, string, CancellationToken,
        ValueTask<Result<IReadOnlyList<PdfTextLayoutRegion>>>>? BuildLayoutDetector()
    {
        if (_layoutDetector is null)
        {
            return null;
        }

        return async (image, modelId, modelPath, token) =>
        {
            Result validation = _layoutDetector.ValidateModel(modelId, modelPath);
            if (validation.IsFailure)
            {
                return Result<IReadOnlyList<PdfTextLayoutRegion>>.Failure(
                    PdfTextLayerDiagnosticCodes.LayoutModelMissing, validation.ErrorMessage!);
            }

            Result<IReadOnlyList<DocumentLayoutRegion>> detected =
                await _layoutDetector.DetectAsync(image.PngBytes, modelId, modelPath, token);
            if (detected.IsFailure)
            {
                return Result<IReadOnlyList<PdfTextLayoutRegion>>.Failure(
                    PdfTextLayerDiagnosticCodes.LayoutDetectionFailed, detected.ErrorMessage!);
            }

            return Result<IReadOnlyList<PdfTextLayoutRegion>>.Success(detected.Value
                .Select(region => new PdfTextLayoutRegion(region.Label, region.BBox,
                    region.Confidence, region.Order))
                .ToArray());
        };
    }

    private static DateTimeOffset? ParseMtime(string? value)
    {
        return DateTimeOffset.TryParse(value, out DateTimeOffset parsed) ? parsed : null;
    }

    private sealed class PageRow
    {
        public string PageId { get; set; } = "";
        public string DocumentInstanceId { get; set; } = "";
        public int PageIndex { get; set; }
        public int Rotation { get; set; }
        public string CoordinateBasis { get; set; } = "";
        public string? SourceFileHash { get; set; }
        public double? Width { get; set; }
        public double? Height { get; set; }
        public double? BasisWidth { get; set; }
        public double? BasisHeight { get; set; }
        public string? RendererBasisVersion { get; set; }
    }

    private sealed class AssetRow
    {
        public string Status { get; set; } = "";
        public string OriginalPath { get; set; } = "";
        public long SizeBytes { get; set; }
        public string? MtimeUtc { get; set; }
        public string? QuickHash { get; set; }
        public string? FullBlake3 { get; set; }
    }
}
