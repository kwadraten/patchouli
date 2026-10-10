using System.Net;
using FluentAssertions;
using LlmTornado.Chat;
using LlmTornado.Code;
using Patchouli.Llm;

namespace Patchouli.Tests;

public sealed class LlmContextAccountingTests
{
    [Theory]
    [InlineData(LLmProviders.Anthropic, 100)]
    [InlineData(LLmProviders.OpenAi, 30100)]
    [InlineData(LLmProviders.Google, 30100)]
    public void Cached_input_is_included_exactly_once_in_context_usage(LLmProviders provider, int reportedPrompt)
    {
        ChatUsage usage = new(provider)
        {
            PromptTokens = reportedPrompt, CompletionTokens = 20, TotalTokens = reportedPrompt + 20,
            CacheReadTokens = 20000, CacheCreationTokens = 10000
        };
        LlmUsage normalized = LlmTornadoChatTransport.ToUsage(usage);
        normalized.PromptTokens.Should().Be(30100);
        normalized.TotalTokens.Should().Be(30120);
        normalized.CacheReadTokens.Should().Be(20000);
    }

    [Fact]
    public void Deepseek_http_400_context_error_is_not_endpoint_configuration_failure()
    {
        const string response =
            """{"error":{"message":"This model's maximum context length is 1048576 tokens. However, you requested 1333908 tokens (1325716 in the messages, 8192 in the completion).","type":"invalid_request_error","code":"invalid_request_error"}}""";
        LlmFailureMapper.FromStatusCode(HttpStatusCode.BadRequest, response).Should()
            .Be(LlmFailureCodes.ContextLengthExceeded);
        LlmFailureMapper.FromStatusCode(HttpStatusCode.BadRequest, "invalid deployment").Should()
            .Be(LlmFailureCodes.BadEndpointConfig);
        LlmFailureMapper.FromStatusCode(HttpStatusCode.Unauthorized, response).Should().Be(LlmFailureCodes.AuthFailed);
    }
}
