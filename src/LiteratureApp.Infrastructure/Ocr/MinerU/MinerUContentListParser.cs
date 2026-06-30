using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiteratureApp.Infrastructure.Ocr.MinerU;

internal sealed record MinerUContentListPage(
    [property: JsonPropertyName("page_num")] int PageNum,
    [property: JsonPropertyName("width")] double Width,
    [property: JsonPropertyName("height")] double Height,
    [property: JsonPropertyName("blocks")] IReadOnlyList<MinerUContentBlock> Blocks);

internal sealed record MinerUContentBlock(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("bbox")] double[]? Bbox,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("latex")] string? LaTex,
    [property: JsonPropertyName("confidence")] double? Confidence);

internal sealed record MinerUContentListDocument(
    [property: JsonPropertyName("pages")] IReadOnlyList<MinerUContentListPage> Pages);

internal sealed class MinerUContentListParser
{
    public MinerUContentListDocument? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        using var document = JsonDocument.Parse(json);
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
        var pages = new SortedDictionary<int, List<MinerUContentBlock>>();

        foreach (var item in root.EnumerateArray())
        {
            var pageIdx = GetInt(item, "page_idx") ?? GetInt(item, "page_num") - 1 ?? 0;
            if (pageIdx < 0)
                pageIdx = 0;

            if (!pages.TryGetValue(pageIdx, out var blocks))
            {
                blocks = [];
                pages[pageIdx] = blocks;
            }

            blocks.Add(new MinerUContentBlock(
                GetString(item, "type") ?? "text",
                GetBbox(item),
                GetText(item),
                GetString(item, "latex") ?? GetString(item, "equation"),
                GetDouble(item, "confidence")));
        }

        return new MinerUContentListDocument(
            pages.Select(pair => new MinerUContentListPage(
                pair.Key + 1,
                1000,
                1000,
                pair.Value)).ToArray());
    }

    private static string? GetText(JsonElement item)
    {
        return GetString(item, "text")
            ?? GetString(item, "table_body")
            ?? GetString(item, "table")
            ?? GetString(item, "caption");
    }

    private static string? GetString(JsonElement item, string propertyName)
    {
        return item.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int? GetInt(JsonElement item, string propertyName)
    {
        return item.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var number)
            ? number
            : null;
    }

    private static double? GetDouble(JsonElement item, string propertyName)
    {
        return item.TryGetProperty(propertyName, out var value) && value.TryGetDouble(out var number)
            ? number
            : null;
    }

    private static double[]? GetBbox(JsonElement item)
    {
        if (!item.TryGetProperty("bbox", out var bbox) || bbox.ValueKind != JsonValueKind.Array)
            return null;

        var values = bbox.EnumerateArray()
            .Where(v => v.ValueKind == JsonValueKind.Number)
            .Select(v => v.GetDouble())
            .ToArray();

        return values.Length == 4 ? values : null;
    }
}
