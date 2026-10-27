using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.Ocr;
using Patchouli.Ocr.Engines.PdfText;

namespace Patchouli.Tests;

/// <summary>Builders and inert service doubles shared by the PDF text-layer tests.</summary>
internal static class PdfTextLayerTestSupport
{
    public static PdfTextGlyph Glyph(int index, string text, double x, double y, double w, double h,
        double angle = 0, string font = "TestFont", double fontSize = 10,
        PdfGlyphFlags flags = PdfGlyphFlags.None)
    {
        return new PdfTextGlyph(index, text, new NormalizedBBox(x, y, w, h),
            x, y + h, angle, OrientationOf(angle), font, fontSize, flags);
    }

    /// <summary>A horizontal line of glyphs placed left→right in display space.</summary>
    public static List<PdfTextGlyph> TextLine(int startIndex, string text, double x, double y,
        double charWidth = 0.012, double charHeight = 0.015, double advance = 0.014)
    {
        List<PdfTextGlyph> glyphs = new(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            glyphs.Add(Glyph(startIndex + i, text[i].ToString(), x + i * advance, y, charWidth,
                charHeight));
        }

        return glyphs;
    }

    /// <summary>A vertical column of glyphs placed top→bottom in display space.</summary>
    public static List<PdfTextGlyph> VerticalLine(int startIndex, string text, double x, double y,
        double charWidth = 0.02, double charHeight = 0.018, double advance = 0.02)
    {
        List<PdfTextGlyph> glyphs = new(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            glyphs.Add(Glyph(startIndex + i, text[i].ToString(), x, y + i * advance, charWidth,
                charHeight, 90));
        }

        return glyphs;
    }

    /// <summary>
    /// A vertical column rendered with UPRIGHT glyph metrics (angle 0): the kind of vertical
    /// Japanese text whose orientation can only be recovered from glyph positions.
    /// </summary>
    public static List<PdfTextGlyph> UprightVerticalLine(int startIndex, string text, double x,
        double y, double charWidth = 0.02, double charHeight = 0.02, double advance = 0.022)
    {
        List<PdfTextGlyph> glyphs = new(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            glyphs.Add(Glyph(startIndex + i, text[i].ToString(), x, y + i * advance, charWidth,
                charHeight, 0));
        }

        return glyphs;
    }

    public static PdfTextLayerContext Context(
        IReadOnlyList<PdfTextGlyph>? glyphs = null,
        NormalizedBBox? region = null,
        IReadOnlyList<PdfTextLayerDiagnostic>? extractionDiagnostics = null,
        Func<int, CancellationToken, ValueTask<PdfTextPageImage>>? readPageImage = null,
        Func<PdfTextPageImage, string, string, CancellationToken,
            ValueTask<Result<IReadOnlyList<PdfTextLayoutRegion>>>>? detectLayout = null)
    {
        ExtensionConfigSnapshot config = new(new Dictionary<string, string>(), 0);
        return new PdfTextLayerContext(
            new ExtensionPageIdentity(DocumentInstanceId.New(), PageId.New(),
                DocumentTreeRevisionId.New(), 0),
            glyphs ?? Array.Empty<PdfTextGlyph>(),
            new PdfTextPageGeometry(612, 792, 0, "hash"),
            config, region, extractionDiagnostics)
        {
            ReadPageImageAsync = readPageImage,
            DetectLayoutAsync = detectLayout
        };
    }

    public static PdfTextLayerParseOptions Options(
        double lineGap = 0.45, double columnGap = 1.2,
        string? modelId = "test-layout", string? modelPath = "/models/layout.onnx",
        double minCoverage = 0.25, bool supplement = false, bool recognizeNontext = true)
    {
        return new PdfTextLayerParseOptions(lineGap, columnGap, true, 0.32, 0.5,
            modelId, modelPath, 200, minCoverage, recognizeNontext, supplement);
    }

    public static PdfiumTextPage RawPage(IReadOnlyList<PdfiumTextGlyph> glyphs,
        double left = 0, double bottom = 0, double right = 612, double top = 792, int rotation = 0,
        double? pageWidth = null, double? pageHeight = null, PdfPageBox? bound = null)
    {
        // The FPDF_GetPageWidthF/HeightF default mirrors the effective display box the
        // renderer sizes from — the crop box intersected with the media box.
        PdfPageBox media = new(left, bottom, right, top);
        PdfPageBox effective = bound ?? media;
        return new PdfiumTextPage(media, effective, rotation, glyphs.Count, glyphs,
            pageWidth ?? (rotation is 90 or 270 ? effective.Height : effective.Width),
            pageHeight ?? (rotation is 90 or 270 ? effective.Width : effective.Height));
    }

    public static PdfiumTextGlyph RawGlyph(int index, string text, double left, double right,
        double bottom, double top, double originX, double originY,
        PdfiumTextGlyphFlags flags = PdfiumTextGlyphFlags.None, double angleRadians = 0)
    {
        return new PdfiumTextGlyph(index, text, left, right, bottom, top, originX, originY,
            angleRadians, flags, "TestFont", 0, 10);
    }

    /// <summary>The text a produced box carries, whatever the payload subtype.</summary>
    public static string? TextOf(PdfTextLayerBox box)
    {
        return box.Box.Payload switch
        {
            TextBoxPayload text => text.Markdown,
            CodeBoxPayload code => code.Code,
            EquationBoxPayload equation => equation.Latex,
            ListBoxPayload list => list.Markdown,
            _ => null
        };
    }

    /// <summary>The union of a box's source-char anchor runs as a sorted index list.</summary>
    public static List<int> AnchorIndices(PdfTextLayerBox box)
    {
        List<int> indices = [];
        foreach (PdfTextAnchor anchor in box.Anchors)
        {
            for (int i = anchor.StartCharIndex; i < anchor.StartCharIndex + anchor.CharCount; i++)
            {
                indices.Add(i);
            }
        }

        return indices;
    }

    public static List<int> AnchorIndices(PdfTextRequiredRegion region)
    {
        List<int> indices = [];
        foreach (PdfTextAnchor anchor in region.Anchors ?? [])
        {
            for (int i = anchor.StartCharIndex; i < anchor.StartCharIndex + anchor.CharCount; i++)
            {
                indices.Add(i);
            }
        }

        return indices;
    }

    /// <summary>A page image stub for visual-guided tests — bytes are never decoded by the provider.</summary>
    public static PdfTextPageImage StubImage(int dpi = 200)
    {
        return new PdfTextPageImage([137, 80, 78, 71], 1224, 1584, dpi);
    }

    public static PdfTextLayoutRegion Region(NormalizedBBox box, string label = "text",
        double confidence = 0.9)
    {
        return new PdfTextLayoutRegion(label, box, confidence, 0);
    }

    public sealed class FakeTextExtractor(PdfiumTextPage page) : IPdfTextExtractor
    {
        public List<(string Path, int Index)> Calls { get; } = [];

        public Task<PdfiumTextPage> ExtractTextPageAsync(string pdfPath, int pageIndex,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((pdfPath, pageIndex));
            return Task.FromResult(page);
        }
    }

    public sealed class FakeMaterialization : IFileMaterializationService
    {
        public List<string> Paths { get; } = [];

        public Task<Result> EnsureAvailableAsync(string path, CancellationToken cancellationToken = default)
        {
            Paths.Add(path);
            return Task.FromResult(Result.Success());
        }
    }

    public sealed class FakeSourceValidation(string status = SourceValidationStatus.Current)
        : ISourceValidationService
    {
        private SourceValidationResult Result(SourceValidationRequest request)
        {
            // Echo the stored full hash like a real unchanged-source validation would, so
            // persisted page-basis reconciliation keys off the persisted hash — the page
            // row's source_file_hash decides staleness, not a hard-coded value.
            return new SourceValidationResult(status, request.ResolvedPath,
                request.StoredFullHash,
                status == SourceValidationStatus.Current ? null : $"status is {status}", false);
        }

        public Task<SourceValidationResult> GetLastKnownAsync(SourceValidationRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result(request));
        }

        public Task<SourceValidationResult?> TryGetCurrentAsync(SourceValidationRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<SourceValidationResult?>(Result(request));
        }

        public Task<SourceValidationResult> EnsureValidatedAsync(SourceValidationRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result(request));
        }

        public Task InvalidateAsync(FileAssetId fileAssetId,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    public sealed class FakeFileResolution(FileResolutionResult result) : IFileResolutionService
    {
        public static FakeFileResolution Available(string path)
        {
            return new FakeFileResolution(new FileResolutionResult(FileAssetId.New(),
                FileAssetStatus.Available, path, [], "high", "none", null));
        }

        public static FakeFileResolution WithStatus(string status, string? warning = null)
        {
            return new FakeFileResolution(new FileResolutionResult(FileAssetId.New(),
                status, status == FileAssetStatus.Available ? "/x/source.pdf" : null, [], "high",
                "none", warning));
        }

        public Task<Result<FileResolutionResult>> ResolveFileAsync(FileAssetId fileAssetId,
            ResolveFilePurpose purpose, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<FileResolutionResult>.Success(
                result with { FileAssetId = fileAssetId }));
        }

        public Task<Result<FileSearchRoot>> AddSearchRootAsync(SelectedFileSearchRoot selectedRoot,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<FileSearchRoot>> BindSearchRootAsync(FileSearchRootId rootId,
            SelectedFileSearchRoot selectedRoot, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<IReadOnlyList<FileSearchRoot>>> ListSearchRootsAsync(
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result> DeleteSearchRootAsync(FileSearchRootId rootId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result> SetSearchRootAvailabilityAsync(FileSearchRootId rootId, bool isAvailable,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result> SetSearchRootOrderAsync(IReadOnlyList<FileSearchRootId> orderedRootIds,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<IReadOnlyList<KnownFileLocation>>> ListKnownLocationsAsync(
            FileAssetId fileAssetId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<FileAsset>> ConfirmMovedCandidateAsync(FileAssetId fileAssetId,
            string selectedPath, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<FileAsset>> RebindSourceAsync(FileAssetId fileAssetId, string selectedPath,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<FileAsset>> ConfirmChangedFileAsync(FileAssetId fileAssetId,
            string selectedPath, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result> ReuseRevisionForNewFingerprintAsync(FileAssetId fileAssetId,
            string selectedPath, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result> KeepOldEvidenceAsync(FileAssetId fileAssetId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result> MarkFileMissingAsync(FileAssetId fileAssetId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private static PdfGlyphOrientation OrientationOf(double angle)
    {
        double folded = angle % 180;
        if (folded < 0)
        {
            folded += 180;
        }

        return folded is < 30 or >= 150
            ? PdfGlyphOrientation.Horizontal
            : folded is >= 60 and <= 120
                ? PdfGlyphOrientation.Vertical
                : PdfGlyphOrientation.Rotated;
    }
}
