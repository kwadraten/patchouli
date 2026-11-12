using System.Diagnostics;
using System.Text.Json;
using FSharp.ModelsDev.Catalog;

namespace Patchouli.Llm;

/// <summary>Local model suggestions with a replaceable, best-effort models.dev snapshot.</summary>
public sealed class LlmModelCatalog
{
    private const int MaxSnapshotBytes = 16 * 1024 * 1024;

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
        MaxResponseContentBufferSize = MaxSnapshotBytes
    };

    private static readonly IModelCatalog Embedded = SnapshotCatalog.Default;

    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private IReadOnlyDictionary<string, IReadOnlyDictionary<string, ModelInfo>> _latest =
        new Dictionary<string, IReadOnlyDictionary<string, ModelInfo>>(StringComparer.Ordinal);

    public static LlmModelCatalog Shared { get; } = new();

    public IReadOnlyList<string> GetModelIds(string? providerId)
    {
        // Azure uses user-defined deployment names. Subscription models come from account discovery.
        if (string.IsNullOrWhiteSpace(providerId) || providerId is "azure-openai" or "openai-subscription" or "custom")
        {
            return [];
        }

        string catalogId = CatalogId(providerId);
        IEnumerable<ModelInfo> models =
            Volatile.Read(ref _latest).TryGetValue(catalogId, out IReadOnlyDictionary<string, ModelInfo>? refreshed)
                ? refreshed.Values
                : Embedded.GetModels(catalogId);
        return models.Where(model => model.OutputModalities.Count == 0 || model.OutputModalities.Contains("text"))
            .Select(model => model.ModelId).Order(StringComparer.Ordinal).ToArray();
    }

    internal ModelInfo? Find(string providerId, string model)
    {
        string catalogId = CatalogId(providerId);
        if (Volatile.Read(ref _latest).TryGetValue(catalogId, out IReadOnlyDictionary<string, ModelInfo>? models) &&
            models.TryGetValue(model, out ModelInfo? refreshed))
        {
            return refreshed;
        }

        return Embedded.TryGet(catalogId, model, out ModelInfo info) ? info : null;
    }

    internal static int ResolveContextWindow(string providerId, string model)
    {
        return ToTokenLimit(Shared.Find(providerId, model)?.ContextLimit) ??
               LlmProviderAppSettings.DefaultContextWindowTokens;
    }

    internal static int? ToTokenLimit(long? value)
    {
        return value is > 0 and <= int.MaxValue ? (int)value.Value : null;
    }

    private static string CatalogId(string providerId)
    {
        return providerId switch
        {
            "alibaba" => "alibaba-cn",
            "moonshotai" => "moonshotai-cn",
            "azure-openai" => "azure",
            "openai-subscription" => "openai-codex",
            _ => providerId
        };
    }

    public Task RefreshInBackgroundAsync(string cacheDirectory)
    {
        return Task.Run(() => RefreshAsync(cacheDirectory, Http));
    }

    internal async Task RefreshAsync(string cacheDirectory, HttpClient http)
    {
        if (!await _refreshGate.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        string path = Path.Combine(cacheDirectory, "models-dev.json");
        try
        {
            try
            {
                FileInfo cache = new(path);
                if (cache.Exists && cache.Length <= MaxSnapshotBytes)
                {
                    byte[] bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
                    Publish(bytes);
                    if (DateTime.UtcNow - cache.LastWriteTimeUtc < TimeSpan.FromDays(1))
                    {
                        return;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                Debug.WriteLine($"models.dev cache read: {exception.Message}");
            }

            byte[] snapshot = await http.GetByteArrayAsync("https://models.dev/api.json").ConfigureAwait(false);
            Publish(snapshot);
            Directory.CreateDirectory(cacheDirectory);
            string staging = $"{path}.{Guid.NewGuid():N}.new";
            try
            {
                await File.WriteAllBytesAsync(staging, snapshot).ConfigureAwait(false);
                File.Move(staging, path, true);
            }
            finally
            {
                File.Delete(staging);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or
                                              IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"models.dev refresh: {exception.Message}");
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void Publish(byte[] snapshot)
    {
        if (snapshot.Length > MaxSnapshotBytes)
        {
            throw new JsonException("The model catalog exceeds its size limit.");
        }

        using JsonDocument json = JsonDocument.Parse(snapshot);
        if (json.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Invalid model catalog.");
        }

        Dictionary<string, IReadOnlyDictionary<string, ModelInfo>> providers = new(StringComparer.Ordinal);
        foreach (JsonProperty provider in json.RootElement.EnumerateObject())
        {
            if (provider.Value.ValueKind != JsonValueKind.Object ||
                !provider.Value.TryGetProperty("models", out JsonElement models) ||
                models.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            Dictionary<string, ModelInfo> entries = new(StringComparer.Ordinal);
            foreach (JsonProperty model in models.EnumerateObject())
            {
                if (model.Value.ValueKind == JsonValueKind.Object)
                {
                    entries[model.Name] = ReadModel(provider.Name, model);
                }
            }

            providers[provider.Name] = entries;
        }

        if (providers.Count == 0 || providers.Values.All(models => models.Count == 0))
        {
            throw new JsonException("The model catalog contains no text models.");
        }

        Volatile.Write(ref _latest, providers);
    }

    private static ModelInfo ReadModel(string provider, JsonProperty model)
    {
        JsonElement limits = Property(model.Value, "limit");
        JsonElement output = Property(Property(model.Value, "modalities"), "output");
        ModelMetadata metadata = new()
        {
            MaxInputTokens = Limit(limits, "input"),
            OutputModalities = output.ValueKind == JsonValueKind.Array
                ? output.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
                    .Select(value => value.GetString()!).ToArray()
                : []
        };
        JsonElement name = Property(model.Value, "name");
        ModelCapabilities capabilities = Property(model.Value, "reasoning").ValueKind == JsonValueKind.True
            ? ModelCapabilities.Reasoning
            : ModelCapabilities.None;
        return new ModelInfo(provider, model.Name,
            name.ValueKind == JsonValueKind.String ? name.GetString() : model.Name, model.Name,
            capabilities, Limit(limits, "context"), Limit(limits, "output"), [], metadata);
    }

    private static long? Limit(JsonElement limits, string name)
    {
        JsonElement limit = Property(limits, name);
        return limit.ValueKind == JsonValueKind.Number && limit.TryGetInt64(out long value) && value > 0 ? value : null;
    }

    private static JsonElement Property(JsonElement parent, string name)
    {
        return parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement property)
            ? property
            : default;
    }
}
