using System.Net;
using System.Text;
using System.Text.RegularExpressions;
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
    private static readonly Regex HtmlRowPattern = new(
        @"<tr\b[^>]*>(?<body>.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex HtmlCellPattern = new(
        @"<(?<tag>t[dh])\b(?<attrs>[^>]*)>(?<body>.*?)</t[dh]>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex HtmlSpanPattern = new(
        @"\bcolspan\s*=\s*[""']?(?<value>\d+)", RegexOptions.IgnoreCase);

    private static readonly Regex HtmlTagPattern = new("<[^>]+>", RegexOptions.Singleline);

    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Singleline);

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
    // - Media: MediaLabel is "图像" for image and "图表" for chart; Text is the block text; Image is
    //   the box's normalized BBox region keyed by imageKey, for drawing a sub-rect of the page
    //   bitmap. Non-media blocks always carry a null Image even if their payload is a media payload.
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
        for (int index = 0; index < model.Blocks.Count; index++)
        {
            MarkdownBlock block = model.Blocks[index];
            DocumentBoxId? boxId = FindBoxId(sourceMap, index);
            DocumentBox? box = boxId is { } id && boxesById.TryGetValue(id, out DocumentBox? resolved)
                ? resolved
                : null;
            string kind = KindFor(box, block.Kind);
            bool isTable = kind == "table";
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
                kind == "media" ? MediaLabelFor(box) : null,
                box?.CodeLanguage,
                kind == "media" ? MediaImageFor(box, imageKey) : null,
                pageIndex));
        }

        return new ReadingScene(blocks);
    }

    // Source-map entries are ranges over preview node indexes; the first covering entry wins,
    // matching how the preview binds a markdown block back to its originating box.
    private static DocumentBoxId? FindBoxId(IReadOnlyList<MarkdownSourceMapEntry> sourceMap, int index)
    {
        foreach (MarkdownSourceMapEntry entry in sourceMap)
        {
            if (index >= entry.PreviewNodeStart && index < entry.PreviewNodeStart + entry.PreviewNodeCount)
            {
                return entry.BoxId;
            }
        }

        return null;
    }

    // Box types describe the authored leaf; reading kinds describe how to draw it. Auxiliary
    // text leaves (captions, footnotes, running heads) read as plain paragraphs, and a box type
    // the reading view does not know keeps the markdown block's own kind.
    private static string KindFor(DocumentBox? box, string fallback)
    {
        return box?.BoxType switch
        {
            DocumentBoxType.Text or DocumentBoxType.RefText => "paragraph",
            DocumentBoxType.Title => "heading",
            DocumentBoxType.Image or DocumentBoxType.Chart => "media",
            DocumentBoxType.List => "list",
            DocumentBoxType.Code or DocumentBoxType.Algorithm => "code",
            DocumentBoxType.Equation => "equation",
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
        return box?.BoxType == DocumentBoxType.Chart ? "图表" : "图像";
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

        return ParseHtmlTable((box?.Payload as TableBoxPayload)?.Html);
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
    // not a renderable grid, so the caller keeps the raw text.
    private static ReadingTable? ParseTable(string text)
    {
        if (string.Equals(text.Trim(), "[Table]", StringComparison.Ordinal))
        {
            return null;
        }

        List<IReadOnlyList<string>> rows = [];
        bool hasHeader = false;
        bool sawDataRow = false;
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            IReadOnlyList<string> cells = SplitCells(line);
            if (IsDelimiterRow(cells))
            {
                hasHeader |= sawDataRow;
                sawDataRow = false;
                continue;
            }

            rows.Add(cells);
            sawDataRow = true;
        }

        return rows.Count == 0 ? null : new ReadingTable(rows, hasHeader);
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

    // Reader for the stored HTML of a [Table] placeholder (an irregular or spanning table that GFM
    // cannot express; the OCR pipeline keeps the source <table> exactly for this case). Only rows
    // and cells are read: a colspan reserves its extra slots as empty cells so the row still lines
    // up with the rest of the grid, and rowspan stays with the row that declares it. Cell markup is
    // flattened to text, so nested emphasis/marks read as their own text rather than as tags.
    private static ReadingTable? ParseHtmlTable(string? html)
    {
        if (string.IsNullOrWhiteSpace(html) || !html.Contains("<tr", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        List<IReadOnlyList<string>> rows = [];
        bool hasHeader = false;
        foreach (Match rowMatch in HtmlRowPattern.Matches(html))
        {
            List<string> cells = [];
            bool headerRow = false;
            foreach (Match cellMatch in HtmlCellPattern.Matches(rowMatch.Groups["body"].Value))
            {
                headerRow |= string.Equals(cellMatch.Groups["tag"].Value, "th", StringComparison.OrdinalIgnoreCase);
                cells.Add(HtmlCellText(cellMatch.Groups["body"].Value));
                int span = ColumnSpan(cellMatch.Groups["attrs"].Value);
                for (int extra = 1; extra < span; extra++)
                {
                    cells.Add(string.Empty);
                }
            }

            if (cells.Count == 0)
            {
                continue;
            }

            hasHeader |= rows.Count == 0 && headerRow;
            rows.Add(cells);
        }

        return rows.Count == 0 ? null : new ReadingTable(rows, hasHeader);
    }

    private static string HtmlCellText(string body)
    {
        return WhitespacePattern.Replace(WebUtility.HtmlDecode(HtmlTagPattern.Replace(body, " ")), " ").Trim();
    }

    private static int ColumnSpan(string attributes)
    {
        Match span = HtmlSpanPattern.Match(attributes);
        return span.Success && int.TryParse(span.Groups["value"].Value, out int value) && value > 0
            ? value
            : 1;
    }
}
