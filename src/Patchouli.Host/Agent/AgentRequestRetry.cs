using Patchouli.Llm;

namespace Patchouli.Host.Agent;

/// <summary>Request failures that can be repeated without changing model input or executing tools.</summary>
public static class AgentRequestRetry
{
    public static bool IsRetryable(string? code)
    {
        return code is
            LlmFailureCodes.NetworkTimeout or LlmFailureCodes.TemporaryProviderError or
            LlmFailureCodes.RateLimited or LlmFailureCodes.InvalidModelOutput or LlmFailureCodes.WorkerCrashed;
    }
}
