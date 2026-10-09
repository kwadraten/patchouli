namespace Patchouli.Llm;

/// <summary>
/// Stable failure codes emitted by <see cref="LlmChatClient"/> and its transports. The codes are the
/// contract with <c>OcrRetryPolicy</c> (S6): every code is classified by
/// <see cref="LlmFailureClassifier"/> as retryable, repairable-by-hand or terminal, so an OCR page
/// failure is never guessed from a message string.
/// </summary>
public static class LlmFailureCodes
{
    // Retryable: the same request can succeed later without user action.
    public const string NetworkTimeout = "network_timeout";
    public const string TemporaryProviderError = "temporary_provider_error";
    public const string InvalidModelOutput = "invalid_model_output";
    public const string RateLimited = "rate_limited";
    public const string WorkerCrashed = "worker_crashed";
    public const string Cancelled = "cancelled";

    // Manual repair: retrying unchanged cannot help.
    public const string AuthFailed = "auth_failed";
    public const string ModelNotFound = "model_not_found";
    public const string BadEndpointConfig = "bad_endpoint_config";
    public const string ContextLengthExceeded = "context_length_exceeded";
    public const string ContentFiltered = "content_filtered";
    public const string EmptyResponse = "empty_response";

    // Terminal: programming or usage error.
    public const string UnsupportedInput = "unsupported_input";
    public const string HistoryInvariantViolated = "history_invariant_violated";
    public const string UsageContractViolated = "usage_contract_violated";
    public const string InvalidPageBox = "invalid_page_box";
    public const string UnsupportedFile = "unsupported_file";
    public const string MissingExecutable = "missing_executable";
    public const string ModelPathMissing = "model_path_missing";
    public const string ModelPathInaccessible = "model_path_inaccessible";
    public const string RendererTimeout = "renderer_timeout";
    public const string ImageTooLargeForOcr = "image_too_large_for_ocr";
    public const string BBoxCoordinateTransformFailed = "bbox_coordinate_transform_failed";
    public const string SourceFileMissing = "source_file_missing";
    public const string SourceFileChanged = "source_file_changed";
    public const string Interrupted = "interrupted";
    public const string UnknownProviderError = "unknown_provider_error";
}

/// <summary>Classification values understood by the OCR retry policy.</summary>
public static class LlmFailureClassification
{
    public const string TransientRetryable = "transient_retryable";
    public const string ManualRepairRequired = "manual_repair_required";
    public const string NonRetryable = "non_retryable";
}

/// <summary>
/// Maps LLM failure codes onto the vocabulary consumed by <c>OcrRetryPolicy</c>. The adapter layer (S6)
/// passes <see cref="LlmFailureCodes"/> values through unchanged; the classification decides whether the
/// queue retries the page, asks the user to repair configuration, or fails it.
/// </summary>
public static class LlmFailureClassifier
{
    private static readonly HashSet<string> Transient = new(StringComparer.Ordinal)
    {
        LlmFailureCodes.NetworkTimeout,
        LlmFailureCodes.TemporaryProviderError,
        LlmFailureCodes.InvalidModelOutput,
        LlmFailureCodes.RateLimited,
        LlmFailureCodes.WorkerCrashed,
        LlmFailureCodes.Cancelled,
        LlmFailureCodes.Interrupted
    };

    private static readonly HashSet<string> ManualRepair = new(StringComparer.Ordinal)
    {
        LlmFailureCodes.AuthFailed,
        LlmFailureCodes.ModelNotFound,
        LlmFailureCodes.BadEndpointConfig,
        LlmFailureCodes.ContextLengthExceeded,
        LlmFailureCodes.ContentFiltered,
        LlmFailureCodes.EmptyResponse,
        LlmFailureCodes.InvalidPageBox,
        LlmFailureCodes.UnsupportedFile,
        LlmFailureCodes.MissingExecutable,
        LlmFailureCodes.ModelPathMissing,
        LlmFailureCodes.ModelPathInaccessible,
        LlmFailureCodes.RendererTimeout,
        LlmFailureCodes.ImageTooLargeForOcr,
        LlmFailureCodes.BBoxCoordinateTransformFailed,
        LlmFailureCodes.SourceFileMissing,
        LlmFailureCodes.SourceFileChanged
    };

    /// <summary>Every code this library can emit, in a stable order.</summary>
    public static IReadOnlyList<string> AllCodes { get; } =
    [
        .. Transient.OrderBy(code => code, StringComparer.Ordinal),
        .. ManualRepair.OrderBy(code => code, StringComparer.Ordinal),
        LlmFailureCodes.UnsupportedInput,
        LlmFailureCodes.HistoryInvariantViolated,
        LlmFailureCodes.UsageContractViolated,
        LlmFailureCodes.UnknownProviderError
    ];

    /// <summary>Classifies a failure code; unknown or missing codes are non-retryable.</summary>
    public static string Classify(string? errorCode)
    {
        string code = errorCode?.Trim() ?? "";
        if (Transient.Contains(code))
        {
            return LlmFailureClassification.TransientRetryable;
        }

        return ManualRepair.Contains(code)
            ? LlmFailureClassification.ManualRepairRequired
            : LlmFailureClassification.NonRetryable;
    }

    /// <summary>True when the queue should retry the same request unchanged.</summary>
    public static bool IsRetryable(string? errorCode)
    {
        return Classify(errorCode) == LlmFailureClassification.TransientRetryable;
    }

    /// <summary>True when the user must change configuration before the request can succeed.</summary>
    public static bool RequiresManualRepair(string? errorCode)
    {
        return Classify(errorCode) == LlmFailureClassification.ManualRepairRequired;
    }
}

/// <summary>
/// A classified LLM failure. <see cref="IsRetryable"/> and <see cref="RequiresManualRepair"/> are derived
/// from <see cref="ErrorCode"/> only, so retry behaviour does not depend on provider text.
/// </summary>
public sealed class LlmChatException : Exception
{
    public LlmChatException(string errorCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    /// <summary>One of <see cref="LlmFailureCodes"/>.</summary>
    public string ErrorCode { get; }

    public bool IsRetryable => LlmFailureClassifier.IsRetryable(ErrorCode);

    public bool RequiresManualRepair => LlmFailureClassifier.RequiresManualRepair(ErrorCode);

    /// <summary>Wraps an arbitrary exception with a classified code.</summary>
    public static LlmChatException From(string errorCode, Exception exception)
    {
        return new LlmChatException(errorCode, exception.Message, exception);
    }
}
