using System.Reflection;
using System.Xml.Linq;
using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Core.View;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Rendering;
using Patchouli.Ocr;
using SkiaSharp;

namespace Patchouli.Tests;

/// <summary>
/// Issue #28 independent tests: numbered-box view and tree-less page view against the
/// real <see cref="PageImageViewService"/> with stubbed page/tree/render dependencies.
/// Covers (a) box numbers following the page XML document order exactly (the order
/// <see cref="PageXmlCodec.EncodePage"/> emits &lt;box&gt; elements), (b) crop views
/// keeping page-global numbers for the boxes actually drawn, and (c) the four
/// revision-resolution branches for pages without a committed tree.
/// Pixel assertions only verify that the accent color is present/absent; the exact
/// glyph rendering is not asserted.
/// </summary>
public sealed class Issue28PageImageViewTests
{
    private const int BufferWidth = 800;
    private const int BufferHeight = 600;

    // ---------- numbered boxes ----------

    [Fact]
    public async Task Boxes_numbering_matches_page_xml_document_order()
    {
        Harness harness = new();
        DocumentBoxId a = harness.AddBox(null, null, new NormalizedBBox(0.05, 0.05, 0.30, 0.10));
        DocumentBoxId a1 = harness.AddBox(a, null, new NormalizedBBox(0.06, 0.06, 0.10, 0.03));
        DocumentBoxId a1A = harness.AddBox(a1, null, new NormalizedBBox(0.06, 0.06, 0.05, 0.02));
        DocumentBoxId a2 = harness.AddBox(a, null, new NormalizedBBox(0.20, 0.06, 0.10, 0.03));
        DocumentBoxId c = harness.AddBox(null, null, new NormalizedBBox(0.05, 0.30, 0.30, 0.10));
        DocumentBoxId d = harness.AddBox(null, null, new NormalizedBBox(0.05, 0.55, 0.30, 0.10));
        harness.LinkSiblings(a, c);
        harness.LinkSiblings(c, d);
        harness.LinkSiblings(a1, a2);
        // A box of another page rides along in the revision listing; numbering is per page.
        harness.AddBox(null, null, new NormalizedBBox(0.10, 0.10, 0.20, 0.20), true);
        harness.ShuffleStorage();

        Result<PageImageViewResult> result = await harness.Service.ViewAsync(
            harness.Request(numberedBoxes: true));

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        IReadOnlyList<PageImageBoxNumber> numbers = result.Value.BoxNumbers!;
        numbers.Should().NotBeNull();
        numbers.Select(number => number.Index).Should().Equal(1, 2, 3, 4, 5, 6);
        numbers.Select(number => number.BoxId).Should().Equal(a, a1, a1A, a2, c, d);

        // The same sequence must come out of the page XML document order.
        string xml = PageXmlCodec.EncodePage(harness.DocId, 1, harness.RevisionId, harness.PageBoxes());
        List<string> xmlIds = XDocument.Parse(xml).Descendants("box")
            .Select(element => element.Attribute("id")!.Value).ToList();
        numbers.Select(number => number.BoxId.ToString()).Should().Equal(xmlIds);

        // The overlay actually drew accent pixels onto the white page.
        CountAccentPixels(result.Value.PngBytes).Should().BeGreaterThan(100);
    }

    [Fact]
    public async Task Large_deep_tree_numbering_is_ordered_without_recursive_stack_growth()
    {
        Harness harness = new();
        DocumentBoxId? parent = null;
        const int boxCount = 1_200;
        List<DocumentBoxId> expected = new(boxCount);
        for (int index = 0; index < boxCount; index++)
        {
            parent = harness.AddBox(parent, null, new NormalizedBBox(0.05, 0.05, 0.01, 0.01));
            expected.Add(parent.Value);
        }

        Result<PageImageViewResult> result = await harness.Service.ViewAsync(
            harness.Request(numberedBoxes: true));

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.BoxNumbers.Should().HaveCount(boxCount);
        result.Value.BoxNumbers!.Select(number => number.Index)
            .Should().Equal(Enumerable.Range(1, boxCount));
        result.Value.BoxNumbers.Select(number => number.BoxId).Should().Equal(expected);
    }

    [Fact]
    public async Task Boxes_region_crop_reports_only_intersecting_boxes_with_global_numbers()
    {
        Harness harness = new();
        DocumentBoxId a = harness.AddBox(null, null, new NormalizedBBox(0.05, 0.05, 0.20, 0.20));
        DocumentBoxId b = harness.AddBox(a, null, new NormalizedBBox(0.08, 0.08, 0.05, 0.05));
        DocumentBoxId far = harness.AddBox(null, null, new NormalizedBBox(0.70, 0.70, 0.20, 0.20));
        harness.LinkSiblings(a, far);

        Result<PageImageViewResult> result = await harness.Service.ViewAsync(
            harness.Request(a, PageImageViewContext.Region, numberedBoxes: true));

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        // Page-global order is A=1, B=2, far=3. The crop covers A (plus margin) and its
        // child B only; the far box is not drawn, so the mapping "drawn onto the image"
        // must not list it, and the listed numbers stay page-global.
        result.Value.BoxNumbers.Should().NotBeNull();
        result.Value.BoxNumbers!.Select(number => (number.Index, number.BoxId))
            .Should().Equal((1, a), (2, b));
        CountAccentPixels(result.Value.PngBytes).Should().BeGreaterThan(20);
    }

    [Fact]
    public async Task Boxes_region_crop_includes_partially_intersecting_box()
    {
        Harness harness = new();
        DocumentBoxId a = harness.AddBox(null, null, new NormalizedBBox(0.05, 0.05, 0.20, 0.10));
        DocumentBoxId b = harness.AddBox(a, null, new NormalizedBBox(0.08, 0.08, 0.05, 0.05));
        // The region crop covers A plus margin (x 0.03..0.27, y 0.03..0.17). This root
        // box starts inside the crop but extends past its right and bottom edges, so
        // it is drawn clamped to the crop; the far box lies fully outside.
        DocumentBoxId partial = harness.AddBox(null, null, new NormalizedBBox(0.20, 0.13, 0.20, 0.20));
        DocumentBoxId far = harness.AddBox(null, null, new NormalizedBBox(0.70, 0.70, 0.20, 0.20));
        harness.LinkSiblings(a, partial);
        harness.LinkSiblings(partial, far);

        // The same page without the partially intersecting box: A and B only are drawn.
        Harness baseline = new();
        DocumentBoxId baseA = baseline.AddBox(null, null, new NormalizedBBox(0.05, 0.05, 0.20, 0.10));
        DocumentBoxId baseB = baseline.AddBox(baseA, null, new NormalizedBBox(0.08, 0.08, 0.05, 0.05));
        DocumentBoxId baseFar = baseline.AddBox(null, null, new NormalizedBBox(0.70, 0.70, 0.20, 0.20));
        baseline.LinkSiblings(baseA, baseFar);

        Result<PageImageViewResult> result = await harness.Service.ViewAsync(
            harness.Request(a, PageImageViewContext.Region, numberedBoxes: true));
        Result<PageImageViewResult> baselineResult = await baseline.Service.ViewAsync(
            baseline.Request(baseA, PageImageViewContext.Region, numberedBoxes: true));

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        baselineResult.IsSuccess.Should().BeTrue(baselineResult.ErrorMessage);
        // Page-global order is A=1, B=2, partial=3, far=4. The partially intersecting
        // box is listed with its global number because it is drawn (clamped); the
        // fully outside box stays unlisted, exactly as in the baseline.
        result.Value.BoxNumbers.Should().NotBeNull();
        result.Value.BoxNumbers!.Select(number => (number.Index, number.BoxId))
            .Should().Equal((1, a), (2, b), (3, partial));
        baselineResult.Value.BoxNumbers.Should().NotBeNull();
        baselineResult.Value.BoxNumbers!.Select(number => (number.Index, number.BoxId))
            .Should().Equal((1, baseA), (2, baseB));
        // The partial box's clamped outline and label add accent pixels on top of the
        // identical A/B drawing, which proves it was drawn, not only listed.
        CountAccentPixels(result.Value.PngBytes)
            .Should().BeGreaterThan(CountAccentPixels(baselineResult.Value.PngBytes));
    }

    [Fact]
    public async Task View_without_boxes_request_has_no_box_numbers_and_no_overlay()
    {
        Harness harness = new();
        harness.AddBox(null, null, new NormalizedBBox(0.05, 0.05, 0.30, 0.10));

        Result<PageImageViewResult> result = await harness.Service.ViewAsync(harness.Request());

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.BoxNumbers.Should().BeNull();
        CountAccentPixels(result.Value.PngBytes).Should().Be(0);
    }

    // ---------- tree-less pages ----------

    [Fact]
    public async Task Treeless_page_view_succeeds_with_empty_revision_and_unpinned_uri()
    {
        Harness harness = new() { CurrentRevision = NotFoundRevision() };

        Result<PageImageViewResult> result = await harness.Service.ViewAsync(
            harness.Request(numberedBoxes: true));

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.UsedRevision.Should().BeEmpty();
        result.Value.CanonicalUri.Should().Be($"patchouli://texts/{harness.DocId}/page-1.xml");
        result.Value.BoxNumbers.Should().NotBeNull().And.BeEmpty();
        result.Value.PngBytes.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Treeless_box_target_is_target_not_found()
    {
        Harness harness = new() { CurrentRevision = NotFoundRevision() };

        Result<PageImageViewResult> result = await harness.Service.ViewAsync(
            harness.Request(DocumentBoxId.New(), PageImageViewContext.Region));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PageImageViewErrorCodes.TargetNotFound);
    }

    [Fact]
    public async Task Explicit_missing_revision_stays_stale_revision()
    {
        Harness harness = new();
        DocumentTreeRevisionId missing = DocumentTreeRevisionId.New();

        Result<PageImageViewResult> result = await harness.Service.ViewAsync(
            harness.Request(revisionId: missing));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PageImageViewErrorCodes.StaleRevision);
    }

    [Fact]
    public async Task Current_revision_lookup_error_is_not_swallowed_into_treeless()
    {
        Harness harness = new()
        {
            CurrentRevision = Result<DocumentTreeRevision>.Failure("storage_unavailable",
                "The tree store is unavailable.")
        };

        Result<PageImageViewResult> result = await harness.Service.ViewAsync(harness.Request());

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("storage_unavailable");
    }

    [Fact]
    public async Task Explicit_existing_revision_is_used_and_pinned()
    {
        Harness harness = new();

        Result<PageImageViewResult> result = await harness.Service.ViewAsync(
            harness.Request(revisionId: harness.RevisionId));

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.UsedRevision.Should().Be(harness.RevisionId.ToString());
        result.Value.CanonicalUri.Should().Contain($"?rev={harness.RevisionId}");
    }

    // ---------- harness ----------

    private static Result<DocumentTreeRevision> NotFoundRevision()
    {
        return Result<DocumentTreeRevision>.Failure(AppErrorCodes.NotFound, "The page has no committed revision.");
    }

    private static int CountAccentPixels(byte[] png)
    {
        using SKBitmap bitmap = SKBitmap.Decode(png);
        int count = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                SKColor pixel = bitmap.GetPixel(x, y);
                if (pixel.Red > 170 && pixel.Green < 120 && pixel.Blue < 120)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private sealed class Harness
    {
        private readonly List<DocumentBox> _boxes = [];

        public Harness()
        {
            Page = new Page(PageId, DocId, 0, "1", 595, 842, 0, "pdfium", 595, 842, "v1", null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            Revision = new DocumentTreeRevision(RevisionId, DocId, PageId, null,
                DocumentTreeRevisionSource.OcrAdopted, DocumentTreeRevisionStatus.Committed, true,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            CurrentRevision = Result<DocumentTreeRevision>.Success(Revision);

            (_, Stub<IPageService> pages) = Stub<IPageService>.Create();
            pages.Handle(nameof(IPageService.ListPagesAsync), _ =>
                Task.FromResult(Result<IReadOnlyList<Page>>.Success([Page])));
            (_, Stub<IDocumentTreeService> trees) = Stub<IDocumentTreeService>.Create();
            trees.Handle(nameof(IDocumentTreeService.GetCurrentRevisionAsync), _ =>
                Task.FromResult(CurrentRevision));
            trees.Handle(nameof(IDocumentTreeService.ListRevisionsAsync), _ =>
                Task.FromResult(Result<IReadOnlyList<DocumentTreeRevision>>.Success([Revision])));
            trees.Handle(nameof(IDocumentTreeService.ListBoxesAsync), _ =>
                Task.FromResult(Result<IReadOnlyList<DocumentBox>>.Success(_boxes.ToArray())));
            (_, Stub<IPageRenderService> renders) = Stub<IPageRenderService>.Create();
            renders.Handle(nameof(IPageRenderService.RenderPreviewAsync), _ =>
            {
                byte[] white = new byte[BufferWidth * BufferHeight * 4];
                Array.Fill(white, (byte)255);
                return Task.FromResult(Result<PdfPagePixelBufferLease>.Success(
                    new PdfPagePixelBufferLease(white, BufferWidth, BufferHeight, BufferWidth * 4, 0,
                        "display", 595, 842, "v1")));
            });
            Service = new PageImageViewService(pages.Proxy, trees.Proxy, renders.Proxy);
        }

        public DocumentInstanceId DocId { get; } = DocumentInstanceId.New();
        public PageId PageId { get; } = PageId.New();
        public DocumentTreeRevisionId RevisionId { get; } = DocumentTreeRevisionId.New();
        public Page Page { get; }
        public DocumentTreeRevision Revision { get; }
        public Result<DocumentTreeRevision> CurrentRevision { get; set; }
        public PageImageViewService Service { get; }

        public DocumentBoxId AddBox(DocumentBoxId? parent, DocumentBoxId? nextSibling, NormalizedBBox bbox,
            bool otherPage = false)
        {
            DocumentBoxId id = DocumentBoxId.New();
            _boxes.Add(new DocumentBox(RevisionId, id, DocId, otherPage ? PageId.New() : PageId,
                parent, nextSibling, DocumentBoxType.Text, null, bbox, new TextBoxPayload("text"),
                null, null, null, false));
            return id;
        }

        public void LinkSiblings(DocumentBoxId first, DocumentBoxId next)
        {
            int index = _boxes.FindIndex(box => box.BoxId == first);
            _boxes[index] = _boxes[index] with { NextSiblingBoxId = next };
        }

        public void ShuffleStorage()
        {
            // Storage order must not matter: chains and parent links define the order.
            _boxes.Reverse();
        }

        public IReadOnlyList<DocumentBox> PageBoxes()
        {
            return _boxes.Where(box => box.PageId == PageId).ToArray();
        }

        public PageImageViewRequest Request(DocumentBoxId? boxId = null,
            PageImageViewContext context = PageImageViewContext.Page,
            DocumentTreeRevisionId? revisionId = null, bool numberedBoxes = false)
        {
            return new PageImageViewRequest(DocId, 1, revisionId, boxId, false, context,
                PageImageViewRequest.DefaultMaxEdge, numberedBoxes);
        }
    }

    /// <summary>Minimal DispatchProxy stub: unhandled methods throw loudly.</summary>
    private class Stub<T> : DispatchProxy where T : class
    {
        private readonly Dictionary<string, Func<object?[], object?>> _handlers = new();

        public T Proxy => (T)(object)this;

        public void Handle(string methodName, Func<object?[], object?> handler)
        {
            _handlers[methodName] = handler;
        }

        public static (T Proxy, Stub<T> Impl) Create()
        {
            T proxy = Create<T, Stub<T>>();
            return (proxy, (Stub<T>)(object)proxy);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is not null &&
                _handlers.TryGetValue(targetMethod.Name, out Func<object?[], object?>? handler))
            {
                return handler(args ?? []);
            }

            throw new NotSupportedException(
                $"Stub method not configured: {targetMethod?.DeclaringType?.Name}.{targetMethod?.Name}");
        }
    }
}
