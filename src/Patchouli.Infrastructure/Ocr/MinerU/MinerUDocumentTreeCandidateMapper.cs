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

            List<OcrBoxCandidate> boxes = [];
            int sourceOrder = 0;
            foreach (MinerUContentBlock block in minerUPage.Blocks)
            {
                OcrBoxCandidate? mapped = MapBlock(
                    block, page, minerUPage.Width, minerUPage.Height, sourceOrder++, false, diagnostics);
                if (mapped is not null)
                {
                    boxes.Add(mapped);
                }
            }

            foreach (MinerUContentBlock block in minerUPage.DiscardedBlocks ?? [])
            {
                OcrBoxCandidate? mapped = MapBlock(
                    block, page, minerUPage.Width, minerUPage.Height, sourceOrder++, true, diagnostics);
                if (mapped is not null)
                {
                    boxes.Add(mapped);
                }
            }

            if (boxes.Count > 0)
            {
                mappedPages.Add(new OcrPageCandidate(page.PageId, page.PageIndex, boxes));
            }
        }

        return new OcrDocumentTreeCandidate(mappedPages, diagnostics);
    }

    private static OcrBoxCandidate? MapBlock(
        MinerUContentBlock block,
        Page page,
        double pageWidth,
        double pageHeight,
        int sourceOrder,
        bool discarded,
        List<OcrDiagnostic> diagnostics)
    {
        NormalizedBBox? bbox = ToNormalizedBBox(block.Bbox, pageWidth, pageHeight);
        if (bbox is null || bbox.Value.Validate().IsFailure)
        {
            diagnostics.Add(new OcrDiagnostic(
                "bbox_invalid",
                "MinerU box bbox could not be normalized to the physical page.",
                page.PageId,
                sourceOrder,
                true));
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

        return (DocumentBoxType.Table, null, null, new TableBoxPayload(gfm), null, false);
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
