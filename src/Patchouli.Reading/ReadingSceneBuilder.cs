using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;

namespace Patchouli.Reading;

/// <summary>
/// Pure projection from a compiled markdown model (plus its source map and the page's boxes) to a
/// <see cref="ReadingScene"/> snapshot. No Avalonia media types, no service calls — fully
/// unit-testable headless. Media (image/chart) blocks carry their normalized page region plus an
/// image key (the owning page) so the view draws the region as a source sub-rect of the page's
/// rendered pixel buffer, whether or not <c>MediaBoxPayload.AssetId</c> is present.
/// </summary>
public static class ReadingSceneBuilder
{
    // Semantics:
    // - One ReadingBlock per MarkdownBlock in model.Blocks, in order.
    // - BoxId: from sourceMap — the entry whose [PreviewNodeStart, PreviewNodeStart+PreviewNodeCount)
    //   range contains the block index; null when no entry matches.
    // - Kind: the box's BoxType when BoxId resolves to a known box, otherwise the MarkdownBlock.Kind.
    //   Map DocumentBoxType.Text/RefText to "paragraph", Title to "heading", Image/Chart to "media";
    //   List stays "list", Code/Algorithm stay "code", Equation stays "equation", Table stays "table".
    //   Caption/footnote/header/footer kinds pass through as "paragraph".
    // - Level: heading level from the box's HeadingLevel ?? block.Level.
    // - Table: for Kind == "table", parse the block's slice of <paramref name="markdownSource"/> as
    //   a GFM pipe table into ReadingTable. MarkdownBlock.Text is the block's *plain* text (a pipe
    //   table flattens to space-separated cells), so the source slice is required for a real grid.
    //   A [Table] placeholder (a complex table GFM cannot represent) falls back to the box's stored
    //   HTML table, so such a table still renders as a grid instead of the literal placeholder.
    // - Images: MediaLabel is "图像" for image, "图表" for chart and "公式" for equation; Image is
    //   the box's normalized BBox region keyed by imageKey, for drawing a sub-rect of the page
    //   bitmap. Blocks other than media/equations carry a null Image even if their payload is a media payload.
    // - CodeLanguage: the box's CodeLanguage when present.
    // - Inlines: block.Inlines passed through (except tables, which render from Table).
    public static ReadingScene Build(
        MarkdownDocumentModel model,
        IReadOnlyList<MarkdownSourceMapEntry> sourceMap,
        IReadOnlyList<DocumentBox> boxes,
        string imageKey,
        int pageIndex = -1,
        string? markdownSource = null)
    {
        if (model.Blocks.Count == 0)
        {
            return ReadingScene.Empty;
        }

        Dictionary<DocumentBoxId, DocumentBox> boxesById = [];
        foreach (DocumentBox box in boxes)
        {
            boxesById.TryAdd(box.BoxId, box);
        }

        List<ReadingBlock> blocks = new(model.Blocks.Count);
        HashSet<DocumentBoxId> payloadTablesBuilt = [];
        HashSet<DocumentBoxId> annotatedBodiesBuilt = [];
        for (int index = 0; index < model.Blocks.Count; index++)
        {
            MarkdownBlock block = model.Blocks[index];
            DocumentBoxId[] sourceBoxIds = FindBoxIds(sourceMap, index);
            DocumentBoxId? boxId = sourceBoxIds.Length > 0 ? sourceBoxIds[0] : null;
            DocumentBox? box = boxId is { } id && boxesById.TryGetValue(id, out DocumentBox? resolved)
                ? resolved
                : null;
            string kind = box?.Payload?.Annotations is { Count: > 0 } && !annotatedBodiesBuilt.Add(box.BoxId)
                ? block.Kind
                : KindFor(box, block.Kind);
            if (kind != "table" && IsInlineHtmlTable(block, box, markdownSource))
            {
                // Whole-book reading compiles a complex table as inline HTML, and Markdig reports
                // that block as HtmlBlock without a source-map entry in some paths. The content is
                // authoritative: a table box (or an unmapped block) whose markdown slice is a
                // <table> must read as a grid, not as the raw markup.
                kind = "table";
            }

            bool isTable = kind == "table";
            bool hasImage = kind is "media" or "equation";
            ReadingTable? table = isTable
                ? ParseTableBlock(block, box, boxId, markdownSource, payloadTablesBuilt)
                : null;
            blocks.Add(new ReadingBlock(
                boxId,
                kind,
                box?.HeadingLevel ?? block.Level,
                block.Text,
                isTable ? null : block.Inlines,
                table,
                hasImage ? MediaLabelFor(box) : null,
                box?.CodeLanguage,
                hasImage ? MediaImageFor(box, imageKey) : null,
                pageIndex)
            {
                SourceBoxIds = sourceBoxIds
            });
        }

        return new ReadingScene(blocks);
    }

    private static DocumentBoxId[] FindBoxIds(IReadOnlyList<MarkdownSourceMapEntry> sourceMap, int index)
    {
        return sourceMap.Where(entry => index >= entry.PreviewNodeStart &&
                                        index < entry.PreviewNodeStart + entry.PreviewNodeCount)
            .Select(entry => entry.BoxId).Distinct().ToArray();
    }

    // Box types describe the authored leaf; reading kinds describe how to draw it. Auxiliary
    // text leaves (captions, footnotes, running heads) read as plain paragraphs, and a box type
    // the reading view does not know keeps the markdown block's own kind.
    private static string KindFor(DocumentBox? box, string fallback)
    {
        if (box?.Payload is AlgorithmBoxPayload)
        {
            return fallback;
        }

        return box?.BoxType switch
        {
            DocumentBoxType.Text or DocumentBoxType.RefText => "paragraph",
            DocumentBoxType.Title => "heading",
            DocumentBoxType.Image or DocumentBoxType.Chart => "media",
            DocumentBoxType.List => "list",
            DocumentBoxType.Code or DocumentBoxType.Algorithm => "code",
            DocumentBoxType.Equation or DocumentBoxType.LegacyEquationInterline => "equation",
            DocumentBoxType.Table => "table",
            DocumentBoxType.ImageCaption or DocumentBoxType.ImageFootnote or
                DocumentBoxType.TableCaption or DocumentBoxType.TableFootnote or
                DocumentBoxType.ChartCaption or DocumentBoxType.ChartFootnote or
                DocumentBoxType.CodeCaption or DocumentBoxType.CodeFootnote or
                DocumentBoxType.Header or DocumentBoxType.Footer or
                DocumentBoxType.PageNumber or DocumentBoxType.AsideText or
                DocumentBoxType.PageFootnote => "paragraph",
            _ => fallback
        };
    }

    private static string MediaLabelFor(DocumentBox? box)
    {
        return box?.BoxType switch
        {
            DocumentBoxType.Chart => "图表",
            DocumentBoxType.Equation or DocumentBoxType.LegacyEquationInterline => "公式",
            _ => "图像"
        };
    }

    // The media region travels the box's normalized BBox (not its asset id) so the view can draw
    // the region straight out of the page's rendered pixel buffer even when MediaBoxPayload.AssetId
    // is absent. The key names the owning page bitmap the host resolves.
    private static ReadingImageRegion? MediaImageFor(DocumentBox? box, string imageKey)
    {
        if (box is null || string.IsNullOrEmpty(imageKey))
        {
            return null;
        }

        NormalizedBBox region = box.BBox;
        return region.Width > 0 && region.Height > 0
            ? new ReadingImageRegion(imageKey, region)
            : null;
    }

    // Resolves a table block's grid from whichever payload its compile produced:
    // - GFM: the block's markdown slice is the pipe table itself.
    // - Complex: the table is not representable as GFM, so the box keeps the source <table> HTML.
    //   The sidebar preview compiles it as the [Table] placeholder (the HTML rides on the box
    //   payload), while whole-book reading compiles the HTML inline instead — both must read as a
    //   grid, not as raw markup.
    private static ReadingTable? ParseTableBlock(
        MarkdownBlock block,
        DocumentBox? box,
        DocumentBoxId? boxId,
        string? markdownSource,
        HashSet<DocumentBoxId> payloadTablesBuilt)
    {
        string tableMarkdown = TableMarkdown(block, markdownSource);
        if (LooksLikeHtmlTableMarkup(tableMarkdown))
        {
            // An inline <table> always carries its own rows; a fragment without any stays raw text
            // instead of being forced into a one-column grid.
            return tableMarkdown.Contains("<tr", StringComparison.OrdinalIgnoreCase)
                ? ParseHtmlTable(tableMarkdown)
                : null;
        }

        ReadingTable? gfm = ParseTable(tableMarkdown);
        if (gfm is not null)
        {
            return gfm;
        }

        // A [Table] placeholder keeps its grid on the box payload. Build it once per box so a
        // complex table whose HTML markdown spans several blocks cannot repeat the same grid.
        if (boxId is not { } id || !payloadTablesBuilt.Add(id))
        {
            return null;
        }

        return box?.Payload is TableBoxPayload payload && TableContentAdapter.TryGetHtml(payload, out string html)
            ? ParseHtmlTable(html)
            : null;
    }

    // A complex table is compiled as raw <table> HTML. When the block's box is known and is not a
    // table, the box typing wins (a text box keeps its literal content); an unmapped block — the
    // whole-book path when the source map has no entry for the HTML node — is classified by its
    // own markdown slice, so the table renders as a grid instead of showing its markup.
    private static bool IsInlineHtmlTable(MarkdownBlock block, DocumentBox? box, string? markdownSource)
    {
        if (box is not null && box.BoxType != DocumentBoxType.Table)
        {
            return false;
        }

        string slice = TableMarkdown(block, markdownSource);
        return LooksLikeHtmlTableMarkup(slice) && slice.Contains("<tr", StringComparison.OrdinalIgnoreCase);
    }

    // The compiled HTML fallback of a complex table is the only table block that starts with a
    // tag; a GFM row starts with its first cell (or a pipe), never with a table tag.
    private static bool LooksLikeHtmlTableMarkup(string text)
    {
        string trimmed = text.TrimStart();
        return trimmed.StartsWith("<table", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("<tr", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("<td", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("<th", StringComparison.OrdinalIgnoreCase);
    }

    // The GFM grid lives in the compiled markdown, not in the block's plain text: Markdig
    // flattens a pipe table into space-separated cells, which would parse as a single-column
    // grid. Slicing the source by the block's [Start, Start+Length) range restores the pipes.
    // The plain text stays the fallback when no source is supplied (headless callers, tests) or
    // when the recorded range does not fit the supplied source.
    private static string TableMarkdown(MarkdownBlock block, string? markdownSource)
    {
        if (string.IsNullOrEmpty(markdownSource))
        {
            return block.Text;
        }

        int start = Math.Clamp(block.Start, 0, markdownSource.Length);
        int length = Math.Clamp(block.Length, 0, markdownSource.Length - start);
        string slice = markdownSource.Substring(start, length);
        return string.IsNullOrWhiteSpace(slice) ? block.Text : slice;
    }

    // Minimal GFM pipe-table reader for the reading view: optional leading/trailing pipes, cells
    // split on unescaped pipes, and a delimiter row (dashes and colons only) marks the row above
    // it as the header instead of being a data row itself. Anything that yields no data row is
    // not a renderable grid, so the caller keeps the raw text. Cell link markup ("[text](url)",
    // "<scheme://url>") is stripped to its display text; when any cell carries a link, the grid
    // also emits positioned cells with their link spans so the view can hit-test them.
    private static ReadingTable? ParseTable(string text)
    {
        if (string.Equals(text.Trim(), "[Table]", StringComparison.Ordinal))
        {
            return null;
        }

        List<IReadOnlyList<string>> rows = [];
        List<ReadingTableCell> linkCells = [];
        bool hasHeader = false;
        bool sawDataRow = false;
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            IReadOnlyList<string> sourceCells = SplitCells(line);
            if (IsDelimiterRow(sourceCells))
            {
                hasHeader |= sawDataRow;
                sawDataRow = false;
                continue;
            }

            int rowIndex = rows.Count;
            List<string> displayCells = new(sourceCells.Count);
            for (int column = 0; column < sourceCells.Count; column++)
            {
                string display = ExtractCellDisplay(sourceCells[column], out List<ReadingLinkSpan>? links);
                displayCells.Add(display);
                if (links is { Count: > 0 })
                {
                    linkCells.Add(new ReadingTableCell(rowIndex, column, display) { Links = links });
                }
            }

            rows.Add(displayCells);
            sawDataRow = true;
        }

        if (rows.Count == 0)
        {
            return null;
        }

        if (linkCells.Count == 0)
        {
            return new ReadingTable(rows, hasHeader);
        }

        // The view lays a table out from its positioned Cells whenever they are present, so a link
        // in one cell must not shrink the grid to just that cell: emit every row/column cell and
        // attach the link spans to the ones that carry them. The header flag is only known once the
        // delimiter row is seen, so row 0 is marked here.
        Dictionary<(int Row, int Column), IReadOnlyList<ReadingLinkSpan>> linksByCell =
            linkCells.Where(cell => cell.Links is { Count: > 0 })
                .ToDictionary(cell => (cell.Row, cell.Column), cell => cell.Links!);
        List<ReadingTableCell> positioned = [];
        for (int row = 0; row < rows.Count; row++)
        {
            for (int column = 0; column < rows[row].Count; column++)
            {
                ReadingTableCell cell = new(row, column, rows[row][column], IsHeader: hasHeader && row == 0);
                if (linksByCell.TryGetValue((row, column), out IReadOnlyList<ReadingLinkSpan>? links))
                {
                    cell = cell with { Links = links };
                }

                positioned.Add(cell);
            }
        }

        return new ReadingTable(rows, hasHeader) { Cells = positioned };
    }

    // Minimal inline-link reader for GFM table cells: "[text](url)" and bare "<scheme://url>"
    // autolinks. The display text replaces the markup so the cell reads like rendered markdown,
    // and the returned spans (offsets into the display text) keep the cell clickable. Anything
    // that does not parse as a link is copied verbatim.
    private static string ExtractCellDisplay(string cell, out List<ReadingLinkSpan>? links)
    {
        links = null;
        if (!cell.Contains('[') && !cell.Contains('<'))
        {
            return cell;
        }

        StringBuilder display = new(cell.Length);
        int index = 0;
        while (index < cell.Length)
        {
            if (TryReadBracketLink(cell, index, out string? linkedText, out string? url, out int consumed) ||
                TryReadAngleAutolink(cell, index, out linkedText, out url, out consumed))
            {
                int start = display.Length;
                display.Append(linkedText);
                links ??= [];
                links.Add(new ReadingLinkSpan(start, linkedText.Length, url));
                index += consumed;
                continue;
            }

            display.Append(cell[index]);
            index++;
        }

        return display.ToString();
    }

    private static bool TryReadBracketLink(
        string cell,
        int index,
        [NotNullWhen(true)] out string? text,
        [NotNullWhen(true)] out string? url,
        out int consumed)
    {
        text = null;
        url = null;
        consumed = 0;
        if (cell[index] != '[')
        {
            return false;
        }

        int close = cell.IndexOf(']', index + 1);
        if (close <= index + 1 || close + 1 >= cell.Length || cell[close + 1] != '(')
        {
            return false;
        }

        int end = cell.IndexOf(')', close + 2);
        if (end < 0)
        {
            return false;
        }

        string target = cell.Substring(close + 2, end - close - 2).Trim();
        // An optional markdown title ("url" "title") is not part of the target.
        int space = target.IndexOfAny(' ', '\t');
        if (space >= 0)
        {
            target = target[..space];
        }

        if (target.Length == 0)
        {
            return false;
        }

        text = cell.Substring(index + 1, close - index - 1);
        url = target;
        consumed = end - index + 1;
        return true;
    }

    private static bool TryReadAngleAutolink(
        string cell,
        int index,
        [NotNullWhen(true)] out string? text,
        [NotNullWhen(true)] out string? url,
        out int consumed)
    {
        text = null;
        url = null;
        consumed = 0;
        if (cell[index] != '<')
        {
            return false;
        }

        int end = cell.IndexOf('>', index + 1);
        if (end < 0)
        {
            return false;
        }

        string target = cell.Substring(index + 1, end - index - 1);
        if (!target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        text = target;
        url = target;
        consumed = end - index + 1;
        return true;
    }

    private static IReadOnlyList<string> SplitCells(string line)
    {
        string content = line;
        if (content.Length > 0 && content[0] == '|')
        {
            content = content[1..];
        }

        if (content.Length > 0 && content[^1] == '|' && !IsEscaped(content, content.Length - 1))
        {
            content = content[..^1];
        }

        List<string> cells = [];
        StringBuilder cell = new();
        for (int index = 0; index < content.Length; index++)
        {
            char current = content[index];
            if (current == '\\' && index + 1 < content.Length && content[index + 1] == '|')
            {
                cell.Append('|');
                index++;
                continue;
            }

            if (current == '|')
            {
                cells.Add(cell.ToString().Trim());
                cell.Clear();
                continue;
            }

            cell.Append(current);
        }

        cells.Add(cell.ToString().Trim());
        return cells;
    }

    private static bool IsDelimiterRow(IReadOnlyList<string> cells)
    {
        foreach (string cell in cells)
        {
            if (!cell.Contains('-'))
            {
                return false;
            }

            foreach (char character in cell)
            {
                if (character is not ('-' or ':'))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsEscaped(string value, int index)
    {
        int backslashes = 0;
        for (int candidate = index - 1; candidate >= 0 && value[candidate] == '\\'; candidate--)
        {
            backslashes++;
        }

        return backslashes % 2 == 1;
    }

    // Read the table's own rows/cells (nested tables remain inside their parent cell), retaining
    // merged-cell positions for the native reader's grid renderer. The original HTML stays on the
    // box payload; this is a read-only display projection.
    private static ReadingTable? ParseHtmlTable(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        HtmlDocument document = new();
        document.LoadHtml(html);
        HtmlNode? table = document.DocumentNode.Descendants("table").FirstOrDefault();
        if (table is null)
        {
            return null;
        }

        List<HtmlNode> sourceRows = [];
        CollectTableChildren(table, "tr", sourceRows);
        List<List<string>> rows = [];
        List<ReadingTableCell> cells = [];
        Dictionary<int, int> occupiedThroughRow = [];
        bool hasHeader = false;
        for (int rowIndex = 0; rowIndex < sourceRows.Count; rowIndex++)
        {
            HtmlNode row = sourceRows[rowIndex];
            List<HtmlNode> sourceCells = [];
            CollectTableChildren(row, "td", sourceCells);
            CollectTableChildren(row, "th", sourceCells);
            sourceCells = sourceCells.OrderBy(cell => cell.StreamPosition).ToList();
            List<string> values = [];
            int column = 0;
            foreach (HtmlNode sourceCell in sourceCells)
            {
                while (occupiedThroughRow.TryGetValue(column, out int occupiedThrough) &&
                       occupiedThrough >= rowIndex)
                {
                    SetCell(values, column++, string.Empty);
                }

                int columnSpan = ReadSpan(sourceCell, "colspan");
                int rowSpan = ReadSpan(sourceCell, "rowspan");
                string text = NormalizeCellText(sourceCell.InnerText);
                bool isHeader = string.Equals(sourceCell.Name, "th", StringComparison.OrdinalIgnoreCase);
                hasHeader |= rowIndex == 0 && isHeader;
                cells.Add(new ReadingTableCell(rowIndex, column, text, rowSpan, columnSpan, isHeader)
                {
                    Links = ExtractCellLinks(sourceCell, text)
                });
                SetCell(values, column, text);
                for (int offset = 0; offset < columnSpan; offset++)
                {
                    int occupiedColumn = column + offset;
                    if (rowSpan > 1)
                    {
                        occupiedThroughRow[occupiedColumn] = Math.Max(
                            occupiedThroughRow.GetValueOrDefault(occupiedColumn, -1), rowIndex + rowSpan - 1);
                    }

                    if (offset > 0)
                    {
                        SetCell(values, occupiedColumn, string.Empty);
                    }
                }

                column += columnSpan;
            }

            foreach ((int occupiedColumn, int occupiedThrough) in occupiedThroughRow)
            {
                if (occupiedThrough >= rowIndex && occupiedColumn >= values.Count)
                {
                    SetCell(values, occupiedColumn, string.Empty);
                }
            }

            rows.Add(values);
        }

        if (rows.Count == 0 || cells.Count == 0)
        {
            return null;
        }

        int columnCount = rows.Max(row => row.Count);
        foreach (List<string> row in rows)
        {
            while (row.Count < columnCount)
            {
                row.Add(string.Empty);
            }
        }

        return new ReadingTable(rows.Select(row => (IReadOnlyList<string>)row.AsReadOnly()).ToArray(), hasHeader)
        {
            Cells = cells.AsReadOnly()
        };
    }

    private static void CollectTableChildren(HtmlNode parent, string elementName, List<HtmlNode> result)
    {
        foreach (HtmlNode child in parent.ChildNodes)
        {
            if (string.Equals(child.Name, "table", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(child.Name, elementName, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(child);
            }
            else
            {
                CollectTableChildren(child, elementName, result);
            }
        }
    }

    private static int ReadSpan(HtmlNode cell, string attributeName)
    {
        string value = cell.GetAttributeValue(attributeName, string.Empty);
        return int.TryParse(value, out int span) ? Math.Clamp(span, 1, 64) : 1;
    }

    // A cell's anchors keep their href as display-projection link spans: each anchor's normalized
    // text is located in the normalized cell text (anchors appear in document order, so the search
    // resumes after the previous match), and its [start, start+length) range becomes clickable.
    private static IReadOnlyList<ReadingLinkSpan>? ExtractCellLinks(HtmlNode cell, string normalizedText)
    {
        List<ReadingLinkSpan>? links = null;
        int searchFrom = 0;
        foreach (HtmlNode anchor in cell.Descendants("a"))
        {
            string href = WebUtility.HtmlDecode(anchor.GetAttributeValue("href", string.Empty)).Trim();
            if (href.Length == 0)
            {
                continue;
            }

            string anchorText = NormalizeCellText(anchor.InnerText);
            if (anchorText.Length == 0)
            {
                continue;
            }

            int start = normalizedText.IndexOf(anchorText, searchFrom, StringComparison.Ordinal);
            if (start < 0)
            {
                start = normalizedText.IndexOf(anchorText, StringComparison.Ordinal);
            }

            if (start < 0)
            {
                continue;
            }

            links ??= [];
            links.Add(new ReadingLinkSpan(start, anchorText.Length, href));
            searchFrom = start + anchorText.Length;
        }

        return links;
    }

    private static string NormalizeCellText(string value)
    {
        return Regex.Replace(WebUtility.HtmlDecode(value), @"\s+", " ", RegexOptions.Singleline).Trim();
    }

    private static void SetCell(List<string> row, int column, string text)
    {
        while (row.Count <= column)
        {
            row.Add(string.Empty);
        }

        row[column] = text;
    }
}
