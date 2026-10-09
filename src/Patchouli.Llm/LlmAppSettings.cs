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
    public const int DefaultContextWindowTokens = 128000;

    /// <summary>Retains an explicitly added connection even before its fields and credentials are complete.</summary>
    public bool IsAdded { get; init; }

    /// <summary>Context capacity of the selected model, including input and output tokens.</summary>
    public int ContextWindowTokens { get; init; } = DefaultContextWindowTokens;

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
}

/// <summary>
/// The JSON "Llm" settings section. Follows the existing settings convention (a record that the host
/// loads, saves and field-level merges) so the settings page only binds fields.
/// </summary>
public sealed record LlmAppSettings(
    IReadOnlyList<LlmProviderAppSettings> Providers,
    string OcrProviderId,
    string OcrModel,
    string TranslationProviderId,
    string TranslationModel,
    string TargetLanguage,
    int TranslationWindowRadius,
    bool BackfillPreviousWindowTranslation)
{
    /// <summary>Largest accepted sliding-window radius (D5 keeps the default at 1).</summary>
    public const int MaxTranslationWindowRadius = 5;

    /// <summary>Fallback target language (D6) when settings carry no usable value.</summary>
    public const string FallbackTargetLanguage = "en";

    private static readonly string[] TargetLanguageFallbacks = ["en", "ja", "zh-Hans"];

    /// <summary>Default model for the default provider, used before the user picks anything.</summary>
    private const string DefaultOpenAiModel = "gpt-4o-mini";

    /// <summary>Provider used when a configured provider id is blank or unknown.</summary>
    public const string DefaultProviderId = "openai";

    /// <summary>
    /// Defaults: one row per catalog provider, the default provider's row pre-filled with the default model so
    /// a fresh install can already call it, and the D5/D6 translation defaults (window radius 1, language
    /// fallback, previous-window backfill on).
    /// </summary>
    public static LlmAppSettings Default()
    {
        LlmProviderAppSettings[] providers = LlmProviderCatalog.All
            .Select(entry => string.Equals(entry.ProviderId, DefaultProviderId, StringComparison.OrdinalIgnoreCase)
                ? LlmProviderAppSettings.FromCatalog(entry) with { Model = DefaultOpenAiModel }
                : LlmProviderAppSettings.FromCatalog(entry))
            .ToArray();

        return new LlmAppSettings(
            providers,
            DefaultProviderId,
            DefaultOpenAiModel,
            DefaultProviderId,
            DefaultOpenAiModel,
            FallbackTargetLanguage,
            1,
            true);
    }

    /// <summary>Language codes offered by the settings form when nothing else is configured.</summary>
    public static IReadOnlyList<string> SuggestedTargetLanguages => TargetLanguageFallbacks;

    /// <summary>Default provider/model pair used when the OCR scope has no usable configuration.</summary>
    [JsonIgnore]
    public (string ProviderId, string Model) OcrSelection => ResolveSelection(OcrProviderId, OcrModel);

    /// <summary>Default provider/model pair used when the translation scope has no usable configuration.</summary>
    [JsonIgnore]
    public (string ProviderId, string Model) TranslationSelection =>
        ResolveSelection(TranslationProviderId, TranslationModel);

    /// <summary>Target language, normalized to the fallback when blank.</summary>
    [JsonIgnore]
    public string EffectiveTargetLanguage =>
        string.IsNullOrWhiteSpace(TargetLanguage) ? FallbackTargetLanguage : TargetLanguage.Trim();

    /// <summary>Sliding-window radius clamped to [0, <see cref="MaxTranslationWindowRadius"/>] (D5).</summary>
    [JsonIgnore]
    public int EffectiveTranslationWindowRadius => ClampWindowRadius(TranslationWindowRadius);

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

    /// <summary>
    /// Replaces or adds one provider row, keeping catalog order so the persisted JSON is stable.
    /// </summary>
    public LlmAppSettings WithProvider(LlmProviderAppSettings provider)
    {
        string providerId = provider.ProviderId.Trim();
        bool replaced = FindProvider(providerId) is not null;
        List<LlmProviderAppSettings> merged = [];
        foreach (LlmProviderCatalogEntry entry in LlmProviderCatalog.All)
        {
            if (string.Equals(entry.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))
            {
                merged.Add(provider);
                continue;
            }

            LlmProviderAppSettings? existing = FindProvider(entry.ProviderId);
            if (existing is not null)
            {
                merged.Add(existing);
            }
        }

        return this with { Providers = replaced ? merged : [.. merged, provider] };
    }

    /// <summary>
    /// Normalizes a persisted instance: every catalog entry gets exactly one row, values are clamped and
    /// the default provider/model selections fall back when blank.
    /// </summary>
    public LlmAppSettings Normalize()
    {
        List<LlmProviderAppSettings> normalized = [];
        foreach (LlmProviderCatalogEntry entry in LlmProviderCatalog.All)
        {
            LlmProviderAppSettings row = FindProvider(entry.ProviderId) ?? LlmProviderAppSettings.FromCatalog(entry);
            normalized.Add(row with
            {
                ProviderId = entry.ProviderId,
                DisplayName = string.IsNullOrWhiteSpace(row.DisplayName) ? entry.DisplayName : row.DisplayName.Trim(),
                BaseUrl = row.BaseUrl.Trim(),
                Model = row.Model.Trim(),
                Subscription = row.Subscription.Trim(),
                Deployment = row.Deployment.Trim(),
                ApiVersion = row.ApiVersion.Trim(),
                ContextWindowTokens = Math.Max(4096, row.ContextWindowTokens),
                AuthenticationMode = LlmSubscriptionCatalog.Find(entry.ProviderId) is not null
                    ? LlmAuthenticationModes.Subscription
                    : LlmAuthenticationModes.ApiKey
            });
        }

        (string ocrProviderId, string ocrModel) = ResolveSelection(OcrProviderId, OcrModel);
        (string translationProviderId, string translationModel) =
            ResolveSelection(TranslationProviderId, TranslationModel);
        return this with
        {
            Providers = normalized,
            OcrProviderId = ocrProviderId,
            OcrModel = ocrModel,
            TranslationProviderId = translationProviderId,
            TranslationModel = translationModel,
            TargetLanguage = EffectiveTargetLanguage,
            TranslationWindowRadius = EffectiveTranslationWindowRadius
        };
    }

    /// <summary>Clamps a window radius into the accepted range.</summary>
    public static int ClampWindowRadius(int radius)
    {
        return Math.Clamp(radius, 0, MaxTranslationWindowRadius);
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
