using System.Text.Json;
using Patchouli.Core.Results;

namespace Patchouli.Ocr;

public interface IMultimodalLayoutOrderResolver
{
    Task<Result<IReadOnlyList<int>>> ResolveReadingOrderAsync(byte[] imageBytes,
        IReadOnlyList<DocumentLayoutRegion> regions, OcrPresetVersion presetVersion,
        CancellationToken cancellationToken = default);
}

public static class MultimodalLayoutReadingOrder
{
    public static Result<IReadOnlyList<int>> Parse(string content, int count)
    {
        try
        {
            string json = content.Trim();
            if (json.StartsWith("```", StringComparison.Ordinal))
            {
                int newline = json.IndexOf('\n');
                int end = json.LastIndexOf("```", StringComparison.Ordinal);
                if (newline >= 0 && end > newline)
                {
                    json = json[(newline + 1)..end].Trim();
                }
            }

            using JsonDocument document = JsonDocument.Parse(json);
            int[] order = document.RootElement.GetProperty("order").EnumerateArray()
                .Select(element => element.GetInt32()).ToArray();
            if (order.Length == count && order.Distinct().Count() == count &&
                order.All(id => id >= 0 && id < count))
            {
                return Result<IReadOnlyList<int>>.Success(order);
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or
                                              KeyNotFoundException or FormatException or OverflowException)
        {
            return InvalidOrder();
        }

        return InvalidOrder();
    }

    private static Result<IReadOnlyList<int>> InvalidOrder()
    {
        return Result<IReadOnlyList<int>>.Failure(AppErrorCodes.ValidationFailed,
            "多模态模型返回的阅读顺序无效：区域编号必须完整、唯一，且不能包含不存在的区域。");
    }
}
