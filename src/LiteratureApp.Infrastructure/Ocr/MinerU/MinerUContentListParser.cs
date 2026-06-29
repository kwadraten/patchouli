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

        var doc = JsonSerializer.Deserialize<MinerUContentListDocument>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        return doc;
    }
}
