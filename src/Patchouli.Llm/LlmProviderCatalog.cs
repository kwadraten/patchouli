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
/// Supported connection types, keyed by the IDs already stored in application settings.
/// </summary>
public static class LlmProviderCatalog
{
    /// <summary>Settings JSON section name and credential provider id prefix.</summary>
    public const string SectionName = "Llm";

    private static readonly LlmProviderCatalogEntry[] Entries =
    [
        new("openai", "OpenAI", "openai-chat", "https://api.openai.com"),
        new("openai-subscription", "ChatGPT / Codex 订阅", "codex-responses", "https://chatgpt.com/backend-api/codex"),
        new("anthropic", "Anthropic", "anthropic-messages", "https://api.anthropic.com"),
        new("azure-openai", "Azure OpenAI", "openai-chat",
            "https://{resource}.openai.azure.com/openai/deployments/{deployment}", true, true),
        new("cohere", "Cohere", "openai-chat", "https://api.cohere.com"),
        new("google", "Google Gemini", "gemini", "https://generativelanguage.googleapis.com"),
        new("groq", "Groq", "openai-chat", "https://api.groq.com"),
        new("deepseek", "DeepSeek", "openai-chat", "https://api.deepseek.com"),
        new("mistral", "Mistral", "openai-chat", "https://api.mistral.ai"),
        new("xai", "xAI", "openai-chat", "https://api.x.ai"),
        new("perplexity", "Perplexity", "openai-chat", "https://api.perplexity.ai"),
        new("voyage", "Voyage", "openai-chat", "https://api.voyageai.com"),
        new("deepinfra", "DeepInfra", "openai-chat", "https://api.deepinfra.com"),
        new("openrouter", "OpenRouter", "openai-chat", "https://openrouter.ai/api"),
        new("moonshotai", "Moonshot AI", "openai-chat", "https://api.moonshot.cn"),
        new("zai", "Z.ai", "openai-chat", "https://api.z.ai/api/paas"),
        new("blablador", "Blablador", "openai-chat", "https://api.blablador.org"),
        new("alibaba", "Alibaba Cloud (Qwen)", "openai-chat",
            "https://dashscope.aliyuncs.com/compatible-mode"),
        new("requesty", "Requesty", "openai-chat", "https://router.requesty.ai"),
        new("upstage", "Upstage", "openai-chat", "https://api.upstage.ai"),
        new("minimax", "MiniMax", "openai-chat", "https://api.minimax.chat"),
        new("litellm", "LiteLLM (self-hosted)", "openai-chat", "http://localhost:4000"),
        new("typesafe", "TypeSafe (self-hosted)", "openai-chat", "http://localhost:8000"),
        new("custom", "Custom OpenAI-compatible endpoint", "openai-chat", "")
    ];

    private static readonly Dictionary<string, LlmProviderCatalogEntry> ByIdMap =
        Entries.ToDictionary(entry => entry.ProviderId, StringComparer.OrdinalIgnoreCase);

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

    /// <summary>Resolves a persisted provider id, falling back to <paramref name="fallbackProviderId"/>.</summary>
    public static LlmProviderCatalogEntry Resolve(string? providerId, string fallbackProviderId)
    {
        return Find(providerId) ?? Find(fallbackProviderId) ?? All[0];
    }
}
