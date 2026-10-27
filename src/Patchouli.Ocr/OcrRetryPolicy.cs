using Patchouli.Llm;
using Patchouli.Ocr.MinerU;

namespace Patchouli.Ocr;

public sealed class OcrRetryPolicy : IOcrRetryPolicy
{
    private static readonly HashSet<string> Transient =
    [
        "network_timeout", "temporary_provider_error", "rate_limited", "quota_exceeded_retryable", "worker_crashed",
        OcrFailureCode.LocalOcrTimeout, MinerUProviderStatus.DownloadFailed, MinerUProviderStatus.Timeout,
        MinerUProviderStatus.UploadFailed, MinerUProviderStatus.UploadUrlFailed,
        LlmFailureCodes.Cancelled, LlmFailureCodes.Interrupted, LlmFailureCodes.InvalidModelOutput
    ];

    private static readonly HashSet<string> Manual =
    [
        "auth_failed", "model_not_found", "bad_endpoint_config", "model_path_missing", "model_path_inaccessible",
        OcrFailureCode.SourceFileMissing, OcrFailureCode.SourceFileChanged,
        OcrFailureCode.BBoxCoordinateTransformFailed, OcrFailureCode.ImageTooLargeForOcr,
        OcrFailureCode.RendererTimeout, "unsupported_file", "invalid_page_box", "missing_executable",
        LlmFailureCodes.ContextLengthExceeded, LlmFailureCodes.ContentFiltered, LlmFailureCodes.EmptyResponse
    ];

    public string Classify(string? code)
    {
        return Manual.Contains(code ?? "") ? OcrRetryClassification.ManualRepairRequired :
            Transient.Contains(code ?? "") ? OcrRetryClassification.TransientRetryable :
            OcrRetryClassification.NonRetryable;
    }

    public bool ShouldRetry(OcrQueueTask task, string? code)
    {
        return Classify(code) == OcrRetryClassification.TransientRetryable && (code == OcrFailureCode.LocalOcrTimeout
            ? task.AttemptCount < 1
            : task.AttemptCount < task.MaxAttempts);
    }

    public TimeSpan GetNextDelay(int attempt)
    {
        return attempt switch
        {
            1 => TimeSpan.FromSeconds(5), 2 => TimeSpan.FromSeconds(30), _ => TimeSpan.FromMinutes(2)
        };
    }
}
