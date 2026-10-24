using Patchouli.Core.Diagnostics;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Core.View;
using Patchouli.Infrastructure.Documents;
using Patchouli.Ocr;
using SkiaSharp;

namespace Patchouli.Infrastructure.Rendering;

/// <summary>
/// Issue #11: reads source images from the ORIGINAL PDF for model text-image collation.
/// Built on the shared page render pipeline (source resolution, fingerprint
/// verification, PDFium rendering), so images are never re-rendered from OCR text.
/// The render DPI is chosen for the target region (never a low-res whole-page render
/// followed by a crop), and a region crop service handles margins and boundary
/// clipping. Coordinate convention: PDFium applies the page /Rotate at render time, so
/// the pixel buffer is the displayed page and normalized bboxes — which the layout
/// pipeline stores in displayed orientation — map directly onto it, exactly like the
/// existing OCR region crops.
/// </summary>
public sealed class PageImageViewService : IPageImageViewService
{
    /// <summary>Maximum encoded PNG bytes; larger results fail explicitly, never truncated.</summary>
    public const long MaxPngBytes = 8L * 1024 * 1024;

    /// <summary>Maximum image pixels; larger results fail explicitly.</summary>
    public const long MaxPixels = 16_777_216;

    private const double BoxMarginFraction = 0.02;
    private const int MinDpi = 72;
    private const int MaxDpi = 600;

    private readonly IPageService _pages;
    private readonly IDocumentTreeService _trees;
    private readonly IPageRenderService _renders;

    public PageImageViewService(
        IPageService pages,
        IDocumentTreeService trees,
        IPageRenderService renders)
    {
        _pages = pages;
        _trees = trees;
        _renders = renders;
    }

    public async Task<Result<PageImageViewResult>> ViewAsync(
        PageImageViewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Result validation = request.Validate();
        if (validation.IsFailure)
        {
            return Result<PageImageViewResult>.Failure(validation.ErrorCode!, validation.ErrorMessage!);
        }

        Result<Page> pageResult = await ResolvePageAsync(request, cancellationToken);
        if (pageResult.IsFailure)
        {
            return Result<PageImageViewResult>.Failure(pageResult.ErrorCode!, pageResult.ErrorMessage!);
        }

        Page page = pageResult.Value;
        Result<DocumentTreeRevisionId?> revisionResult =
            await ResolveRevisionAsync(request, page, cancellationToken);
        if (revisionResult.IsFailure)
        {
            return Result<PageImageViewResult>.Failure(revisionResult.ErrorCode!, revisionResult.ErrorMessage!);
        }

        DocumentTreeRevisionId? usedRevision = revisionResult.Value;
        if (request.BoxId is not null && usedRevision is null)
        {
            // A box target is addressed inside the tree; without a committed revision
            // there is no box to view (a whole-page target below does not need one).
            return Result<PageImageViewResult>.Failure(PageImageViewErrorCodes.TargetNotFound,
                "The requested box does not exist: the page has no committed revision.");
        }

        Result<ViewRegion> regionResult = await ResolveRegionAsync(request, page, usedRevision, cancellationToken);
        if (regionResult.IsFailure)
        {
            return Result<PageImageViewResult>.Failure(regionResult.ErrorCode!, regionResult.ErrorMessage!);
        }

        ViewRegion region = regionResult.Value;
        IReadOnlyList<NumberedBox> numberedBoxes = [];
        if (request.NumberedBoxes && usedRevision is not null)
        {
            Result<IReadOnlyList<DocumentBox>> boxes =
                await _trees.ListBoxesAsync(usedRevision.Value, cancellationToken);
            if (boxes.IsFailure)
            {
                return Result<PageImageViewResult>.Failure(boxes.ErrorCode!, boxes.ErrorMessage!);
            }

            numberedBoxes = NumberBoxes(boxes.Value, page.PageId, cancellationToken);
        }

        int dpi = ChooseDpi(page, region.Crop, request.MaxEdge);
        Result<PdfPagePixelBufferLease> rendered = await _renders.RenderPreviewAsync(
            new PageRenderRequest(
                request.DocumentInstanceId,
                page.PageId,
                null,
                dpi,
                "png",
                PageRenderPurpose.Preview),
            cancellationToken);
        if (rendered.IsFailure)
        {
            return Result<PageImageViewResult>.Failure(
                MapSourceError(rendered.ErrorCode, rendered.ErrorMessage),
                rendered.ErrorMessage ?? "The source page could not be rendered.");
        }

        using PdfPagePixelBufferLease lease = rendered.Value;
        Result<PageImageViewResult> cropped =
            CropToViewResult(request, page, usedRevision, region, lease, numberedBoxes);
        return cropped;
    }

    private async Task<Result<Page>> ResolvePageAsync(
        PageImageViewRequest request,
        CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<Page>> pages =
            await _pages.ListPagesAsync(request.DocumentInstanceId, cancellationToken);
        if (pages.IsFailure)
        {
            return Result<Page>.Failure(pages.ErrorCode!, pages.ErrorMessage!);
        }

        // The canonical page URI uses the one-based physical PDF page index.
        Page? page = pages.Value.FirstOrDefault(candidate =>
            candidate.PageIndex == request.PageIndex1Based - 1);
        return page is null
            ? Result<Page>.Failure(PageImageViewErrorCodes.TargetNotFound,
                $"Page {request.PageIndex1Based} does not exist in this document.")
            : Result<Page>.Success(page);
    }

    /// <summary>
    /// Resolves the tree revision a view is pinned to. A null result is a legitimate
    /// outcome, not a failure: the page exists physically but has no committed tree
    /// revision yet (it was never OCR'd into a box tree). Issue #28: the PDF workbench
    /// shows such a page's rendered image without any tree, so a whole-page view must
    /// work tree-less as well (human/agent parity); only box targets and box numbering
    /// need the revision. An explicitly requested revision is different: it must exist,
    /// and a missing one stays a stale-revision failure — never a silent substitution.
    /// </summary>
    private async Task<Result<DocumentTreeRevisionId?>> ResolveRevisionAsync(
        PageImageViewRequest request,
        Page page,
        CancellationToken cancellationToken)
    {
        if (request.RevisionId is null)
        {
            // An unspecified revision pins to the actually-used revision of this call.
            Result<DocumentTreeRevision> current =
                await _trees.GetCurrentRevisionAsync(request.DocumentInstanceId, page.PageId, cancellationToken);
            if (current.IsSuccess)
            {
                return Result<DocumentTreeRevisionId?>.Success(current.Value.TreeRevisionId);
            }

            return string.Equals(current.ErrorCode, AppErrorCodes.NotFound, StringComparison.Ordinal)
                ? Result<DocumentTreeRevisionId?>.Success(null)
                : Result<DocumentTreeRevisionId?>.Failure(current.ErrorCode!, current.ErrorMessage!);
        }

        // A historical revision must exist; never substitute the latest revision.
        Result<IReadOnlyList<DocumentTreeRevision>> revisions =
            await _trees.ListRevisionsAsync(request.DocumentInstanceId, page.PageId, cancellationToken);
        if (revisions.IsFailure)
        {
            return Result<DocumentTreeRevisionId?>.Failure(revisions.ErrorCode!, revisions.ErrorMessage!);
        }

        DocumentTreeRevisionId? requested = request.RevisionId;
        bool exists = requested is not null && revisions.Value.Any(revision =>
            revision.TreeRevisionId == requested);
        return exists
            ? Result<DocumentTreeRevisionId?>.Success(requested!.Value)
            : Result<DocumentTreeRevisionId?>.Failure(PageImageViewErrorCodes.StaleRevision,
                "The requested revision does not exist for this page.");
    }

    private sealed record ViewRegion(NormalizedBBox Crop, string ViewType);

    /// <summary>One box of the viewed page with its drawn sequence number and geometry.</summary>
    private sealed record NumberedBox(int Index, DocumentBoxId BoxId, NormalizedBBox BBox);

    /// <summary>
    /// Issue #28: assigns sequence numbers in page XML document order — the depth-first
    /// pre-order traversal <c>PageXmlCodec.EncodeBox</c> emits (a box, then its children),
    /// with siblings chained by <c>NextSiblingBoxId</c> via
    /// <see cref="DocumentBoxProjection.Siblings"/>. An agent can therefore map a number
    /// drawn on the image to the n-th <c>&lt;box&gt;</c> element of the fetched page XML,
    /// and the result's mapping resolves that number to the stable box id.
    /// </summary>
    private static IReadOnlyList<NumberedBox> NumberBoxes(IReadOnlyList<DocumentBox> boxes, PageId pageId,
        CancellationToken cancellationToken)
    {
        DocumentBox[] pageBoxes = boxes.Where(box => box.PageId == pageId).ToArray();
        // Build parent/child groups and each sibling chain once. The projection helper
        // scans the entire page per parent, which made this traversal quadratic.
        List<DocumentBox> rootBoxes = [];
        Dictionary<DocumentBoxId, List<DocumentBox>> byParent = [];
        Dictionary<DocumentBoxId, DocumentBox> byId = new(pageBoxes.Length);
        foreach (DocumentBox box in pageBoxes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byId.TryAdd(box.BoxId, box);
            if (box.ParentBoxId is not { } parentId)
            {
                rootBoxes.Add(box);
                continue;
            }

            if (!byParent.TryGetValue(parentId, out List<DocumentBox>? children))
            {
                children = [];
                byParent.Add(parentId, children);
            }

            children.Add(box);
        }

        Dictionary<DocumentBoxId, DocumentBox[]> orderedChildren = new(byParent.Count);
        DocumentBox[] orderedRoots = OrderSiblings(rootBoxes, null);
        foreach ((DocumentBoxId parentId, List<DocumentBox> children) in byParent)
        {
            cancellationToken.ThrowIfCancellationRequested();
            orderedChildren.Add(parentId, OrderSiblings(children, parentId));
        }

        List<NumberedBox> numbered = [];
        Stack<DocumentBox> pending = new();
        for (int index = orderedRoots.Length - 1; index >= 0; index--)
        {
            pending.Push(orderedRoots[index]);
        }

        // Iterative pre-order avoids call-stack growth for unusually deep but valid trees.
        HashSet<DocumentBoxId> visited = [];
        while (pending.TryPop(out DocumentBox? box))
        {
            if (box is null)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(box.BoxId))
            {
                continue;
            }

            numbered.Add(new NumberedBox(numbered.Count + 1, box.BoxId, box.BBox));
            if (orderedChildren.TryGetValue(box.BoxId, out DocumentBox[]? children))
            {
                for (int index = children.Length - 1; index >= 0; index--)
                {
                    pending.Push(children[index]);
                }
            }
        }

        return numbered;

        DocumentBox[] OrderSiblings(IReadOnlyList<DocumentBox> siblings, DocumentBoxId? parentId)
        {
            HashSet<DocumentBoxId> referenced = siblings
                .Where(child => child.NextSiblingBoxId is not null)
                .Select(child => child.NextSiblingBoxId!.Value)
                .ToHashSet();
            DocumentBox? current = siblings.FirstOrDefault(child => !referenced.Contains(child.BoxId));
            List<DocumentBox> ordered = new(siblings.Count);
            HashSet<DocumentBoxId> visitedSiblings = [];
            while (current is not null && visitedSiblings.Add(current.BoxId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ordered.Add(current);
                current = current.NextSiblingBoxId is { } next && byId.TryGetValue(next, out DocumentBox? nextBox) &&
                          nextBox.ParentBoxId == parentId
                    ? nextBox
                    : null;
            }

            return ordered.ToArray();
        }
    }

    private async Task<Result<ViewRegion>> ResolveRegionAsync(
        PageImageViewRequest request,
        Page page,
        DocumentTreeRevisionId? usedRevision,
        CancellationToken cancellationToken)
    {
        NormalizedBBox fullPage = new(0, 0, 1, 1);
        if (request.BoxId is null)
        {
            string viewType = request.IsTranslation ? PageImageViewTypes.TranslationPage : PageImageViewTypes.Page;
            return Result<ViewRegion>.Success(new ViewRegion(fullPage, viewType));
        }

        if (usedRevision is null)
        {
            // Guarded by ViewAsync as well; a box target can never resolve tree-less.
            return Result<ViewRegion>.Failure(PageImageViewErrorCodes.TargetNotFound,
                "The requested box does not exist: the page has no committed revision.");
        }

        // Historical boxes use their own revision's bbox.
        Result<IReadOnlyList<DocumentBox>> boxes =
            await _trees.ListBoxesAsync(usedRevision.Value, cancellationToken);
        if (boxes.IsFailure)
        {
            return Result<ViewRegion>.Failure(boxes.ErrorCode!, boxes.ErrorMessage!);
        }

        DocumentBox? box = boxes.Value.FirstOrDefault(candidate =>
            candidate.BoxId == request.BoxId && candidate.PageId == page.PageId);
        if (box is null)
        {
            return Result<ViewRegion>.Failure(PageImageViewErrorCodes.TargetNotFound,
                "The requested box does not exist in this page revision.");
        }

        if (request.Context == PageImageViewContext.Page)
        {
            string pageViewType = request.IsTranslation
                ? PageImageViewTypes.TranslationPage
                : PageImageViewTypes.Page;
            return Result<ViewRegion>.Success(new ViewRegion(fullPage, pageViewType));
        }

        NormalizedBBox crop = WithMargin(box.BBox);
        Result cropValidation = crop.Validate();
        if (cropValidation.IsFailure)
        {
            return Result<ViewRegion>.Failure(PageImageViewErrorCodes.StaleCoordinates,
                "The box coordinates are stale for this page revision.");
        }

        string boxViewType = request.IsTranslation ? PageImageViewTypes.TranslationBox : PageImageViewTypes.Box;
        return Result<ViewRegion>.Success(new ViewRegion(crop, boxViewType));
    }

    /// <summary>Expands a box bbox by a small margin on every side, clipped to the page.</summary>
    public static NormalizedBBox WithMargin(NormalizedBBox bbox)
    {
        double x = Math.Max(0, bbox.X - BoxMarginFraction);
        double y = Math.Max(0, bbox.Y - BoxMarginFraction);
        double right = Math.Min(1, bbox.X + bbox.Width + BoxMarginFraction);
        double bottom = Math.Min(1, bbox.Y + bbox.Height + BoxMarginFraction);
        return new NormalizedBBox(x, y, Math.Max(0.001, right - x), Math.Max(0.001, bottom - y));
    }

    /// <summary>
    /// Chooses the render DPI so the cropped region's longest edge lands near
    /// <paramref name="maxEdge"/> pixels: the page is rendered at the target region's
    /// resolution instead of a fixed low resolution.
    /// </summary>
    public static int ChooseDpi(Page page, NormalizedBBox region, int maxEdge)
    {
        double regionLongest = Math.Max(region.Width, region.Height);
        double pageLongestPt = Math.Max(page.Width ?? 0, page.Height ?? 0);
        if (regionLongest <= 0 || pageLongestPt <= 0)
        {
            return 200;
        }

        double dpi = maxEdge * 72.0 / (regionLongest * pageLongestPt);
        return (int)Math.Clamp(Math.Round(dpi), MinDpi, MaxDpi);
    }

    private static Result<PageImageViewResult> CropToViewResult(
        PageImageViewRequest request,
        Page page,
        DocumentTreeRevisionId? usedRevision,
        ViewRegion region,
        PdfPagePixelBufferLease lease,
        IReadOnlyList<NumberedBox> numberedBoxes)
    {
        RegionPixelRect rect = RegionImageCrop.ComputePixelRect(region.Crop, lease.WidthPixels, lease.HeightPixels);
        long pixels = (long)rect.Width * rect.Height;
        if (pixels > MaxPixels)
        {
            return Result<PageImageViewResult>.Failure(PageImageViewErrorCodes.ImageTooLarge,
                $"The requested image would be {rect.Width}x{rect.Height} pixels, above the v1 limit.");
        }

        // The crop range is reported in page-normalized coordinates so the geometry is
        // reproducible regardless of the render DPI; the annotation mapping uses the
        // same range to place box outlines onto the output pixels.
        NormalizedBBox cropRange = PixelRectToNormalized(rect, lease.WidthPixels, lease.HeightPixels);
        byte[] png;
        int outWidth;
        int outHeight;
        IReadOnlyList<NumberedBox> drawnBoxes;
        try
        {
            (png, outWidth, outHeight, drawnBoxes) =
                CropLeaseToPng(lease, rect, request.MaxEdge, numberedBoxes, cropRange);
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.page-image-view", "crop"))
        {
            return Result<PageImageViewResult>.Failure(PageImageViewErrorCodes.RenderFailed,
                "The source region could not be cropped.");
        }

        if (png.Length > MaxPngBytes)
        {
            return Result<PageImageViewResult>.Failure(PageImageViewErrorCodes.ImageTooLarge,
                "The encoded image exceeds the v1 size limit; narrow the region or lower max_edge.");
        }

        string canonicalUri = BuildCanonicalUri(request, usedRevision);
        // The number mapping is built from exactly the boxes the drawing pass put
        // onto the image (returned by CropLeaseToPng), never re-filtered here: only
        // the drawing pass sees the pixel-quantized crop range and the final bitmap
        // size, so a second filtering at this layer would drift at the crop boundary.
        return Result<PageImageViewResult>.Success(new PageImageViewResult(
            canonicalUri,
            region.ViewType,
            "image/png",
            outWidth,
            outHeight,
            cropRange,
            0,
            usedRevision?.ToString() ?? string.Empty,
            png,
            request.NumberedBoxes
                ? drawnBoxes.Select(box => new PageImageBoxNumber(box.Index, box.BoxId)).ToArray()
                : null));
    }

    /// <summary>
    /// P2-7: computes the output dimensions after enforcing max_edge. Exposed for testing.
    /// </summary>
    public static (int Width, int Height) ComputeOutputSize(int width, int height, int maxEdge)
    {
        int longest = Math.Max(width, height);
        if (longest <= maxEdge || maxEdge <= 0)
        {
            return (width, height);
        }

        double scale = (double)maxEdge / longest;
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    private static (byte[] Png, int Width, int Height, IReadOnlyList<NumberedBox> DrawnBoxes) CropLeaseToPng(
        PdfPagePixelBufferLease lease,
        RegionPixelRect rect,
        int maxEdge,
        IReadOnlyList<NumberedBox> numberedBoxes,
        NormalizedBBox cropRange)
    {
        using SKBitmap source = new(lease.WidthPixels, lease.HeightPixels, SKColorType.Bgra8888, SKAlphaType.Premul);
        source.SetPixels(lease.PixelAddress);
        using SKBitmap crop = new(rect.Width, rect.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (SKCanvas canvas = new(crop))
        {
            canvas.DrawBitmap(source,
                SKRect.Create(rect.Left, rect.Top, rect.Width, rect.Height),
                SKRect.Create(0, 0, rect.Width, rect.Height));
            canvas.Flush();
        }

        // P2-7: max_edge is a hard cap on the output's longest edge. The render DPI
        // has a 72 floor, so a small max_edge on a large page would otherwise exceed it.
        (int scaledWidth, int scaledHeight) = ComputeOutputSize(crop.Width, crop.Height, maxEdge);
        SKBitmap final = crop;
        if (scaledWidth != crop.Width || scaledHeight != crop.Height)
        {
            SKBitmap scaled = new(scaledWidth, scaledHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (SKCanvas canvas = new(scaled))
            using (SKImage cropImage = SKImage.FromBitmap(crop))
            {
                canvas.DrawImage(cropImage,
                    SKRect.Create(0, 0, crop.Width, crop.Height),
                    SKRect.Create(0, 0, scaledWidth, scaledHeight),
                    new SKSamplingOptions(SKCubicResampler.Mitchell), null);
                canvas.Flush();
            }

            final = scaled;
        }

        IReadOnlyList<NumberedBox> drawnBoxes = [];
        if (numberedBoxes.Count > 0)
        {
            drawnBoxes = DrawNumberedBoxes(final, cropRange, numberedBoxes);
        }

        try
        {
            using SKImage image = SKImage.FromBitmap(final);
            using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
            return (data.ToArray(), final.Width, final.Height, drawnBoxes);
        }
        finally
        {
            if (!ReferenceEquals(final, crop))
            {
                final.Dispose();
            }
        }
    }

    /// <summary>
    /// Issue #28: draws each numbered box's outline and sequence number onto the final
    /// output bitmap. Box geometry is page-normalized; <paramref name="cropRange"/> is
    /// the page-normalized range the bitmap covers, so a box maps linearly onto output
    /// pixels and boxes outside the crop are skipped (their numbers stay stable: they
    /// are page-global, assigned in page XML document order). Returns the boxes that
    /// were actually drawn, in drawing order: this pass is the single source of truth
    /// for the result's number mapping.
    /// </summary>
    private static IReadOnlyList<NumberedBox> DrawNumberedBoxes(SKBitmap bitmap, NormalizedBBox cropRange,
        IReadOnlyList<NumberedBox> numberedBoxes)
    {
        SKColor accent = new(227, 49, 37);
        float fontSize = Math.Clamp(bitmap.Width / 72f, 12f, 30f);
        float strokeWidth = Math.Clamp(bitmap.Width / 600f, 1.5f, 4f);
        using SKFont font = new(SKTypeface.Default, fontSize) { Embolden = true };
        using SKPaint outline = new()
        {
            Color = accent, Style = SKPaintStyle.Stroke, StrokeWidth = strokeWidth,
            IsAntialias = true
        };
        using SKPaint labelFill = new() { Color = accent, Style = SKPaintStyle.Fill };
        using SKPaint labelText = new() { Color = SKColors.White, IsAntialias = true };
        using SKCanvas canvas = new(bitmap);
        List<NumberedBox> drawn = [];
        foreach (NumberedBox box in numberedBoxes)
        {
            double left = (box.BBox.X - cropRange.X) / cropRange.Width * bitmap.Width;
            double top = (box.BBox.Y - cropRange.Y) / cropRange.Height * bitmap.Height;
            double right = (box.BBox.X + box.BBox.Width - cropRange.X) / cropRange.Width * bitmap.Width;
            double bottom = (box.BBox.Y + box.BBox.Height - cropRange.Y) / cropRange.Height * bitmap.Height;
            if (right <= 0 || bottom <= 0 || left >= bitmap.Width || top >= bitmap.Height)
            {
                continue;
            }

            drawn.Add(box);
            SKRect rect = SKRect.Create(
                (float)Math.Max(0, left), (float)Math.Max(0, top),
                (float)(Math.Min(bitmap.Width, right) - Math.Max(0, left)),
                (float)(Math.Min(bitmap.Height, bottom) - Math.Max(0, top)));
            canvas.DrawRect(rect, outline);

            string label = box.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            font.MeasureText(label, out SKRect textBounds);
            float padding = fontSize * 0.3f;
            float labelWidth = textBounds.Width + padding * 2;
            float labelHeight = fontSize + padding * 1.6f;
            float labelX = Math.Clamp(rect.Left, 0, Math.Max(0, bitmap.Width - labelWidth));
            float labelY = Math.Clamp(rect.Top, 0, Math.Max(0, bitmap.Height - labelHeight));
            canvas.DrawRect(SKRect.Create(labelX, labelY, labelWidth, labelHeight), labelFill);
            canvas.DrawText(label,
                labelX + padding - textBounds.Left,
                labelY + (labelHeight - textBounds.Height) / 2f - textBounds.Top,
                font, labelText);
        }

        canvas.Flush();
        return drawn;
    }

    private static NormalizedBBox PixelRectToNormalized(RegionPixelRect rect, int imageWidth, int imageHeight)
    {
        return new NormalizedBBox(
            (double)rect.Left / imageWidth,
            (double)rect.Top / imageHeight,
            (double)rect.Width / imageWidth,
            (double)rect.Height / imageHeight);
    }

    private static string BuildCanonicalUri(PageImageViewRequest request, DocumentTreeRevisionId? usedRevision)
    {
        string root = request.IsTranslation ? "translations" : "texts";
        string uri = $"patchouli://{root}/{request.DocumentInstanceId}/page-{request.PageIndex1Based}.xml";
        if (usedRevision is null)
        {
            // Tree-less page view: there is no revision to pin, so the canonical URI is
            // the plain page URI (a box suffix cannot occur — boxes require a revision).
            return uri;
        }

        uri = $"{uri}?rev={usedRevision.Value}";
        return request.BoxId is null ? uri : $"{uri}&box={request.BoxId}";
    }

    private static string MapSourceError(string? errorCode, string? message)
    {
        if (string.Equals(errorCode, AppErrorCodes.NotFound, StringComparison.Ordinal) ||
            message?.Contains("unavailable", StringComparison.OrdinalIgnoreCase) == true)
        {
            return PageImageViewErrorCodes.SourceMissing;
        }

        if (message?.Contains("source_changed", StringComparison.OrdinalIgnoreCase) == true ||
            message?.Contains("bbox_basis_stale", StringComparison.OrdinalIgnoreCase) == true)
        {
            return PageImageViewErrorCodes.SourceChanged;
        }

        return PageImageViewErrorCodes.RenderFailed;
    }
}
