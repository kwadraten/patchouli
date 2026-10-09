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
    public static string FromStatusCode(HttpStatusCode statusCode)
    {
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
                                                                 HttpStatusCode.ServiceUnavailable),
            IOException => LlmFailureCodes.NetworkTimeout,
            _ => LlmFailureCodes.UnknownProviderError
        };
    }
}
