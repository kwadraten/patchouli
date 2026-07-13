using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Net;

namespace Patchouli.Infrastructure.Ocr.MinerU;

internal sealed record MinerUContentListPage(
    [property: JsonPropertyName("page_num")]
    int PageNum,
    [property: JsonPropertyName("width")] double Width,
    [property: JsonPropertyName("height")] double Height,
    [property: JsonPropertyName("blocks")] IReadOnlyList<MinerUContentBlock> Blocks,
    [property: JsonPropertyName("discarded_blocks")]
    IReadOnlyList<MinerUContentBlock>? DiscardedBlocks = null);

internal sealed record MinerUContentBlock(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("bbox")] double[]? Bbox,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("latex")] string? LaTex,
    [property: JsonPropertyName("confidence")]
    double? Confidence,
    [property: JsonPropertyName("table_cells")]
    IReadOnlyList<MinerUTableCell>? TableCells = null,
    [property: JsonPropertyName("cells")] IReadOnlyList<MinerUTableCell>? Cells = null,
    [property: JsonPropertyName("level")] int? HeadingLevel = null);

internal sealed record MinerUTableCell(
    [property: JsonPropertyName("row_index")]
    int? RowIndex,
    [property: JsonPropertyName("col_index")]
    int? ColIndex,
    [property: JsonPropertyName("row_span")]
    int? RowSpan,
    [property: JsonPropertyName("col_span")]
    int? ColSpan,
    [property: JsonPropertyName("is_header")]
    bool? IsHeader,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("bbox")] double[]? Bbox);

internal sealed record MinerUContentListDocument(
    [property: JsonPropertyName("pages")] IReadOnlyList<MinerUContentListPage> Pages);

internal sealed class MinerUContentListParser
{
    private static readonly Regex TableRowPattern = new(@"<tr\b[^>]*>(?<body>.*?)</tr>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex TableCellPattern = new(@"<(?<tag>t[dh])\b(?<attrs>[^>]*)>(?<body>.*?)</t[dh]>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex SpanAttributePattern =
        new(@"\b(?<name>rowspan|colspan)\s*=\s*[""']?(?<value>\d+)[""']?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BreakPattern = new(@"<br\s*/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TagPattern = new(@"<[^>]+>", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);

    public MinerUContentListDocument? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind switch
        {
            JsonValueKind.Array => ParseFlatContentList(document.RootElement),
            JsonValueKind.Object => ParsePagedContentList(json),
            _ => null
        };
    }

    private static MinerUContentListDocument? ParsePagedContentList(string json)
    {
        return JsonSerializer.Deserialize<MinerUContentListDocument>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }

    private static MinerUContentListDocument ParseFlatContentList(JsonElement root)
    {
        SortedDictionary<int, List<MinerUContentBlock>> pages = new();

        int pageOrdinal = 0;
        foreach (JsonElement item in root.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Array)
            {
                int pageIdx = pageOrdinal++;
                foreach (JsonElement block in item.EnumerateArray())
                {
                    AddFlatItem(pages, block, pageIdx);
                }

                continue;
            }

            AddFlatItem(pages, item, null);
        }

        return new MinerUContentListDocument(
            pages.Select(pair => new MinerUContentListPage(
                pair.Key + 1,
                1000,
                1000,
                pair.Value)).ToArray());
    }

    private static void AddFlatItem(
        SortedDictionary<int, List<MinerUContentBlock>> pages,
        JsonElement item,
        int? explicitPageIdx)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        int pageIdx = explicitPageIdx ?? GetInt(item, "page_idx") ?? GetInt(item, "page_num") - 1 ?? 0;
        if (pageIdx < 0)
        {
            pageIdx = 0;
        }

        if (!pages.TryGetValue(pageIdx, out List<MinerUContentBlock>? blocks))
        {
            blocks = [];
            pages[pageIdx] = blocks;
        }

        string? text = GetText(item);
        string? tableHtml = GetTableHtml(item);
        IReadOnlyList<MinerUTableCell>? tableCells = GetCells(item, "table_cells")
                                                     ?? GetCells(item, "cells")
                                                     ?? ParseHtmlTableCells(tableHtml);

        blocks.Add(new MinerUContentBlock(
            GetString(item, "type") ?? "text",
            GetBbox(item),
            text,
            GetString(item, "latex") ?? GetString(item, "equation"),
            GetDouble(item, "confidence"),
            tableCells,
            null,
            GetInt(item, "level") ?? GetInt(item, "heading_level")));
    }

    private static string? GetText(JsonElement item)
    {
        return GetString(item, "text")
               ?? GetString(item, "table_body")
               ?? GetString(item, "table")
               ?? GetString(item, "caption")
               ?? GetContentText(item);
    }

    private static string? GetTableHtml(JsonElement item)
    {
        string? html = GetString(item, "table_body")
                       ?? GetString(item, "table")
                       ?? GetNestedString(item, "content", "html");
        return string.IsNullOrWhiteSpace(html) ? null : html;
    }

    private static string? GetContentText(JsonElement item)
    {
        if (!item.TryGetProperty("content", out JsonElement content))
        {
            return null;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString();
        }

        if (content.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return GetString(content, "html")
               ?? GetInlineText(content, "title_content")
               ?? GetInlineText(content, "paragraph_content")
               ?? GetTypedInlineText(item, content);
    }

    private static string? GetTypedInlineText(JsonElement item, JsonElement content)
    {
        string? type = GetString(item, "type");
        return string.IsNullOrWhiteSpace(type)
            ? null
            : GetInlineText(content, $"{type.Trim().ToLowerInvariant()}_content");
    }

    private static string? GetString(JsonElement item, string propertyName)
    {
        return item.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int? GetInt(JsonElement item, string propertyName)
    {
        return item.TryGetProperty(propertyName, out JsonElement value) && value.TryGetInt32(out int number)
            ? number
            : null;
    }

    private static double? GetDouble(JsonElement item, string propertyName)
    {
        return item.TryGetProperty(propertyName, out JsonElement value) && value.TryGetDouble(out double number)
            ? number
            : null;
    }

    private static double[]? GetBbox(JsonElement item)
    {
        if (!item.TryGetProperty("bbox", out JsonElement bbox) || bbox.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        double[] values = bbox.EnumerateArray()
            .Where(v => v.ValueKind == JsonValueKind.Number)
            .Select(v => v.GetDouble())
            .ToArray();

        return values.Length == 4 ? values : null;
    }

    private static IReadOnlyList<MinerUTableCell>? GetCells(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out JsonElement cells) || cells.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return cells.EnumerateArray()
            .Where(cell => cell.ValueKind == JsonValueKind.Object)
            .Select(cell => new MinerUTableCell(
                GetInt(cell, "row_index"),
                GetInt(cell, "col_index"),
                GetInt(cell, "row_span"),
                GetInt(cell, "col_span"),
                GetBool(cell, "is_header"),
                GetText(cell),
                GetBbox(cell)))
            .ToArray();
    }

    private static bool? GetBool(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static string? GetNestedString(JsonElement item, string objectName, string propertyName)
    {
        if (!item.TryGetProperty(objectName, out JsonElement nested) || nested.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return GetString(nested, propertyName);
    }

    private static string? GetInlineText(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string[] parts = array.EnumerateArray()
            .Select(part => part.ValueKind == JsonValueKind.Object ? GetString(part, "content") : null)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text!.Trim())
            .ToArray();

        return parts.Length == 0 ? null : string.Join("", parts);
    }

    private static IReadOnlyList<MinerUTableCell>? ParseHtmlTableCells(string? html)
    {
        if (string.IsNullOrWhiteSpace(html) || !html.Contains("<table", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        List<MinerUTableCell> cells = new();
        HashSet<(int Row, int Col)> occupied = new();
        int rowIndex = 0;

        foreach (Match rowMatch in TableRowPattern.Matches(html))
        {
            int colIndex = 0;
            foreach (Match cellMatch in TableCellPattern.Matches(rowMatch.Groups["body"].Value))
            {
                while (occupied.Contains((rowIndex, colIndex)))
                {
                    colIndex++;
                }

                string attrs = cellMatch.Groups["attrs"].Value;
                int rowSpan = ReadSpan(attrs, "rowspan");
                int colSpan = ReadSpan(attrs, "colspan");
                bool? isHeader = cellMatch.Groups["tag"].Value.Equals("th", StringComparison.OrdinalIgnoreCase)
                    ? true
                    : (bool?)null;

                cells.Add(new MinerUTableCell(
                    rowIndex,
                    colIndex,
                    rowSpan,
                    colSpan,
                    isHeader,
                    CleanHtmlText(cellMatch.Groups["body"].Value),
                    null));

                for (int row = rowIndex; row < rowIndex + rowSpan; row++)
                for (int col = colIndex; col < colIndex + colSpan; col++)
                {
                    occupied.Add((row, col));
                }

                colIndex += colSpan;
            }

            rowIndex++;
        }

        return cells.Count == 0 ? null : cells;
    }

    private static int ReadSpan(string attrs, string name)
    {
        foreach (Match match in SpanAttributePattern.Matches(attrs))
        {
            if (match.Groups["name"].Value.Equals(name, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(match.Groups["value"].Value, out int value)
                && value > 0)
            {
                return value;
            }
        }

        return 1;
    }

    private static string CleanHtmlText(string html)
    {
        string withBreaks = BreakPattern.Replace(html, "\n");
        string withoutTags = TagPattern.Replace(withBreaks, " ");
        string decoded = WebUtility.HtmlDecode(withoutTags);
        return WhitespacePattern.Replace(decoded, " ").Trim();
    }
}
