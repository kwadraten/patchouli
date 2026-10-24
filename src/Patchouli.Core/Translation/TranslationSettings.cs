namespace Patchouli.Core.Translation;

using Bibliography;

/// <summary>Error codes reported by the translation extension contracts.</summary>
public static class TranslationErrorCodes
{
    /// <summary>The model response could not be parsed into the structured batch protocol.</summary>
    public const string InvalidModelResponse = "translation_invalid_model_response";

    /// <summary>The structured batch result does not align with the requested segment ids.</summary>
    public const string BatchMismatch = "translation_batch_mismatch";

    /// <summary>No model invoker is configured for the translation pipeline.</summary>
    public const string InvokerUnavailable = "translation_invoker_unavailable";
}

/// <summary>
/// Runtime settings for one translation run. This is the engine-facing shape; the persisted
/// app-settings shape lives in <c>Patchouli.Llm.TranslationAppSettings</c> and maps onto this record.
/// A null <see cref="ProviderId"/>/<see cref="Model"/> means "use the default chat provider/model".
/// </summary>
public sealed record TranslationSettings(
    string TargetLanguage = "en",
    int BatchSize = 8,
    int MaxBatchCharacters = 24000,
    int? MaxBatchTokens = null,
    int Concurrency = 4,
    TimeSpan? RequestInterval = null,
    TimeSpan? RequestTimeout = null,
    int MaxRetries = 2,
    int WindowRadius = 1,
    bool BackfillPreviousWindowTranslation = true,
    string? ProviderId = null,
    string? Model = null,
    string EngineId = TranslationSettings.DefaultEngineId,
    string SourceLanguage = TranslationSettings.DefaultSourceLanguage)
{
    public const string DefaultTargetLanguage = "en";
    public const string DefaultSourceLanguage = "auto";
    public const string DefaultEngineId = "translation.microsoft-free";
    public const int MinBatchSize = 1;
    public const int MaxBatchSize = 64;
    public const int DefaultBatchSize = 8;

    public const int MinMaxBatchCharacters = 1000;
    public const int MaxMaxBatchCharacters = 200000;
    public const int DefaultMaxBatchCharacters = 24000;

    public const int MinConcurrency = 1;
    public const int MaxConcurrency = 16;
    public const int DefaultConcurrency = 4;

    public const int MinMaxRetries = 0;
    public const int MaxMaxRetries = 10;
    public const int DefaultMaxRetries = 2;

    public const int MinWindowRadius = 0;
    public const int MaxWindowRadius = 5;
    public const int DefaultWindowRadius = 1;

    public static TimeSpan DefaultRequestTimeout { get; } = TimeSpan.FromMinutes(2);

    /// <summary>Delay between parallel batch requests; zero means no throttle.</summary>
    public TimeSpan RequestIntervalValue => RequestInterval ?? TimeSpan.Zero;

    /// <summary>Per-request timeout; the default bounds one batch translation call.</summary>
    public TimeSpan RequestTimeoutValue => RequestTimeout ?? DefaultRequestTimeout;

    /// <summary>
    /// Clamps every ranged value back into its legal band and canonicalizes the language codes
    /// through <see cref="ItemLanguage.Canonicalize"/>, so a run's languages are host canonical
    /// tags (zh-Hans style) before any job is created from them.
    /// </summary>
    public TranslationSettings Normalize()
    {
        string language = string.IsNullOrWhiteSpace(TargetLanguage)
            ? DefaultTargetLanguage
            : ItemLanguage.Canonicalize(TargetLanguage) ?? DefaultTargetLanguage;
        string sourceLanguage = string.IsNullOrWhiteSpace(SourceLanguage)
            ? DefaultSourceLanguage
            : ItemLanguage.Canonicalize(SourceLanguage) ?? DefaultSourceLanguage;
        return this with
        {
            TargetLanguage = language,
            SourceLanguage = sourceLanguage,
            EngineId = string.IsNullOrWhiteSpace(EngineId) ? DefaultEngineId : EngineId.Trim(),
            BatchSize = Math.Clamp(BatchSize, MinBatchSize, MaxBatchSize),
            MaxBatchCharacters = Math.Clamp(MaxBatchCharacters, MinMaxBatchCharacters, MaxMaxBatchCharacters),
            MaxBatchTokens = MaxBatchTokens is > 0 ? MaxBatchTokens : null,
            Concurrency = Math.Clamp(Concurrency, MinConcurrency, MaxConcurrency),
            RequestInterval = RequestInterval is { } interval && interval > TimeSpan.Zero ? interval : null,
            RequestTimeout = RequestTimeout is { } timeout && timeout > TimeSpan.Zero ? timeout : null,
            MaxRetries = Math.Clamp(MaxRetries, MinMaxRetries, MaxMaxRetries),
            WindowRadius = Math.Clamp(WindowRadius, MinWindowRadius, MaxWindowRadius),
            ProviderId = string.IsNullOrWhiteSpace(ProviderId) ? null : ProviderId.Trim(),
            Model = string.IsNullOrWhiteSpace(Model) ? null : Model.Trim()
        };
    }
}
