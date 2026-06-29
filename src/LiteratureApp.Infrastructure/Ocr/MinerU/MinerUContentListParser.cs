using System.Text.Json;

namespace LiteratureApp.Infrastructure.Ocr.MinerU;

internal sealed record MinerUContentListPage(
    int PageNum,
    double Width,
    double Height,
    IReadOnlyList<MinerUContentBlock> Blocks);

internal sealed record MinerUContentBlock(
    string Type,
    double[]? Bbox,
    string? Text,
    string? LaTex,
    double? Confidence);

internal sealed record MinerUContentListDocument(
    IReadOnlyList<MinerUContentListPage> Pages);

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
