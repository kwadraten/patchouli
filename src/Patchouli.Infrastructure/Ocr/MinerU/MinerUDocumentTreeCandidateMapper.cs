using Patchouli.Core.Documents;
using Patchouli.Core.Layout;
using Patchouli.Ocr;

namespace Patchouli.Infrastructure.Ocr.MinerU;

internal sealed class MinerUDocumentTreeCandidateMapper
{
    public OcrDocumentTreeCandidate MapDocument(
        MinerUContentListDocument document,
        IReadOnlyList<Page> pages)
    {
        List<OcrPageCandidate> mappedPages = [];
        List<OcrDiagnostic> diagnostics = [];
        Dictionary<int, Page> pageLookup = pages.ToDictionary(page => page.PageIndex);

        foreach (MinerUContentListPage minerUPage in document.Pages)
        {
            int pageIndex = minerUPage.PageNum - 1;
            if (!pageLookup.TryGetValue(pageIndex, out Page? page))
            {
                continue;
            }

            List<OcrBoxCandidate> boxes = MapPageBoxes(minerUPage, page, null, false, diagnostics);
            mappedPages.Add(new OcrPageCandidate(page.PageId, page.PageIndex, boxes));
        }

        return new OcrDocumentTreeCandidate(LinkContinuations(mappedPages, diagnostics), diagnostics);
    }

    public (OcrPageCandidate Page, IReadOnlyList<OcrDiagnostic> Diagnostics) MapImagePage(
        MinerUContentListPage contentPage,
        Page page,
        NormalizedBBox? region)
    {
        List<OcrDiagnostic> diagnostics = [];
        List<OcrBoxCandidate> boxes = MapPageBoxes(contentPage, page, region, true, diagnostics);
        OcrPageCandidate candidate = LinkContinuations(
            [new OcrPageCandidate(page.PageId, page.PageIndex, boxes)], diagnostics)[0];
        return (candidate, diagnostics);
    }

    // MinerU keeps the full text of a paragraph split across pages or columns in the first
    // box and emits the remaining regions as paragraph blocks with empty content. Link each
    // empty region back to the box that holds the text so the workspace can draw the dashed
    // connection instead of showing a mysterious empty box.
    private static IReadOnlyList<OcrPageCandidate> LinkContinuations(
        IReadOnlyList<OcrPageCandidate> pages,
        List<OcrDiagnostic> diagnostics)
    {
        List<OcrBoxCandidate>[] boxesByPage = pages.Select(page => page.Boxes.ToList()).ToArray();
        (int Page, int Index)? headPosition = null;

        for (int pageIndex = 0; pageIndex < boxesByPage.Length; pageIndex++)
        {
            List<OcrBoxCandidate> boxes = boxesByPage[pageIndex];
            for (int boxIndex = 0; boxIndex < boxes.Count; boxIndex++)
            {
                OcrBoxCandidate box = boxes[boxIndex];
                if (IsContinuationRegion(box))
                {
                    if (headPosition is null)
                    {
                        continue;
                    }

                    OcrBoxCandidate head = boxesByPage[headPosition.Value.Page][headPosition.Value.Index];
                    if (head.PreassignedBoxId is null)
                    {
                        head = head with { PreassignedBoxId = Core.Ids.DocumentBoxId.New() };
                        boxesByPage[headPosition.Value.Page][headPosition.Value.Index] = head;
                    }

                    boxes[boxIndex] = box with { ContinuesFromBoxId = head.PreassignedBoxId };
                    diagnostics.Add(new OcrDiagnostic(
                        "paragraph_continuation_linked",
                        "MinerU paragraph continuation region was linked to the box holding its text.",
                        pages[pageIndex].PageId,
                        box.SourceOrder));
                    continue;
                }

                if (IsContinuationHead(box))
                {
                    headPosition = (pageIndex, boxIndex);
                }
            }
        }

        return pages.Select((page, index) => page with { Boxes = boxesByPage[index] }).ToArray();
    }

    private static bool IsContinuationRegion(OcrBoxCandidate box)
    {
        return box.BoxType == DocumentBoxType.Text && !box.Suppressed &&
               box.Payload is TextBoxPayload text && string.IsNullOrWhiteSpace(text.Markdown);
    }

    private static bool IsContinuationHead(OcrBoxCandidate box)
    {
        return box.BoxType == DocumentBoxType.Text && !box.Suppressed &&
               box.Payload is TextBoxPayload text && !string.IsNullOrWhiteSpace(text.Markdown);
    }

    private static List<OcrBoxCandidate> MapPageBoxes(
        MinerUContentListPage minerUPage,
        Page page,
        NormalizedBBox? region,
        bool skipMissingBBox,
        List<OcrDiagnostic> diagnostics)
    {
        List<OcrBoxCandidate> boxes = [];
        int sourceOrder = 0;
        foreach (MinerUContentBlock block in minerUPage.Blocks)
        {
            OcrBoxCandidate? mapped = MapBlock(
                block, page, minerUPage.Width, minerUPage.Height, sourceOrder++, false, skipMissingBBox, region,
                diagnostics);
            if (mapped is not null)
            {
                boxes.Add(mapped);
            }
        }

        foreach (MinerUContentBlock block in minerUPage.DiscardedBlocks ?? [])
        {
            OcrBoxCandidate? mapped = MapBlock(
                block, page, minerUPage.Width, minerUPage.Height, sourceOrder++, true, skipMissingBBox, region,
                diagnostics);
            if (mapped is not null)
            {
                boxes.Add(mapped);
            }
        }

        boxes = MergeTextContainedByImages(boxes, page.PageId, diagnostics);

        if (boxes.Count == 0)
        {
            boxes.Add(new OcrBoxCandidate(
                DocumentBoxType.LogicalPage,
                null,
                null,
                0,
                new TextBoxPayload("Blank page (MinerU returned no content)."),
                region ?? new NormalizedBBox(0, 0, 1, 1),
                null,
                null,
                false));
            diagnostics.Add(new OcrDiagnostic(
                "blank_page_placeholder",
                "MinerU returned no content for this physical page; a logical-page placeholder was created.",
                page.PageId));
        }

        return boxes;
    }

    private static List<OcrBoxCandidate> MergeTextContainedByImages(
        IReadOnlyList<OcrBoxCandidate> boxes,
        Core.Ids.PageId pageId,
        List<OcrDiagnostic> diagnostics)
    {
        OcrBoxCandidate[] images = boxes
            .Where(box => box.BoxType == DocumentBoxType.Image && !box.Suppressed && box.Payload is MediaBoxPayload)
            .ToArray();
        if (images.Length == 0)
        {
            return boxes.ToList();
        }

        Dictionary<OcrBoxCandidate, List<OcrBoxCandidate>> containedByImage = [];
        foreach (OcrBoxCandidate text in boxes.Where(box =>
                     box.BoxType == DocumentBoxType.Text && !box.Suppressed && box.Payload is TextBoxPayload))
        {
            OcrBoxCandidate? image = images
                .Where(candidate => Contains(candidate.BBox, text.BBox))
                .MinBy(candidate => candidate.BBox.Width * candidate.BBox.Height);
            if (image is null)
            {
                continue;
            }

            if (!containedByImage.TryGetValue(image, out List<OcrBoxCandidate>? contained))
            {
                contained = [];
                containedByImage[image] = contained;
            }

            contained.Add(text);
            diagnostics.Add(new OcrDiagnostic(
                "image_embedded_text_merged",
                "MinerU text fully contained by an image was imported as the image description.",
                pageId,
                text.SourceOrder));
        }

        if (containedByImage.Count == 0)
        {
            return boxes.ToList();
        }

        HashSet<OcrBoxCandidate> mergedText = containedByImage.Values.SelectMany(values => values).ToHashSet();
        return boxes
            .Where(box => !mergedText.Contains(box))
            .Select(box => containedByImage.TryGetValue(box, out List<OcrBoxCandidate>? contained)
                ? box with
                {
                    Payload = MergeImageDescription(
                        (MediaBoxPayload)box.Payload,
                        contained.OrderBy(text => text.SourceOrder)
                            .Select(text => ((TextBoxPayload)text.Payload).Markdown))
                }
                : box)
            .ToList();
    }

    private static MediaBoxPayload MergeImageDescription(MediaBoxPayload image, IEnumerable<string> text)
    {
        string[] parts = text
            .Prepend(image.Description ?? string.Empty)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToArray();
        return image with { Description = parts.Length == 0 ? null : string.Join("\n", parts) };
    }

    private static bool Contains(NormalizedBBox container, NormalizedBBox contained)
    {
        const double tolerance = 1e-9;
        return contained.X >= container.X - tolerance &&
               contained.Y >= container.Y - tolerance &&
               contained.X + contained.Width <= container.X + container.Width + tolerance &&
               contained.Y + contained.Height <= container.Y + container.Height + tolerance;
    }

    private static OcrBoxCandidate? MapBlock(
        MinerUContentBlock block,
        Page page,
        double pageWidth,
        double pageHeight,
        int sourceOrder,
        bool discarded,
        bool skipMissingBBox,
        NormalizedBBox? region,
        List<OcrDiagnostic> diagnostics)
    {
        NormalizedBBox? bbox = ToNormalizedBBox(block.Bbox, pageWidth, pageHeight);
        if (bbox is null)
        {
            diagnostics.Add(skipMissingBBox
                ? new OcrDiagnostic(
                    "bbox_missing_skipped",
                    "MinerU image block without a bbox was skipped.",
                    page.PageId,
                    sourceOrder)
                : new OcrDiagnostic(
                    "bbox_invalid",
                    "MinerU box bbox could not be normalized to the physical page.",
                    page.PageId,
                    sourceOrder,
                    true));
            return null;
        }

        if (region is not null)
        {
            bbox = ScaleIntoRegion(bbox.Value, region.Value);
        }

        if (bbox.Value.Validate().IsFailure)
        {
            diagnostics.Add(new OcrDiagnostic(
                "bbox_invalid",
                "MinerU box bbox could not be normalized to the physical page.",
                page.PageId,
                sourceOrder,
                !skipMissingBBox));
            return null;
        }

        string originalType = (block.Type ?? "text").Trim().ToLowerInvariant();
        if (originalType is "phonetic" or "ruby")
        {
            diagnostics.Add(new OcrDiagnostic(
                "phonetic_flattened",
                "MinerU phonetic content was flattened into ordinary text.",
                page.PageId,
                sourceOrder));
            originalType = DocumentBoxType.Text;
        }

        (string boxType, string? subType, string? baseType, DocumentBoxPayload payload, int? headingLevel,
            bool auxiliary) = MapType(block, originalType, diagnostics, page.PageId, sourceOrder);
        return new OcrBoxCandidate(
            boxType,
            subType,
            baseType,
            sourceOrder,
            payload,
            bbox.Value,
            headingLevel,
            block.Confidence,
            discarded || auxiliary);
    }

    private static (string BoxType, string? SubType, string? BaseType, DocumentBoxPayload Payload,
        int? HeadingLevel, bool Auxiliary) MapType(
            MinerUContentBlock block,
            string originalType,
            List<OcrDiagnostic> diagnostics,
            Core.Ids.PageId pageId,
            int sourceOrder)
    {
        string text = (block.Text ?? block.LaTex ?? string.Empty).Trim();
        return originalType switch
        {
            "text" or "paragraph" => Text(DocumentBoxType.Text, text),
            "title" or "heading" => (
                DocumentBoxType.Title,
                null,
                null,
                new TextBoxPayload(text),
                Math.Clamp(block.HeadingLevel ?? 1, 1, 6),
                false),
            "ref_text" => Text(DocumentBoxType.RefText, text),
            "formula" or "equation" => (
                DocumentBoxType.Equation,
                null,
                null,
                new EquationBoxPayload((block.LaTex ?? block.Text ?? string.Empty).Trim()),
                null,
                false),
            "list" => (
                DocumentBoxType.List,
                null,
                null,
                new ListBoxPayload(text),
                null,
                false),
            "table" => MapTable(block, diagnostics, pageId, sourceOrder),
            "image" or "figure" => (
                DocumentBoxType.Image,
                null,
                null,
                new MediaBoxPayload(null, NullIfWhiteSpace(text)),
                null,
                false),
            "chart" => (
                DocumentBoxType.Chart,
                null,
                null,
                new MediaBoxPayload(null, NullIfWhiteSpace(text)),
                null,
                false),
            "code" => (
                DocumentBoxType.Code,
                null,
                null,
                new CodeBoxPayload(text),
                null,
                false),
            "algorithm" => (
                DocumentBoxType.Code,
                DocumentBoxType.Algorithm,
                null,
                new CodeBoxPayload(text),
                null,
                false),
            "page_header" or "header" => Text(DocumentBoxType.Header, text, true),
            "page_footer" or "footer" => Text(DocumentBoxType.Footer, text, true),
            "page_number" => Text(DocumentBoxType.PageNumber, text, true),
            "aside_text" or "aside" => Text(DocumentBoxType.AsideText, text, true),
            "footnote" or "page_footnote" => Text(DocumentBoxType.PageFootnote, text, true),
            "image_caption" => Text(DocumentBoxType.ImageCaption, text),
            "image_footnote" => Text(DocumentBoxType.ImageFootnote, text),
            "table_caption" => Text(DocumentBoxType.TableCaption, text),
            "table_footnote" => Text(DocumentBoxType.TableFootnote, text),
            "chart_caption" => Text(DocumentBoxType.ChartCaption, text),
            "chart_footnote" => Text(DocumentBoxType.ChartFootnote, text),
            "code_caption" => Text(DocumentBoxType.CodeCaption, text),
            "code_footnote" => Text(DocumentBoxType.CodeFootnote, text),
            _ => (
                originalType,
                null,
                "text",
                new TextBoxPayload(text),
                null,
                false)
        };
    }

    private static (string, string?, string?, DocumentBoxPayload, int?, bool) MapTable(
        MinerUContentBlock block,
        List<OcrDiagnostic> diagnostics,
        Core.Ids.PageId pageId,
        int sourceOrder)
    {
        IReadOnlyList<MinerUTableCell> cells = block.TableCells is { Count: > 0 }
            ? block.TableCells
            : block.Cells ?? [];
        string? gfm = TryBuildGfmTable(cells);
        if (gfm is null)
        {
            diagnostics.Add(new OcrDiagnostic(
                "table_not_representable_as_gfm",
                "MinerU table could not be losslessly normalized to a regular GFM pipe table.",
                pageId,
                sourceOrder));
            gfm = "[Table]";
        }

        return (DocumentBoxType.Table, null, null,
            new TableBoxPayload(gfm, gfm == "[Table]" ? block.TableHtml : null), null, false);
    }

    private static string? TryBuildGfmTable(IReadOnlyList<MinerUTableCell> cells)
    {
        if (cells.Count == 0 || cells.Any(cell =>
                cell.RowIndex is null || cell.ColIndex is null || (cell.RowSpan ?? 1) != 1 ||
                (cell.ColSpan ?? 1) != 1))
        {
            return null;
        }

        int maxRow = cells.Max(cell => cell.RowIndex!.Value);
        int maxColumn = cells.Max(cell => cell.ColIndex!.Value);
        if (maxRow < 1 || maxColumn < 0)
        {
            return null;
        }

        Dictionary<(int Row, int Column), MinerUTableCell> grid = new();
        foreach (MinerUTableCell cell in cells)
        {
            if (!grid.TryAdd((cell.RowIndex!.Value, cell.ColIndex!.Value), cell))
            {
                return null;
            }
        }

        for (int row = 0; row <= maxRow; row++)
        for (int column = 0; column <= maxColumn; column++)
        {
            if (!grid.ContainsKey((row, column)))
            {
                return null;
            }
        }

        List<string> lines = [Row(0), "| " + string.Join(" | ", Enumerable.Repeat("---", maxColumn + 1)) + " |"];
        for (int row = 1; row <= maxRow; row++)
        {
            lines.Add(Row(row));
        }

        return string.Join("\n", lines);

        string Row(int row)
        {
            return "| " + string.Join(" | ", Enumerable.Range(0, maxColumn + 1)
                .Select(column => EscapeCell(grid[(row, column)].Text))) + " |";
        }
    }

    private static string EscapeCell(string? text)
    {
        return (text ?? string.Empty).Replace("\\", "\\\\").Replace("|", "\\|")
            .Replace("\r", " ").Replace("\n", " ").Trim();
    }

    private static NormalizedBBox? ToNormalizedBBox(double[]? bbox, double pageWidth, double pageHeight)
    {
        if (bbox is not { Length: 4 } || pageWidth <= 0 || pageHeight <= 0)
        {
            return null;
        }

        double x = bbox[0] / pageWidth;
        double y = bbox[1] / pageHeight;
        double width = (bbox[2] - bbox[0]) / pageWidth;
        double height = (bbox[3] - bbox[1]) / pageHeight;
        return new NormalizedBBox(x, y, width, height);
    }

    private static NormalizedBBox ScaleIntoRegion(NormalizedBBox image, NormalizedBBox region)
    {
        double x = region.X + image.X * region.Width;
        double y = region.Y + image.Y * region.Height;
        double width = Math.Max(0, Math.Min(image.Width * region.Width, 1 - x));
        double height = Math.Max(0, Math.Min(image.Height * region.Height, 1 - y));
        return new NormalizedBBox(x, y, width, height);
    }

    private static (string, string?, string?, DocumentBoxPayload, int?, bool) Text(
        string type,
        string text,
        bool auxiliary = false)
    {
        return (type, null, null, new TextBoxPayload(text), null, auxiliary);
    }

    private static string? NullIfWhiteSpace(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
