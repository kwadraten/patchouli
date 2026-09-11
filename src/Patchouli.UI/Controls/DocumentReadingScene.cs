using System.Text;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;

namespace Patchouli.UI.Controls;

// One row of a parsed GFM pipe table. Cells are raw inline-markdown text.
public sealed record ReadingTable(
    IReadOnlyList<IReadOnlyList<string>> Rows,
    bool HasHeader);

// A render-ready reading block: the unit the reading view lays out and draws top to bottom.
// Kind values: "heading", "paragraph", "list", "quote", "code", "equation", "table", "media",
// "divider", or any MarkdownBlock.Kind passed through unchanged. Text carries the plain payload
// (paragraph text, code, latex, media description); Inlines carries rich inline markup when the
// block kind supports it; Table carries the parsed grid for Kind == "table".
public sealed record ReadingBlock(
    DocumentBoxId? BoxId,
    string Kind,
    int Level,
    string Text,
    IReadOnlyList<MarkdownInlineModel>? Inlines = null,
    ReadingTable? Table = null,
    string? MediaLabel = null,
    string? CodeLanguage = null,
    string? MediaAssetId = null);

// Immutable snapshot of one page's readable content, in reading order.
public sealed record DocumentReadingScene(IReadOnlyList<ReadingBlock> Blocks)
{
    public static DocumentReadingScene Empty { get; } = new([]);
}

// Pure projection from the compiled markdown model (+ its source map and the page's boxes) to a
// reading snapshot. No Avalonia media types, no service calls — fully unit-testable headless.
public static class DocumentReadingSceneBuilder
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
    // - Table: for Kind == "table", parse block.Text as a GFM pipe table into ReadingTable —
    //   split lines, trim leading/trailing '|', split cells on unescaped '|' ('\|' is a literal
    //   pipe), detect a separator row (cells of only '-' and ':') marking the row above it as the
    //   header. When parsing yields no rows, or the text is the "[Table]" placeholder, set Table
    //   to null and keep the raw text.
    // - Media: MediaLabel is "图像" for image and "图表" for chart; Text is the block text;
    //   MediaAssetId comes from the box's MediaBoxPayload.AssetId when present; non-media blocks
    //   always carry a null MediaAssetId even if their payload happens to be a media payload.
    // - CodeLanguage: the box's CodeLanguage when present.
    // - Inlines: block.Inlines passed through (except tables, which render from Table).
    public static DocumentReadingScene Build(
        MarkdownDocumentModel model,
        IReadOnlyList<MarkdownSourceMapEntry> sourceMap,
        IReadOnlyList<DocumentBox> boxes)
    {
        if (model.Blocks.Count == 0)
        {
            return DocumentReadingScene.Empty;
        }

        Dictionary<DocumentBoxId, DocumentBox> boxesById = [];
        foreach (DocumentBox box in boxes)
        {
            boxesById.TryAdd(box.BoxId, box);
        }

        List<ReadingBlock> blocks = new(model.Blocks.Count);
        for (int index = 0; index < model.Blocks.Count; index++)
        {
            MarkdownBlock block = model.Blocks[index];
            DocumentBoxId? boxId = FindBoxId(sourceMap, index);
            DocumentBox? box = boxId is { } id && boxesById.TryGetValue(id, out DocumentBox? resolved)
                ? resolved
                : null;
            string kind = KindFor(box, block.Kind);
            bool isTable = kind == "table";
            ReadingTable? table = isTable ? ParseTable(block.Text) : null;
            blocks.Add(new ReadingBlock(
                boxId,
                kind,
                box?.HeadingLevel ?? block.Level,
                block.Text,
                isTable ? null : block.Inlines,
                table,
                kind == "media" ? MediaLabelFor(box) : null,
                box?.CodeLanguage,
                kind == "media" ? MediaAssetIdFor(box) : null));
        }

        return new DocumentReadingScene(blocks);
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

    // The asset id is opaque to the reading view; it only needs to travel from the box payload to
    // the media loader that knows how to resolve it.
    private static string? MediaAssetIdFor(DocumentBox? box)
    {
        return (box?.Payload as MediaBoxPayload)?.AssetId;
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
}
