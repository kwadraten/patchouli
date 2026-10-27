using System.Globalization;
using System.Text.Json;
using Patchouli.Extensions.Contracts;

namespace Patchouli.Ocr.Engines.Vision;

/// <summary>
/// The frozen multimodal-OCR configuration, resolved once per job from the pinned
/// <see cref="ExtensionConfigSnapshot"/>. Explicit typed keys overlay the legacy
/// <c>parameters-json</c> payload, which overlays the defaults — the same precedence the
/// native engines use, so a migrated preset loses no configured value.
/// </summary>
public sealed record MultimodalOcrOptions(
    string? ProviderId,
    string? Model,
    string? ReasoningEffort,
    bool UseLocalLayout,
    string LayoutModelId,
    string? LayoutModelPath,
    int Concurrency,
    string ImageDetail)
{
    /// <summary>Region-recognition call budget; mirrors <c>OcrPerformanceSettings</c> in Patchouli.Llm.</summary>
    public const int DefaultConcurrency = 4;

    public const int MaxConcurrency = 16;

    public static MultimodalOcrOptions FromSnapshot(ExtensionConfigSnapshot config)
    {
        ArgumentNullException.ThrowIfNull(config);
        IReadOnlyDictionary<string, string> values = config.Values;
        string parametersJson = GetValue(values, "parameters-json") ?? "";

        (string? selectionProvider, string? selectionModel) = ReadModelSelection(GetValue(values, "model"));
        string? providerId = FirstNonEmpty(selectionProvider,
            ReadJsonString(parametersJson, "providerId"));
        string? model = FirstNonEmpty(GetValue(values, "model-id"), selectionModel,
            ReadJsonString(parametersJson, "modelId"), ReadJsonString(parametersJson, "model"));
        string? layoutPath = FirstNonEmpty(GetValue(values, "model-path"),
            ReadJsonString(parametersJson, "modelPath"), ReadJsonString(parametersJson, "layoutModelPath"));
        string layoutModel = VisionLayoutModels.Normalize(FirstNonEmpty(GetValue(values, "layout-model"),
            ReadJsonString(parametersJson, "layoutModel")));
        bool useLocalLayout = ReadBool(values, "use-local-layout",
            ReadJsonBool(parametersJson, "useLocalLayout", true));
        int concurrency = ReadInt(values, "concurrency",
            ReadJsonInt(parametersJson, "concurrency", DefaultConcurrency));
        if (concurrency < 1 || concurrency > MaxConcurrency)
        {
            throw new InvalidOperationException(
                $"Multimodal OCR concurrency must be between 1 and {MaxConcurrency}.");
        }

        string? reasoning = NormalizeReasoning(FirstNonEmpty(GetValue(values, "reasoning-effort"),
            ReadJsonString(parametersJson, "reasoningEffort")));
        string? detail = FirstNonEmpty(GetValue(values, "image-detail"),
            ReadJsonString(parametersJson, "imageDetail"));
        // The legacy adapter always sent detail=high; keep that default unless configured otherwise.
        string imageDetail = detail?.ToLowerInvariant() is "low" or "auto"
            ? detail!.Trim().ToLowerInvariant()
            : "high";

        return new MultimodalOcrOptions(providerId, model, reasoning, useLocalLayout, layoutModel,
            layoutPath, concurrency, imageDetail);
    }

    /// <summary>
    /// The extension model selection passed to <c>ResolveLlm("ocr", ...)</c>: a preset overrides
    /// the provider only through the model-selection JSON; <c>model-id</c> rebinds just the model
    /// name on whatever provider the OCR slot resolved.
    /// </summary>
    public ExtensionModelSelection? ModelSelection
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ProviderId) && string.IsNullOrWhiteSpace(Model))
            {
                return null;
            }

            return new ExtensionModelSelection(ProviderId ?? "", Model ?? "");
        }
    }

    /// <summary>
    /// OCR reasoning normalization (the <c>OcrPerformanceSettings</c> rule mirrored: this leaf
    /// assembly cannot reference Patchouli.Llm). An explicit value wins; <c>default</c>/blank maps
    /// to null so the host's configured OCR effort applies.
    /// </summary>
    public static string? NormalizeReasoning(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "default" => null,
            "none" or "low" or "medium" or "high" or "max" => value.Trim().ToLowerInvariant(),
            _ => "none"
        };
    }

    private static (string? ProviderId, string? Model) ReadModelSelection(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (null, null);
        }

        try
        {
            ExtensionModelSelection? selection = JsonSerializer.Deserialize<ExtensionModelSelection>(raw,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return (selection?.ProviderId, selection?.Model);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Snapshot lookup with generic legacy-alias normalization: the canonical kebab-case key wins
    /// when present, then any spelling that normalizes to it — camelCase, snake_case, or
    /// kebab-case — resolves to the same option. An explicitly present key always beats the
    /// <c>parameters-json</c> overlay; an empty value does not count as present.
    /// </summary>
    private static string? GetValue(IReadOnlyDictionary<string, string> values, string key)
    {
        if (values.TryGetValue(key, out string? value))
        {
            return value;
        }

        string normalized = NormalizeKey(key);
        foreach (KeyValuePair<string, string> pair in values)
        {
            if (pair.Key != key && NormalizeKey(pair.Key) == normalized)
            {
                return pair.Value;
            }
        }

        return null;
    }

    /// <summary>Canonical comparison form: lowercase with '-' and '_' stripped.</summary>
    private static string NormalizeKey(string name)
    {
        System.Text.StringBuilder builder = new(name.Length);
        foreach (char character in name)
        {
            if (character is not ('-' or '_'))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static bool NamesMatch(string left, string right)
    {
        return NormalizeKey(left) == NormalizeKey(right);
    }

    private static string? FirstNonEmpty(params string?[] candidates)
    {
        foreach (string? candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate.Trim();
            }
        }

        return null;
    }

    private static bool ReadBool(IReadOnlyDictionary<string, string> values, string key, bool fallback)
    {
        string? value = GetValue(values, key);
        return string.IsNullOrWhiteSpace(value)
            ? fallback
            : bool.TryParse(value, out bool parsed)
                ? parsed
                : value switch
                {
                    "1" => true,
                    "0" => false,
                    _ => fallback
                };
    }

    private static int ReadInt(IReadOnlyDictionary<string, string> values, string key, int fallback)
    {
        string? value = GetValue(values, key);
        return string.IsNullOrWhiteSpace(value)
            ? fallback
            : int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : fallback;
    }

    private static string? ReadJsonString(string json, string property)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (JsonProperty member in document.RootElement.EnumerateObject())
            {
                if (NamesMatch(member.Name, property) &&
                    member.Value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(member.Value.GetString()))
                {
                    return member.Value.GetString()!.Trim();
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static bool ReadJsonBool(string json, string property, bool fallback)
    {
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                foreach (JsonProperty member in document.RootElement.EnumerateObject())
                {
                    if (NamesMatch(member.Name, property) &&
                        member.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        return member.Value.GetBoolean();
                    }
                }
            }
            catch (JsonException)
            {
            }
        }

        return fallback;
    }

    private static int ReadJsonInt(string json, string property, int fallback)
    {
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                foreach (JsonProperty member in document.RootElement.EnumerateObject())
                {
                    if (NamesMatch(member.Name, property) &&
                        member.Value.ValueKind == JsonValueKind.Number &&
                        member.Value.TryGetInt32(out int parsed))
                    {
                        return parsed;
                    }
                }
            }
            catch (JsonException)
            {
            }
        }

        return fallback;
    }
}
