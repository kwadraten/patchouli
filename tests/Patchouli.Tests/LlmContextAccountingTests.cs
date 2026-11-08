using System.Net;
using FluentAssertions;
using Patchouli.Llm;

namespace Patchouli.Tests;

public sealed class LlmContextAccountingTests
{
    [Theory]
    [InlineData("anthropic",
        """{"model":"test","content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn","usage":{"input_tokens":100,"output_tokens":20,"cache_read_input_tokens":20000,"cache_creation_input_tokens":10000}}""")]
    [InlineData("openai",
        """{"model":"test","choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":30100,"completion_tokens":20,"total_tokens":30120,"prompt_tokens_details":{"cached_tokens":20000}}}""")]
    [InlineData("google",
        """{"modelVersion":"test","candidates":[{"content":{"role":"model","parts":[{"text":"ok"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":30100,"candidatesTokenCount":20,"totalTokenCount":30120,"cachedContentTokenCount":20000}}""")]
    public async Task Cached_input_is_included_exactly_once_in_context_usage(string provider, string body)
    {
        LlmProviderCatalogEntry entry = LlmProviderCatalog.Find(provider)!;
        LlmProviderRuntimeSettings settings = new(entry, "key",
            LlmProviderClientFactory.ResolveBaseUrl(entry, null, "", ""), "test", "", "", "");
        using HttpClient http = new(new CompletionHandler(body));
        LlmModelsDevTransport transport = new(settings, http);
        LlmTransportResult result = await transport.SendAsync(new LlmTransportRequest(settings, "test",
            [new LlmTransportMessage(LlmChatRole.User, [new LlmMessagePart.LlmTextPart("hello")])],
            [], null, null, false, null, null));
        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        LlmUsage normalized = result.Usage;
        normalized.PromptTokens.Should().Be(30100);
        normalized.TotalTokens.Should().Be(30120);
        normalized.CacheReadTokens.Should().Be(20000);
    }

    private sealed class CompletionHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
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
