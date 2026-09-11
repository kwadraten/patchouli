using System.Text.Json;

namespace Patchouli.Infrastructure.Ocr.RapidOcr;

/// <summary>
/// RapidOCR preset parameters. Defaults mirror the official v3.9.2 <c>config.yaml</c> so a
/// preset that omits a value keeps upstream behavior. RapidOCR is executed in-process on
/// ONNX Runtime; there is no Python or external runner path.
/// </summary>
public sealed record RapidOcrParameters(
    double TextScore,
    bool UseDet,
    bool UseCls,
    bool UseRec,
    bool UsePreprocessImg,
    int MinSideLen,
    int MaxSideLen,
    bool UseVerticalPadding,
    int MinHeight,
    int WidthHeightRatio,
    bool ReturnWordBox,
    bool ReturnSingleCharBox,
    string? ModelRootDir,
    string? RecKeysPath,
    int IntraOpNumThreads,
    int InterOpNumThreads)
{
    public const double DefaultTextScore = 0.5;
    public const int DefaultMinSideLen = 30;
    public const int DefaultMaxSideLen = 2000;
    public const int DefaultMinHeight = 30;
    public const int DefaultWidthHeightRatio = 8;
    public const int DefaultThreads = -1;

    public static RapidOcrParameters Default { get; } = new(
        DefaultTextScore,
        true,
        true,
        true,
        true,
        DefaultMinSideLen,
        DefaultMaxSideLen,
        true,
        DefaultMinHeight,
        DefaultWidthHeightRatio,
        false,
        false,
        null,
        null,
        DefaultThreads,
        DefaultThreads);

    private static readonly StringComparer KeyComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Applies the JSON object onto the official defaults. Unknown keys are ignored and a
    /// missing key keeps the upstream default (including the empty object, which must not
    /// fall back to CLR default values).
    /// </summary>
    public static RapidOcrParameters FromJson(string? parametersJson)
    {
        RapidOcrParameters parameters = Default;
        if (string.IsNullOrWhiteSpace(parametersJson))
        {
            return parameters;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(parametersJson);
        }
        catch (JsonException)
        {
            return parameters;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return parameters;
            }

            Dictionary<string, JsonElement> values = new(KeyComparer);
            foreach (JsonProperty property in root.EnumerateObject())
            {
                values[property.Name] = property.Value;
            }

            parameters = parameters with
            {
                TextScore = ReadDouble(values, "textScore", parameters.TextScore),
                UseDet = ReadBool(values, "useDet", parameters.UseDet),
                UseCls = ReadBool(values, "useCls", parameters.UseCls),
                UseRec = ReadBool(values, "useRec", parameters.UseRec),
                UsePreprocessImg = ReadBool(values, "usePreprocessImg", parameters.UsePreprocessImg),
                MinSideLen = ReadInt(values, "minSideLen", parameters.MinSideLen),
                MaxSideLen = ReadInt(values, "maxSideLen", parameters.MaxSideLen),
                UseVerticalPadding = ReadBool(values, "useVerticalPadding", parameters.UseVerticalPadding),
                MinHeight = ReadInt(values, "minHeight", parameters.MinHeight),
                WidthHeightRatio = ReadInt(values, "widthHeightRatio", parameters.WidthHeightRatio),
                ReturnWordBox = ReadBool(values, "returnWordBox", parameters.ReturnWordBox),
                ReturnSingleCharBox = ReadBool(values, "returnSingleCharBox", parameters.ReturnSingleCharBox),
                ModelRootDir = ReadString(values, "modelRootDir", parameters.ModelRootDir),
                RecKeysPath = ReadString(values, "recKeysPath", parameters.RecKeysPath),
                IntraOpNumThreads = ReadInt(values, "intraOpNumThreads", parameters.IntraOpNumThreads),
                InterOpNumThreads = ReadInt(values, "interOpNumThreads", parameters.InterOpNumThreads)
            };
        }

        return parameters.Normalize();
    }

    private static double ReadDouble(Dictionary<string, JsonElement> values, string key, double fallback)
    {
        return values.TryGetValue(key, out JsonElement element) && element.ValueKind == JsonValueKind.Number &&
               element.TryGetDouble(out double value)
            ? value
            : fallback;
    }

    private static int ReadInt(Dictionary<string, JsonElement> values, string key, int fallback)
    {
        return values.TryGetValue(key, out JsonElement element) && element.ValueKind == JsonValueKind.Number &&
               element.TryGetInt32(out int value)
            ? value
            : fallback;
    }

    private static bool ReadBool(Dictionary<string, JsonElement> values, string key, bool fallback)
    {
        return values.TryGetValue(key, out JsonElement element) &&
               element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetBoolean()
            : fallback;
    }

    private static string? ReadString(Dictionary<string, JsonElement> values, string key, string? fallback)
    {
        if (!values.TryGetValue(key, out JsonElement element))
        {
            return fallback;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Null => null,
            _ => fallback
        };
    }

    public RapidOcrParameters Normalize()
    {
        int minSideLen = Math.Max(1, MinSideLen);
        int maxSideLen = Math.Max(minSideLen, MaxSideLen);
        return this with
        {
            TextScore = Math.Clamp(TextScore, 0.0, 1.0),
            MinSideLen = minSideLen,
            MaxSideLen = maxSideLen,
            MinHeight = Math.Max(1, MinHeight),
            // Upstream uses -1 to disable the vertical-padding aspect ratio check.
            WidthHeightRatio = WidthHeightRatio == -1 ? -1 : Math.Max(1, WidthHeightRatio)
        };
    }

    public bool RequiresDetectionAndRecognition => UseDet && UseRec;

    /// <summary>
    /// Options the native pipeline does not implement. They must fail validation instead of
    /// being silently ignored.
    /// </summary>
    public IReadOnlyList<string> GetUnsupportedOptions()
    {
        List<string> unsupported = new();
        if (ReturnWordBox)
        {
            unsupported.Add("returnWordBox");
        }

        if (ReturnSingleCharBox)
        {
            unsupported.Add("returnSingleCharBox");
        }

        return unsupported;
    }
}
