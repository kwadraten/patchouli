using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Ocr.MinerU;

namespace Patchouli.Infrastructure.Ocr.MinerU;

internal sealed record MappedLayoutNode(
    LayoutNodeId NodeId,
    DocumentInstanceId DocumentInstanceId,
    PageId PageId,
    LayoutNodeId? ParentNodeId,
    string NodeType,
    NormalizedBBox? BBox,
    string? OwnText,
    string TextPolicy,
    int ReadingOrder,
    LayoutRevisionId RevisionId,
    int? RowIndex = null,
    int? ColIndex = null,
    int? RowSpan = null,
    int? ColSpan = null,
    bool IsHeader = false);

internal sealed class MinerULayoutNodeMapper
{
    private int _globalOrder;

    public IReadOnlyList<MappedLayoutNode> MapDocument(
        MinerUContentListDocument document,
        DocumentInstanceId documentInstanceId,
        LayoutRevisionId revisionId,
        IReadOnlyList<Core.Layout.Page> pages)
    {
        _globalOrder = 0;
        var nodes = new List<MappedLayoutNode>();
        var pageLookup = pages.ToDictionary(p => p.PageIndex);

        foreach (var mineruPage in document.Pages)
        {
            var pageIndex = mineruPage.PageNum - 1;
            if (!pageLookup.TryGetValue(pageIndex, out var page))
                continue;

            foreach (var block in mineruPage.Blocks)
            {
                nodes.AddRange(MapBlock(block, documentInstanceId, page.PageId, revisionId, mineruPage.Width, mineruPage.Height));
            }
        }

        return nodes;
    }

    private IReadOnlyList<MappedLayoutNode> MapBlock(
        MinerUContentBlock block,
        DocumentInstanceId documentInstanceId,
        PageId pageId,
        LayoutRevisionId revisionId,
        double pageWidth,
        double pageHeight)
    {
        var (nodeType, textPolicy, text) = MapTypeAndText(block);

        if (nodeType is null)
            return [];

        NormalizedBBox? bbox = null;
        if (block.Bbox is { Length: 4 } && pageWidth > 0 && pageHeight > 0)
        {
            var x = Math.Clamp(block.Bbox[0] / pageWidth, 0, 1);
            var y = Math.Clamp(block.Bbox[1] / pageHeight, 0, 1);
            var w = Math.Clamp((block.Bbox[2] - block.Bbox[0]) / pageWidth, 0, 1 - x);
            var h = Math.Clamp((block.Bbox[3] - block.Bbox[1]) / pageHeight, 0, 1 - y);
            bbox = new NormalizedBBox(x, y, w, h);
        }

        var order = Interlocked.Increment(ref _globalOrder);
        var nodeId = LayoutNodeId.New();

        var root = new MappedLayoutNode(
            nodeId,
            documentInstanceId,
            pageId,
            null,
            nodeType,
            bbox,
            text,
            textPolicy,
            order,
            revisionId);

        if (nodeType != LayoutNodeType.Table)
            return [root];

        var cells = (block.TableCells is { Count: > 0 } ? block.TableCells : block.Cells) ?? [];
        if (cells.Count == 0)
            return [root];

        var nodes = new List<MappedLayoutNode>
        {
            root with { TextPolicy = TextPolicy.AggregateChildren, OwnText = null }
        };
        var rowParents = new Dictionary<int, LayoutNodeId>();
        foreach (var cell in cells.Where(c => c.RowIndex is not null && c.ColIndex is not null).OrderBy(c => c.RowIndex).ThenBy(c => c.ColIndex))
        {
            var rowIndex = cell.RowIndex!.Value;
            if (!rowParents.TryGetValue(rowIndex, out var rowId))
            {
                rowId = LayoutNodeId.New();
                rowParents[rowIndex] = rowId;
                nodes.Add(new MappedLayoutNode(
                    rowId,
                    documentInstanceId,
                    pageId,
                    nodeId,
                    LayoutNodeType.TableRow,
                    null,
                    null,
                    TextPolicy.AggregateChildren,
                    Interlocked.Increment(ref _globalOrder),
                    revisionId));
            }

            nodes.Add(new MappedLayoutNode(
                LayoutNodeId.New(),
                documentInstanceId,
                pageId,
                rowId,
                LayoutNodeType.TableCell,
                ToNormalizedBBox(cell.Bbox, pageWidth, pageHeight),
                cell.Text,
                TextPolicy.Own,
                Interlocked.Increment(ref _globalOrder),
                revisionId,
                rowIndex,
                cell.ColIndex,
                cell.RowSpan ?? 1,
                cell.ColSpan ?? 1,
                cell.IsHeader ?? rowIndex == 0));
        }

        return nodes;
    }

    private static NormalizedBBox? ToNormalizedBBox(double[]? bbox, double pageWidth, double pageHeight)
    {
        if (bbox is not { Length: 4 } || pageWidth <= 0 || pageHeight <= 0)
            return null;
        var x = Math.Clamp(bbox[0] / pageWidth, 0, 1);
        var y = Math.Clamp(bbox[1] / pageHeight, 0, 1);
        var w = Math.Clamp((bbox[2] - bbox[0]) / pageWidth, 0, 1 - x);
        var h = Math.Clamp((bbox[3] - bbox[1]) / pageHeight, 0, 1 - y);
        return new NormalizedBBox(x, y, w, h);
    }

    private static (string? NodeType, string TextPolicy, string? Text) MapTypeAndText(
        MinerUContentBlock block)
    {
        var blockType = block.Type?.ToLowerInvariant() ?? "";
        var text = block.Text;
        var latex = block.LaTex;

        return blockType switch
        {
            "text" or "paragraph" => (LayoutNodeType.Paragraph, TextPolicy.Own, text),
            "title" or "heading" => (LayoutNodeType.Heading, TextPolicy.Own, text),
            "table" => (LayoutNodeType.Table, TextPolicy.Own, text ?? latex),
            "formula" or "equation" when !string.IsNullOrWhiteSpace(latex) => (LayoutNodeType.Paragraph, TextPolicy.Own, latex),
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
