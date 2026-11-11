using FSharp.ModelsDev.Catalog;

namespace Patchouli.Llm;

/// <summary>
/// One selectable provider connection type. The settings page uses this catalog for progressive
/// configuration; API and subscription connections can coexist for the same vendor.
/// </summary>
/// <param name="ProviderId">
/// Stable lowercase id persisted in settings JSON and used as the credential provider id.
/// </param>
/// <param name="DisplayName">Name shown in the settings form.</param>
/// <param name="Protocol">The ModelsDev protocol used for this connection.</param>
/// <param name="DefaultBaseUrl">
/// Default endpoint. Empty requires a user-supplied base URL. Existing {0} (api-version)
/// and {1} (endpoint) placeholders are resolved by the transport.
/// </param>
/// <param name="RequiresSubscription">
/// True for Azure OpenAI, whose endpoint embeds the resource (subscription) name.
/// </param>
/// <param name="RequiresDeployment">True when the model id is a deployment name rather than a model name.</param>
public sealed record LlmProviderCatalogEntry(
    string ProviderId,
    string DisplayName,
    string Protocol,
    string DefaultBaseUrl,
    bool RequiresSubscription = false,
    bool RequiresDeployment = false)
{
    public bool IsAzure => RequiresSubscription || RequiresDeployment;

    public bool RequiresBaseUrl => string.IsNullOrWhiteSpace(DefaultBaseUrl);

    public bool IsSupportedProtocol => LlmProviderCatalog.IsSupportedProtocol(Protocol);
}

/// <summary>
/// Supported connection types, keyed by the IDs already stored in application settings.
/// </summary>
public static class LlmProviderCatalog
{
    /// <summary>Settings JSON section name and credential provider id prefix.</summary>
    public const string SectionName = "Llm";

    private static readonly LlmProviderCatalogEntry[] Entries =
        SnapshotCatalog.Default.ProviderIds
            .Select(providerId =>
            {
                if (!SnapshotCatalog.Default.TryGetProvider(providerId,
                        out ProviderDefinition definition))
                {
                    throw new InvalidOperationException(
                        $"Snapshot catalog lists provider '{providerId}' but TryGetProvider failed.");
                }

                return new LlmProviderCatalogEntry(definition.ProviderId, definition.Name ?? providerId,
                    definition.ProtocolId ?? "", definition.BaseUrl ?? "");
            })
            .ToArray();

    private static readonly LlmProviderCatalogEntry[] FallbackEntries =
    [
        new("openai-subscription", "ChatGPT / Codex 订阅", "codex-responses",
            "https://chatgpt.com/backend-api/codex"),
        new("azure-openai", "Azure OpenAI", "openai-chat",
            "https://{resource}.openai.azure.com/openai/deployments/{deployment}", true, true),
        new("custom", "Custom OpenAI-compatible endpoint", "openai-chat", ""),
        new("blablador", "Blablador", "openai-chat", "https://api.blablador.org"),
        new("litellm", "LiteLLM (self-hosted)", "openai-chat", "http://localhost:4000"),
        new("typesafe", "TypeSafe (self-hosted)", "openai-chat", "http://localhost:8000"),
        new("voyage", "Voyage", "openai-chat", "https://api.voyageai.com")
    ];

    private static readonly IReadOnlyDictionary<string, string> LegacyDefaultBaseUrls =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["openai"] = "https://api.openai.com",
            ["anthropic"] = "https://api.anthropic.com",
            ["cohere"] = "https://api.cohere.com",
            ["google"] = "https://generativelanguage.googleapis.com",
            ["groq"] = "https://api.groq.com",
            ["mistral"] = "https://api.mistral.ai",
            ["xai"] = "https://api.x.ai",
            ["perplexity"] = "https://api.perplexity.ai",
            ["deepinfra"] = "https://api.deepinfra.com",
            ["openrouter"] = "https://openrouter.ai/api",
            ["moonshotai"] = "https://api.moonshot.cn",
            ["zai"] = "https://api.z.ai/api/paas",
            ["alibaba"] = "https://dashscope.aliyuncs.com/compatible-mode",
            ["requesty"] = "https://router.requesty.ai",
            ["upstage"] = "https://api.upstage.ai",
            ["minimax"] = "https://api.minimax.chat"
        };

    private static readonly IReadOnlyDictionary<string, string> LegacyDisplayNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["google"] = "Google Gemini",
            ["deepinfra"] = "DeepInfra",
            ["zai"] = "Z.ai",
            ["alibaba"] = "Alibaba Cloud (Qwen)",
            ["minimax"] = "MiniMax"
        };

    private static readonly Dictionary<string, LlmProviderCatalogEntry> ByIdMap =
        Entries.ToDictionary(entry => entry.ProviderId, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, LlmProviderCatalogEntry> FallbackByIdMap =
        FallbackEntries.ToDictionary(entry => entry.ProviderId, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, int> OrderMap = Entries
        .Select((entry, index) => (entry.ProviderId, index))
        .ToDictionary(pair => pair.ProviderId, pair => pair.index, StringComparer.OrdinalIgnoreCase);

    /// <summary>All available connection types in display order.</summary>
    public static IReadOnlyList<LlmProviderCatalogEntry> All { get; } = Entries;

    /// <summary>Catalog ids in display order.</summary>
    public static IReadOnlyList<string> ProviderIds { get; } = Entries.Select(entry => entry.ProviderId).ToArray();

    public static IReadOnlyList<LlmProviderCatalogEntry> FallbackAll { get; } = FallbackEntries;

    public static bool IsSupportedApiProtocol(string? protocol)
    {
        return protocol is "openai-chat" or "anthropic-messages" or "gemini";
    }

    public static bool IsSupportedProtocol(string? protocol)
    {
        return IsSupportedApiProtocol(protocol) || protocol == "codex-responses";
    }

    public static int CatalogOrder(string? providerId)
    {
        return providerId is not null && OrderMap.TryGetValue(providerId.Trim(), out int order)
            ? order
            : int.MaxValue;
    }

    public static bool IsLegacyDefaultBaseUrl(string providerId, string? baseUrl)
    {
        return !string.IsNullOrWhiteSpace(baseUrl) &&
               LegacyDefaultBaseUrls.TryGetValue(providerId.Trim(), out string? legacy) &&
               string.Equals(legacy, baseUrl.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsLegacyDisplayName(string providerId, string? displayName)
    {
        return !string.IsNullOrWhiteSpace(displayName) &&
               LegacyDisplayNames.TryGetValue(providerId.Trim(), out string? legacy) &&
               string.Equals(legacy, displayName.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Resolves a persisted provider id; returns null when the id is unknown or empty.</summary>
    public static LlmProviderCatalogEntry? Find(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return null;
        }

        string id = providerId.Trim();
        return ByIdMap.TryGetValue(id, out LlmProviderCatalogEntry? entry)
            ? entry
            : FallbackByIdMap.GetValueOrDefault(id);
    }

    /// <summary>Resolves a persisted provider id, falling back to <paramref name="fallbackProviderId"/>.</summary>
    public static LlmProviderCatalogEntry Resolve(string? providerId, string fallbackProviderId)
    {
        return Find(providerId) ?? Find(fallbackProviderId) ?? Entries[0];
    }
}
