using System.Net;

namespace Patchouli.Llm;

/// <summary>
/// Maps a transport exception or HTTP status onto <see cref="LlmFailureCodes"/>. Shared by the LlmTornado
/// transport and by <see cref="LlmChatClient"/>, so a failure keeps the same classification no matter where
/// it surfaces. Retry decisions read the classification only, never provider text.
/// </summary>
public static class LlmFailureMapper
{
    /// <summary>Maps an HTTP status onto the failure vocabulary.</summary>
    public static string FromStatusCode(HttpStatusCode statusCode, string? response = null)
    {
        if (statusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge or
                HttpStatusCode.UnprocessableEntity && IsContextLengthExceeded(response))
        {
            return LlmFailureCodes.ContextLengthExceeded;
        }

        return statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => LlmFailureCodes.AuthFailed,
            HttpStatusCode.NotFound => LlmFailureCodes.ModelNotFound,
            HttpStatusCode.TooManyRequests => LlmFailureCodes.RateLimited,
            HttpStatusCode.RequestEntityTooLarge => LlmFailureCodes.ContextLengthExceeded,
            HttpStatusCode.BadRequest => LlmFailureCodes.BadEndpointConfig,
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => LlmFailureCodes.NetworkTimeout,
            _ => LlmFailureCodes.TemporaryProviderError
        };
    }

    /// <summary>Recognizes provider context errors delivered as HTTP 400 or in-band error messages.</summary>
    public static bool IsContextLengthExceeded(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return false;
        }

        string normalized = detail.Replace('_', ' ').Replace('-', ' ').ToLowerInvariant();
        return normalized.Contains("context length exceeded", StringComparison.Ordinal) ||
               normalized.Contains("context window exceeded", StringComparison.Ordinal) ||
               normalized.Contains("maximum context length", StringComparison.Ordinal) ||
               normalized.Contains("maximum context window", StringComparison.Ordinal) ||
               normalized.Contains("max context length", StringComparison.Ordinal) ||
               normalized.Contains("exceeds the context window", StringComparison.Ordinal) ||
               normalized.Contains("too large for model context", StringComparison.Ordinal);
    }

    /// <summary>Maps a transport exception onto the failure vocabulary.</summary>
    public static string FromException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            Newtonsoft.Json.JsonException or System.Text.Json.JsonException => LlmFailureCodes.InvalidModelOutput,
            TimeoutException => LlmFailureCodes.NetworkTimeout,
            TaskCanceledException => LlmFailureCodes.NetworkTimeout,
            HttpRequestException httpException => FromStatusCode(httpException.StatusCode ??
                                                                 HttpStatusCode.ServiceUnavailable,
                httpException.Message),
            IOException => LlmFailureCodes.NetworkTimeout,
            _ => LlmFailureCodes.UnknownProviderError
        };
    }
}
