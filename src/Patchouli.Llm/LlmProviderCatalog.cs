using LlmTornado.Code;

namespace Patchouli.Llm;

/// <summary>
/// One selectable provider connection type. The settings page uses this catalog for progressive
/// configuration; API and subscription connections can coexist for the same vendor.
/// </summary>
/// <param name="ProviderId">
/// Stable lowercase id persisted in settings JSON and used as the credential provider id.
/// </param>
/// <param name="DisplayName">Name shown in the settings form.</param>
/// <param name="Vendor">The LlmTornado provider enum value used to build the API client.</param>
/// <param name="DefaultBaseUrl">
/// Default endpoint. Empty means "use the vendor default supplied by LlmTornado". A base URL that only
/// carries {0} ({api-version}) and {1} ({endpoint}) placeholders is passed to <c>TornadoApi.ApiUrlFormat</c>
/// verbatim; any other value is treated as a host or root prefix and completed by the adapter.
/// </param>
/// <param name="RequiresSubscription">
/// True for Azure OpenAI, whose endpoint embeds the resource (subscription) name.
/// </param>
/// <param name="RequiresDeployment">True when the model id is a deployment name rather than a model name.</param>
public sealed record LlmProviderCatalogEntry(
    string ProviderId,
    string DisplayName,
    LLmProviders Vendor,
    string DefaultBaseUrl,
    bool RequiresSubscription = false,
    bool RequiresDeployment = false)
{
    /// <summary>True for Azure OpenAI: the settings form additionally asks for subscription and deployment.</summary>
    public bool IsAzure => RequiresSubscription || RequiresDeployment;

    /// <summary>
    /// True when this provider must be told an explicit <c>baseUrl</c> before it can be called, which is
    /// exactly the entry whose <see cref="DefaultBaseUrl"/> is empty. Keeping this derived from the data (not
    /// from a second hand-maintained flag) means a new provider cannot claim a default endpoint it lacks.
    /// </summary>
    public bool RequiresBaseUrl => string.IsNullOrWhiteSpace(DefaultBaseUrl);
}

/// <summary>
/// The data-driven provider directory (D3). Every LlmTornado vendor has an API entry; supported
/// subscriptions have separate entries. Vendor lookup prefers the ordinary API connection.
/// </summary>
public static class LlmProviderCatalog
{
    /// <summary>Settings JSON section name and credential provider id prefix.</summary>
    public const string SectionName = "Llm";

    // LlmTornado.Code.LLmProviders.Unknown and .Length are sentinels, not providers, and are
    // deliberately absent. LlmProviderCatalogTests asserts the mirror image of this list
    // (the enum minus the sentinels) so a new LlmTornado provider fails the build's test run
    // loudly instead of being silently unreachable from settings.
    private static readonly LlmProviderCatalogEntry[] Entries =
    [
        new("openai", "OpenAI", LLmProviders.OpenAi, "https://api.openai.com"),
        new("openai-subscription", "ChatGPT / Codex 订阅", LLmProviders.OpenAi, "https://chatgpt.com/backend-api/codex"),
        new("anthropic", "Anthropic", LLmProviders.Anthropic, "https://api.anthropic.com"),
        new("azure-openai", "Azure OpenAI", LLmProviders.AzureOpenAi,
            "https://{resource}.openai.azure.com/openai/deployments/{deployment}", true, true),
        new("cohere", "Cohere", LLmProviders.Cohere, "https://api.cohere.com"),
        new("google", "Google Gemini", LLmProviders.Google, "https://generativelanguage.googleapis.com"),
        new("groq", "Groq", LLmProviders.Groq, "https://api.groq.com"),
        new("deepseek", "DeepSeek", LLmProviders.DeepSeek, "https://api.deepseek.com"),
        new("mistral", "Mistral", LLmProviders.Mistral, "https://api.mistral.ai"),
        new("xai", "xAI", LLmProviders.XAi, "https://api.x.ai"),
        new("perplexity", "Perplexity", LLmProviders.Perplexity, "https://api.perplexity.ai"),
        new("voyage", "Voyage", LLmProviders.Voyage, "https://api.voyageai.com"),
        new("deepinfra", "DeepInfra", LLmProviders.DeepInfra, "https://api.deepinfra.com"),
        new("openrouter", "OpenRouter", LLmProviders.OpenRouter, "https://openrouter.ai/api"),
        new("moonshotai", "Moonshot AI", LLmProviders.MoonshotAi, "https://api.moonshot.cn"),
        new("zai", "Z.ai", LLmProviders.Zai, "https://api.z.ai/api/paas"),
        new("blablador", "Blablador", LLmProviders.Blablador, "https://api.blablador.org"),
        new("alibaba", "Alibaba Cloud (Qwen)", LLmProviders.Alibaba,
            "https://dashscope.aliyuncs.com/compatible-mode"),
        new("requesty", "Requesty", LLmProviders.Requesty, "https://router.requesty.ai"),
        new("upstage", "Upstage", LLmProviders.Upstage, "https://api.upstage.ai"),
        new("minimax", "MiniMax", LLmProviders.MiniMax, "https://api.minimax.chat"),
        new("litellm", "LiteLLM (self-hosted)", LLmProviders.LiteLlm, "http://localhost:4000"),
        new("typesafe", "TypeSafe (self-hosted)", LLmProviders.TypeSafe, "http://localhost:8000"),
        new("custom", "Custom OpenAI-compatible endpoint", LLmProviders.Custom, "")
    ];

    private static readonly Dictionary<string, LlmProviderCatalogEntry> ByIdMap =
        Entries.ToDictionary(entry => entry.ProviderId, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<LLmProviders, LlmProviderCatalogEntry> ByVendorMap =
        Entries.GroupBy(entry => entry.Vendor).ToDictionary(group => group.Key, group => group.First());

    /// <summary>All available connection types in display order.</summary>
    public static IReadOnlyList<LlmProviderCatalogEntry> All { get; } = Entries;

    /// <summary>Catalog ids in display order.</summary>
    public static IReadOnlyList<string> ProviderIds { get; } = Entries.Select(entry => entry.ProviderId).ToArray();

    /// <summary>Resolves a persisted provider id; returns null when the id is unknown or empty.</summary>
    public static LlmProviderCatalogEntry? Find(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return null;
        }

        return ByIdMap.GetValueOrDefault(providerId.Trim());
    }

    /// <summary>Resolves the catalog entry for a LlmTornado vendor value.</summary>
    public static LlmProviderCatalogEntry? ByVendor(LLmProviders vendor)
    {
        return ByVendorMap.GetValueOrDefault(vendor);
    }

    /// <summary>Resolves a persisted provider id, falling back to <paramref name="fallbackProviderId"/>.</summary>
    public static LlmProviderCatalogEntry Resolve(string? providerId, string fallbackProviderId)
    {
        return Find(providerId) ?? Find(fallbackProviderId) ?? All[0];
    }

    /// <summary>LlmTornado vendor values that are real providers (the enum minus its sentinels).</summary>
    public static IReadOnlyList<LLmProviders> SelectableVendors { get; } =
        Enum.GetValues<LLmProviders>()
            .Where(vendor => vendor is not (LLmProviders.Unknown or LLmProviders.Length))
            .ToArray();
}
