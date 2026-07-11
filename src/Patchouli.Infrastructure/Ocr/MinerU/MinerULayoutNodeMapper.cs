using Patchouli.Core.Layout;
using Patchouli.Ocr;

namespace Patchouli.Infrastructure.Ocr.MinerU;

internal sealed class MinerULayoutNodeMapper
{
    private int _globalOrder;

    public OcrLayoutDocument MapDocument(
        MinerUContentListDocument document,
        IReadOnlyList<Page> pages)
    {
        _globalOrder = 0;
        List<OcrLayoutPage> mappedPages = new();
        Dictionary<int, Page> pageLookup = pages.ToDictionary(page => page.PageIndex);

        foreach (MinerUContentListPage mineruPage in document.Pages)
        {
            int pageIndex = mineruPage.PageNum - 1;
            if (!pageLookup.TryGetValue(pageIndex, out Page? page))
            {
                continue;
            }

            OcrLayoutBlock[] blocks = mineruPage.Blocks
                .Select(block => MapBlock(block, mineruPage.Width, mineruPage.Height))
                .Where(block => block is not null)
                .Cast<OcrLayoutBlock>()
                .ToArray();
            if (blocks.Length == 0)
            {
                continue;
            }

            mappedPages.Add(new OcrLayoutPage(page.PageId, page.PageIndex, mineruPage.Width, mineruPage.Height,
                blocks));
        }

        return new OcrLayoutDocument(mappedPages);
    }

    private OcrLayoutBlock? MapBlock(
        MinerUContentBlock block,
        double pageWidth,
        double pageHeight)
    {
        (string? nodeType, string textPolicy, string? text) = MapTypeAndText(block);
        if (nodeType is null)
        {
            return null;
        }

        NormalizedBBox? bbox = ToNormalizedBBox(block.Bbox, pageWidth, pageHeight);
        int readingOrder = Interlocked.Increment(ref _globalOrder);
        if (nodeType != LayoutNodeType.Table)
        {
            return new OcrLayoutBlock(
                nodeType,
                textPolicy,
                readingOrder,
                text,
                block.LaTex,
                bbox,
                block.Confidence);
        }

        IReadOnlyList<MinerUTableCell> cells = (block.TableCells is { Count: > 0 } ? block.TableCells : block.Cells) ??
                                               [];
        if (cells.Count == 0)
        {
            return new OcrLayoutBlock(
                nodeType,
                textPolicy,
                readingOrder,
                text,
                block.LaTex,
                bbox,
                block.Confidence);
        }

        OcrLayoutBlock[] rowBlocks = cells
            .Where(cell => cell.RowIndex is not null && cell.ColIndex is not null)
            .GroupBy(cell => cell.RowIndex!.Value)
            .OrderBy(group => group.Key)
            .Select(group => new OcrLayoutBlock(
                LayoutNodeType.TableRow,
                TextPolicy.AggregateChildren,
                Interlocked.Increment(ref _globalOrder),
                Children: group
                    .OrderBy(cell => cell.ColIndex)
                    .Select(cell => new OcrLayoutBlock(
                        LayoutNodeType.TableCell,
                        TextPolicy.Own,
                        Interlocked.Increment(ref _globalOrder),
                        cell.Text,
                        BBox: ToNormalizedBBox(cell.Bbox, pageWidth, pageHeight),
                        TableCell: new OcrTableCell(
                            group.Key,
                            cell.ColIndex!.Value,
                            cell.RowSpan ?? 1,
                            cell.ColSpan ?? 1,
                            cell.IsHeader ?? group.Key == 0)))
                    .ToArray()))
            .ToArray();

        return new OcrLayoutBlock(
            LayoutNodeType.Table,
            TextPolicy.AggregateChildren,
            readingOrder,
            BBox: bbox,
            Confidence: block.Confidence,
            Children: rowBlocks);
    }

    private static NormalizedBBox? ToNormalizedBBox(double[]? bbox, double pageWidth, double pageHeight)
    {
        if (bbox is not { Length: 4 } || pageWidth <= 0 || pageHeight <= 0)
        {
            return null;
        }

        double x = Math.Clamp(bbox[0] / pageWidth, 0, 1);
        double y = Math.Clamp(bbox[1] / pageHeight, 0, 1);
        double w = Math.Clamp((bbox[2] - bbox[0]) / pageWidth, 0, 1 - x);
        double h = Math.Clamp((bbox[3] - bbox[1]) / pageHeight, 0, 1 - y);
        return new NormalizedBBox(x, y, w, h);
    }

    private static (string? NodeType, string TextPolicy, string? Text) MapTypeAndText(
        MinerUContentBlock block)
    {
        string blockType = block.Type?.ToLowerInvariant() ?? "";
        string? text = block.Text;
        string? latex = block.LaTex;

        return blockType switch
        {
            "text" or "paragraph" => (LayoutNodeType.Paragraph, TextPolicy.Own, text),
            "title" or "heading" => (LayoutNodeType.Heading, TextPolicy.Own, text),
            "table" => (LayoutNodeType.Table, TextPolicy.Own, text ?? latex),
            "formula" or "equation" when !string.IsNullOrWhiteSpace(latex) => (LayoutNodeType.Paragraph, TextPolicy.Own,
                latex),
            "formula" or "equation" => (LayoutNodeType.Paragraph, TextPolicy.Own, text),
            "image" or "figure" when string.IsNullOrWhiteSpace(text) => (null, TextPolicy.None, null),
            "image" or "figure" => (LayoutNodeType.Paragraph, TextPolicy.Own, text),
            "page_header" or "header" => (LayoutNodeType.Header, TextPolicy.Own, text),
            "page_footer" or "footer" => (LayoutNodeType.Footer, TextPolicy.Own, text),
            "page_number" => (LayoutNodeType.PageNumber, TextPolicy.Own, text),
            "footnote" => (LayoutNodeType.Footnote, TextPolicy.Own, text),
            "discarded" or "ignore" => (null, TextPolicy.None, null),
            _ => (LayoutNodeType.Paragraph, TextPolicy.Own, text ?? latex)
        };
    }
}
