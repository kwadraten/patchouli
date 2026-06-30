using System.Text.Json;
using Patchouli.Core.Layout;

namespace Patchouli.Ocr;

public sealed class MockOcrEngine : IOcrEngine
{
    public string EngineId => OcrEngineIds.Mock;

    public Task<OcrEnginePageResult> RunPageAsync(
        Page page,
        OcrPresetVersion presetVersion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var options = MockOcrOptions.Parse(presetVersion.ParametersJson);
        if (options.BBoxFailurePages.Contains(page.PageIndex))
        {
            return Task.FromResult(new OcrEnginePageResult(
                page.PageId,
                Succeeded: false,
                Text: null,
                BBox: null,
                OcrFailureCode.BBoxCoordinateTransformFailed,
                "Mock bbox coordinate transform failed."));
        }

        if (options.FailPages.Contains(page.PageIndex))
        {
            return Task.FromResult(new OcrEnginePageResult(
                page.PageId,
                Succeeded: false,
                Text: null,
                BBox: null,
                OcrFailureCode.MockPageFailure,
                "Mock OCR page failure."));
        }

        var text = page.PageIndex switch
        {
            0 => "这是 OCR 测试文本。",
            1 => "这是第二页 OCR 文本。",
            _ => $"这是第 {page.PageIndex + 1} 页 OCR 文本。"
        };

        return Task.FromResult(new OcrEnginePageResult(
            page.PageId,
            Succeeded: true,
            text,
            new NormalizedBBox(0.1, 0.1, 0.8, 0.2),
            ErrorCode: null,
            ErrorMessage: null,
            SourceBBox: new SourceBBox(0.1, 0.1, 0.8, 0.2, SourceBBoxCoordinateSystem.NormalizedPage, EngineName: OcrEngineIds.Mock)));
    }

    private sealed record MockOcrOptions(IReadOnlySet<int> FailPages, IReadOnlySet<int> BBoxFailurePages)
    {
        public static MockOcrOptions Parse(string parametersJson)
        {
            if (string.IsNullOrWhiteSpace(parametersJson))
            {
                return new MockOcrOptions(new HashSet<int>(), new HashSet<int>());
            }

            using var document = JsonDocument.Parse(parametersJson);
            return new MockOcrOptions(
                ReadIntSet(document.RootElement, "failPages"),
                ReadIntSet(document.RootElement, "bboxFailurePages"));
        }

        private static IReadOnlySet<int> ReadIntSet(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Array)
            {
                return new HashSet<int>();
            }

            return property.EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.Number)
                .Select(element => element.GetInt32())
                .ToHashSet();
        }
    }
}
