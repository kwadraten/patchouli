using Patchouli.Core.Translation;

namespace Patchouli.Llm;

/// <summary>
/// The persisted "Translation" subsection of the "Llm" settings. It mirrors
/// <see cref="TranslationSettings"/>, but stores the request interval and timeout as whole seconds so the
/// JSON stays stable and easy to bind. A blank <see cref="ProviderId"/> or <see cref="Model"/> means "use
/// the default chat provider/model" and is resolved by the caller instead of being copied here.
/// </summary>
public sealed record TranslationAppSettings(
    string TargetLanguage = TranslationSettings.DefaultTargetLanguage,
    int BatchSize = TranslationSettings.DefaultBatchSize,
    int MaxBatchCharacters = TranslationSettings.DefaultMaxBatchCharacters,
    int? MaxBatchTokens = null,
    int Concurrency = TranslationSettings.DefaultConcurrency,
    int RequestIntervalSeconds = TranslationAppSettings.DefaultRequestIntervalSeconds,
    int RequestTimeoutSeconds = TranslationAppSettings.DefaultRequestTimeoutSeconds,
    int MaxRetries = TranslationSettings.DefaultMaxRetries,
    int WindowRadius = TranslationSettings.DefaultWindowRadius,
    bool BackfillPreviousWindowTranslation = TranslationAppSettings.DefaultBackfillPreviousWindowTranslation,
    string? ProviderId = null,
    string? Model = null)
{
    /// <summary>Delay between batch requests; zero disables throttling, matching the runtime default.</summary>
    public const int DefaultRequestIntervalSeconds = 0;

    public const int MinRequestIntervalSeconds = 0;
    public const int MaxRequestIntervalSeconds = 600;

    /// <summary>Per-request timeout in seconds; mirrors <see cref="TranslationSettings.DefaultRequestTimeout"/>.</summary>
    public const int DefaultRequestTimeoutSeconds = 120;

    public const int MinRequestTimeoutSeconds = 1;
    public const int MaxRequestTimeoutSeconds = 3600;

    public const bool DefaultBackfillPreviousWindowTranslation = true;

    /// <summary>Clamps every ranged value back into its legal band and blanks unresolvable selections.</summary>
    public TranslationAppSettings Normalize()
    {
        return this with
        {
            TargetLanguage = string.IsNullOrWhiteSpace(TargetLanguage)
                ? TranslationSettings.DefaultTargetLanguage
                : TargetLanguage.Trim(),
            BatchSize = Math.Clamp(BatchSize, TranslationSettings.MinBatchSize, TranslationSettings.MaxBatchSize),
            MaxBatchCharacters = Math.Clamp(MaxBatchCharacters, TranslationSettings.MinMaxBatchCharacters,
                TranslationSettings.MaxMaxBatchCharacters),
            MaxBatchTokens = MaxBatchTokens is > 0 ? MaxBatchTokens : null,
            Concurrency = Math.Clamp(Concurrency, TranslationSettings.MinConcurrency,
                TranslationSettings.MaxConcurrency),
            RequestIntervalSeconds = Math.Clamp(RequestIntervalSeconds, MinRequestIntervalSeconds,
                MaxRequestIntervalSeconds),
            RequestTimeoutSeconds = Math.Clamp(RequestTimeoutSeconds, MinRequestTimeoutSeconds,
                MaxRequestTimeoutSeconds),
            MaxRetries = Math.Clamp(MaxRetries, TranslationSettings.MinMaxRetries, TranslationSettings.MaxMaxRetries),
            WindowRadius = Math.Clamp(WindowRadius, TranslationSettings.MinWindowRadius,
                TranslationSettings.MaxWindowRadius),
            ProviderId = string.IsNullOrWhiteSpace(ProviderId) ? null : ProviderId.Trim(),
            Model = string.IsNullOrWhiteSpace(Model) ? null : Model.Trim()
        };
    }

    /// <summary>Projects the persisted shape onto the engine-facing runtime settings.</summary>
    public TranslationSettings ToRuntimeSettings()
    {
        TranslationAppSettings normalized = Normalize();
        return new TranslationSettings(
            normalized.TargetLanguage,
            normalized.BatchSize,
            normalized.MaxBatchCharacters,
            normalized.MaxBatchTokens,
            normalized.Concurrency,
            normalized.RequestIntervalSeconds > 0
                ? TimeSpan.FromSeconds(normalized.RequestIntervalSeconds)
                : null,
            TimeSpan.FromSeconds(normalized.RequestTimeoutSeconds),
            normalized.MaxRetries,
            normalized.WindowRadius,
            normalized.BackfillPreviousWindowTranslation,
            normalized.ProviderId,
            normalized.Model);
    }

    /// <summary>Captures runtime settings into the persisted shape, expanding the timeout defaults.</summary>
    public static TranslationAppSettings FromRuntimeSettings(TranslationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        TranslationSettings normalized = settings.Normalize();
        return new TranslationAppSettings(
            normalized.TargetLanguage,
            normalized.BatchSize,
            normalized.MaxBatchCharacters,
            normalized.MaxBatchTokens,
            normalized.Concurrency,
            (int)Math.Clamp(normalized.RequestIntervalValue.TotalSeconds, MinRequestIntervalSeconds,
                MaxRequestIntervalSeconds),
            (int)Math.Clamp(normalized.RequestTimeoutValue.TotalSeconds, MinRequestTimeoutSeconds,
                MaxRequestTimeoutSeconds),
            normalized.MaxRetries,
            normalized.WindowRadius,
            normalized.BackfillPreviousWindowTranslation,
            normalized.ProviderId,
            normalized.Model).Normalize();
    }
}
