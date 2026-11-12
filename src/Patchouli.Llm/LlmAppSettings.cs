using System.Text.Json.Serialization;

namespace Patchouli.Llm;

/// <summary>
/// Provider configuration for one entry of <see cref="LlmProviderCatalog"/>. The API key is never part
/// of this record: it lives in the existing <c>ICredentialStore</c> under
/// <see cref="CredentialProviderId"/> (D2), so a settings file can be shared or synced without leaking a
/// secret.
/// </summary>
/// <param name="ProviderId">Catalog id; also the credential provider id.</param>
/// <param name="DisplayName">Display name captured at save time so a renamed catalog entry stays readable.</param>
/// <param name="BaseUrl">Explicit endpoint; empty means the catalog default.</param>
/// <param name="Model">Default model for this provider; empty means the user must pick one at call time.</param>
/// <param name="Subscription">Azure OpenAI resource (subscription) name.</param>
/// <param name="Deployment">Azure OpenAI deployment name.</param>
/// <param name="ApiVersion">Azure OpenAI REST api-version override.</param>
public sealed record LlmProviderAppSettings(
    string ProviderId,
    string DisplayName = "",
    string BaseUrl = "",
    string Model = "",
    string Subscription = "",
    string Deployment = "",
    string ApiVersion = "",
    string AuthenticationMode = LlmAuthenticationModes.ApiKey)
{
    public const int DefaultContextWindowTokens = 256000;

    /// <summary>Retains an explicitly added connection even before its fields and credentials are complete.</summary>
    public bool IsAdded { get; init; }

    /// <summary>The credential store lookup key for this provider. Exactly one key per provider (D2).</summary>
    [JsonIgnore]
    public string CredentialProviderId => AuthenticationMode == LlmAuthenticationModes.Subscription
        ? LlmSubscriptionCatalog.CredentialProviderId(ProviderId)
        : ProviderId.Trim().ToLowerInvariant();

    /// <summary>Builds the default settings row for a catalog entry.</summary>
    public static LlmProviderAppSettings FromCatalog(LlmProviderCatalogEntry entry)
    {
        return new LlmProviderAppSettings(entry.ProviderId, entry.DisplayName, entry.DefaultBaseUrl,
            AuthenticationMode: LlmSubscriptionCatalog.Find(entry.ProviderId) is not null
                ? LlmAuthenticationModes.Subscription
                : LlmAuthenticationModes.ApiKey);
    }

    public bool IsImplicitlyEmpty()
    {
        if (IsAdded ||
            !string.IsNullOrWhiteSpace(Subscription) || !string.IsNullOrWhiteSpace(Deployment) ||
            !string.IsNullOrWhiteSpace(ApiVersion))
        {
            return false;
        }

        string model = Model.Trim();
        bool baselineModel = string.Equals(ProviderId.Trim(), "openai", StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(model, "gpt-4o-mini", StringComparison.OrdinalIgnoreCase);
        if (model.Length != 0 && !baselineModel)
        {
            return false;
        }

        string expectedAuthentication = LlmSubscriptionCatalog.Find(ProviderId) is not null
            ? LlmAuthenticationModes.Subscription
            : LlmAuthenticationModes.ApiKey;
        if (!string.Equals(AuthenticationMode, expectedAuthentication, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        LlmProviderCatalogEntry? entry = LlmProviderCatalog.Find(ProviderId);
        string displayName = DisplayName.Trim();
        if (displayName.Length != 0 &&
            !string.Equals(displayName, entry?.DisplayName, StringComparison.OrdinalIgnoreCase) &&
            !LlmProviderCatalog.IsLegacyDisplayName(ProviderId, displayName))
        {
            return false;
        }

        string baseUrl = BaseUrl.Trim();
        return baseUrl.Length == 0 ||
               string.Equals(baseUrl, entry?.DefaultBaseUrl, StringComparison.OrdinalIgnoreCase) ||
               LlmProviderCatalog.IsLegacyDefaultBaseUrl(ProviderId, baseUrl);
    }
}

/// <summary>
/// The JSON "Llm" settings section. Follows the existing settings convention (a record that the host
/// loads, saves and field-level merges) so the settings page only binds fields.
/// </summary>
public sealed record LlmAppSettings(
    IReadOnlyList<LlmProviderAppSettings> Providers,
    string OcrProviderId,
    string OcrModel,
    string ChatProviderId,
    string ChatModel)
{
    /// <summary>Local layout detection is the default; weights are always supplied by the user.</summary>
    public bool OcrUseLocalLayout { get; init; } = true;

    public string OcrLayoutModel { get; init; } = OcrLayoutModelIds.PpDocLayoutV3;
    public string OcrReasoningEffort { get; init; } = "none";
    public int OcrConcurrency { get; init; } = OcrPerformanceSettings.DefaultConcurrency;

    /// <summary>
    /// Direct translation engine settings. A blank <see cref="TranslationAppSettings.ProviderId"/> or
    /// <see cref="TranslationAppSettings.Model"/> falls back to the chat selection where it is read; the
    /// chat values are never copied into this subsection.
    /// </summary>
    public TranslationAppSettings Translation { get; init; } = new();

    public static IReadOnlyList<string> ReasoningEfforts { get; } =
        ["", "none", "minimal", "low", "medium", "high", "xhigh", "max"];

    public static string NormalizeReasoningEffort(string? value)
    {
        string normalized = value?.Trim().ToLowerInvariant() ?? "";
        return ReasoningEfforts.Contains(normalized) ? normalized : "";
    }

    /// <summary>Default model for the default provider, used before the user picks anything.</summary>
    private const string DefaultOpenAiModel = "gpt-4o-mini";

    /// <summary>Provider used when a configured provider id is blank or unknown.</summary>
    public const string DefaultProviderId = "openai";

    public static LlmAppSettings Default()
    {
        return new LlmAppSettings(
            [],
            DefaultProviderId,
            DefaultOpenAiModel,
            DefaultProviderId,
            DefaultOpenAiModel);
    }

    /// <summary>Default provider/model pair used when the OCR scope has no usable configuration.</summary>
    [JsonIgnore]
    public (string ProviderId, string Model) OcrSelection => ResolveSelection(OcrProviderId, OcrModel);

    /// <summary>Provider/model pair used by ordinary chat sessions.</summary>
    [JsonIgnore]
    public (string ProviderId, string Model) ChatSelection => ResolveSelection(ChatProviderId, ChatModel);

    /// <summary>Settings row for a provider id, or null when the catalog entry has no row yet.</summary>
    public LlmProviderAppSettings? FindProvider(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return null;
        }

        return Providers.FirstOrDefault(provider =>
            string.Equals(provider.ProviderId.Trim(), providerId.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public LlmAppSettings WithProvider(LlmProviderAppSettings provider)
    {
        string providerId = provider.ProviderId.Trim();
        bool replaced = false;
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<LlmProviderAppSettings> merged = [];
        foreach (LlmProviderAppSettings existing in Providers)
        {
            if (string.Equals(existing.ProviderId.Trim(), providerId, StringComparison.OrdinalIgnoreCase))
            {
                if (!replaced)
                {
                    merged.Add(provider);
                    replaced = true;
                }

                continue;
            }

            if (seen.Add(existing.ProviderId.Trim()))
            {
                merged.Add(existing);
            }
        }

        if (!replaced)
        {
            merged.Add(provider);
        }

        return this with { Providers = OrderProviders(merged) };
    }

    public LlmAppSettings WithoutProvider(string providerId)
    {
        string id = providerId.Trim();
        return this with
        {
            Providers = Providers
                .Where(existing => !string.Equals(existing.ProviderId.Trim(), id, StringComparison.OrdinalIgnoreCase))
                .ToArray()
        };
    }

    public LlmAppSettings Normalize()
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<LlmProviderAppSettings> normalized = [];
        foreach (LlmProviderAppSettings row in Providers)
        {
            string providerId = row.ProviderId.Trim();
            if (providerId.Length == 0 || !seen.Add(providerId))
            {
                continue;
            }

            LlmProviderCatalogEntry? entry = LlmProviderCatalog.Find(providerId);
            LlmProviderAppSettings normalizedRow = row with
            {
                ProviderId = entry?.ProviderId ?? providerId,
                DisplayName = string.IsNullOrWhiteSpace(row.DisplayName)
                    ? entry?.DisplayName ?? providerId
                    : row.DisplayName.Trim(),
                BaseUrl = row.BaseUrl.Trim(),
                Model = row.Model.Trim(),
                Subscription = row.Subscription.Trim(),
                Deployment = row.Deployment.Trim(),
                ApiVersion = row.ApiVersion.Trim(),
                AuthenticationMode = LlmSubscriptionCatalog.Find(providerId) is not null
                    ? LlmAuthenticationModes.Subscription
                    : LlmAuthenticationModes.ApiKey
            };
            normalized.Add(normalizedRow);
        }

        (string ocrProviderId, string ocrModel) = ResolveSelection(OcrProviderId, OcrModel);
        (string chatProviderId, string chatModel) =
            ResolveSelection(ChatProviderId, ChatModel);
        return this with
        {
            Providers = OrderProviders(normalized),
            OcrProviderId = ocrProviderId,
            OcrModel = ocrModel,
            OcrLayoutModel = OcrLayoutModelIds.Normalize(OcrLayoutModel),
            OcrReasoningEffort = OcrPerformanceSettings.NormalizeReasoning(OcrReasoningEffort),
            OcrConcurrency = Math.Clamp(OcrConcurrency, 1, OcrPerformanceSettings.MaxConcurrency),
            ChatProviderId = chatProviderId,
            ChatModel = chatModel,
            Translation = Translation.Normalize()
        };
    }

    private static List<LlmProviderAppSettings> OrderProviders(IReadOnlyList<LlmProviderAppSettings> rows)
    {
        return rows
            .Select((row, index) => (row, index))
            .OrderBy(item => LlmProviderCatalog.CatalogOrder(item.row.ProviderId))
            .ThenBy(item => item.index)
            .Select(item => item.row)
            .ToList();
    }

    /// <summary>
    /// Resolves a provider/model pair: an unknown provider falls back to the default provider and a blank
    /// model falls back to the model configured for that provider row (possibly still blank).
    /// </summary>
    private (string ProviderId, string Model) ResolveSelection(string? providerId, string? model)
    {
        LlmProviderCatalogEntry entry = LlmProviderCatalog.Resolve(providerId, DefaultProviderId);
        string resolvedModel = model?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(resolvedModel))
        {
            resolvedModel = FindProvider(entry.ProviderId)?.Model.Trim() ?? "";
        }

        return (entry.ProviderId, resolvedModel);
    }
}
