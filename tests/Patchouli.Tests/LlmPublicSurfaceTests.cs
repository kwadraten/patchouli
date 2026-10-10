using FluentAssertions;
using Patchouli.Llm;

namespace Patchouli.Tests;

/// <summary>
/// Exercises the public surface by contract rather than by implementation: the provider-neutral factory,
/// the content comparer and the value records that later stages (the agent loop and the S6 multimodal OCR
/// adapter) consume. Keeping these assertions here means the surface is covered instead of only declared.
/// </summary>
public sealed class LlmPublicSurfaceTests
{
    private static LlmProviderRuntimeSettings Settings(string providerId = "openai")
    {
        return new LlmProviderRuntimeSettings(LlmProviderCatalog.Find(providerId)!, "sk-test",
            "https://api.openai.com/{0}/{1}", "gpt-4o-mini", "", "", "");
    }

    [Fact]
    public void The_factory_builds_a_tornado_client_for_resolved_settings()
    {
        LlmProviderRuntimeSettings settings = Settings();

        LlmTornado.TornadoApi api = LlmClientFactory.CreateApi(settings);
        ILlmChatTransport transport = LlmClientFactory.CreateTransport(settings);

        api.Should().NotBeNull();
        api.ApiUrlFormat.Should().Be("https://api.openai.com/{0}/{1}");
        api.ApiVersion.Should().Be("v1");
        transport.Should().BeOfType<LlmTornadoChatTransport>();
    }

    [Fact]
    public void The_factory_builds_an_azure_client_with_the_azure_api_version()
    {
        LlmProviderRuntimeSettings azure = Settings("azure-openai") with
        {
            BaseUrl = "https://resource.openai.azure.com/openai/deployments/deployment/{0}/{1}",
            Subscription = "resource",
            Deployment = "deployment"
        };

        LlmTornado.TornadoApi api = LlmClientFactory.CreateApi(azure);

        api.ApiVersion.Should().Be(LlmProviderClientFactory.DefaultAzureApiVersion);
        LlmProviderClientFactory.ResolveApiVersion(azure with { ApiVersion = "2025-01-01" })
            .Should().Be("2025-01-01");
    }

    [Fact]
    public void A_chat_client_reports_its_provider()
    {
        LlmChatClient client = new(Settings("anthropic"), new ThrowingTransport());

        client.ProviderId.Should().Be("anthropic");
    }

    [Fact]
    public void The_content_comparer_compares_values_not_references()
    {
        LlmMessagePart text = new LlmMessagePart.LlmTextPart("hello");
        LlmMessagePart sameText = new LlmMessagePart.LlmTextPart("hello");
        LlmMessagePart otherText = new LlmMessagePart.LlmTextPart("HELLO");
        LlmMessagePart image = new LlmMessagePart.LlmImagePart("data:image/png;base64,AA", "image/png", "high");
        LlmMessagePart sameImage = new LlmMessagePart.LlmImagePart("data:image/png;base64,AA", "image/png", "high");
        LlmMessagePart otherImage = new LlmMessagePart.LlmImagePart("data:image/png;base64,AA", "image/png", "low");

        LlmMessageContentComparer.IsSamePart(text, sameText).Should().BeTrue();
        LlmMessageContentComparer.IsSamePart(text, otherText).Should().BeFalse();
        LlmMessageContentComparer.IsSamePart(image, sameImage).Should().BeTrue();
        LlmMessageContentComparer.IsSamePart(image, otherImage).Should().BeFalse();
        LlmMessageContentComparer.IsSamePart(text, image).Should().BeFalse();
        Action nullLeft = () => LlmMessageContentComparer.IsSamePart(null!, text);
        Action nullRight = () => LlmMessageContentComparer.IsSamePart(text, null!);
        nullLeft.Should().Throw<ArgumentNullException>();
        nullRight.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void A_completion_reports_cached_token_counters_and_text_presence()
    {
        LlmUsage usage = new(120, 30, 150, 100, 20);
        LlmChatCompletion completion = new("translated", "gpt-4o-mini", "openai", "stop", usage, true, "session-1",
            "abc");

        usage.PromptTokens.Should().Be(120);
        usage.CompletionTokens.Should().Be(30);
        usage.TotalTokens.Should().Be(150);
        usage.CacheReadTokens.Should().Be(100);
        usage.CacheCreationTokens.Should().Be(20);
        LlmUsage.Empty.CacheReadTokens.Should().BeNull();
        completion.HasText.Should().BeTrue();
        (completion with { Text = "   " }).HasText.Should().BeFalse();
        completion.PrefixSignature.Should().Be("abc");
    }

    [Fact]
    public void Provider_settings_defaults_and_suggestions_are_available_to_the_settings_page()
    {
        LlmProviderAppSettings provider = new("openai");
        LlmProviderAppSettings copy = provider with { Model = "gpt-4o" };

        copy.Model.Should().Be("gpt-4o");
        provider.Model.Should().BeEmpty();
        LlmProviderAppSettings.FromCatalog(LlmProviderCatalog.Find("cohere")!).DisplayName.Should().Be("Cohere");
        LlmAppSettings.Default().ChatSelection.ProviderId.Should().Be(LlmAppSettings.DefaultProviderId);
    }

    [Fact]
    public async Task The_agent_facing_interface_is_satisfied_by_the_chat_client()
    {
        // The S1 agent loop and the S6 OCR adapter both depend on ILlmChatClient only.
        LLlmRecordingTransport transport = new();
        ILlmChatClient client = new LlmChatClient(Settings(), transport);
        LlmChatHistory history = LlmChatHistory.Create("instructions").Append([LlmChatMessage.User("hi")]);

        await client.CompleteAsync("session-1", new LlmChatRequest(history));
        await client.CompleteVisionAsync("session-2", new LlmChatRequest(history),
            new LlmVisionInput("data:image/png;base64,AA", "image/png"));

        transport.Count.Should().Be(2);
    }

    [Fact]
    public async Task A_text_completion_refuses_a_history_that_carries_an_image()
    {
        LLlmRecordingTransport transport = new();
        LlmChatClient client = new(Settings(), transport);
        LlmChatHistory history = LlmChatHistory.Create("instructions").Append(
        [
            new LlmChatMessage(LlmChatRole.User,
                [new LlmMessagePart.LlmImagePart("data:image/png;base64,AA", "image/png", "auto")])
        ]);

        Core.Results.Result<LlmChatCompletion> result =
            await client.CompleteAsync("session-1", new LlmChatRequest(history));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(LlmFailureCodes.UnsupportedInput);
        transport.Count.Should().Be(0);
    }

    private sealed class ThrowingTransport : ILlmChatTransport
    {
        public Task<LlmTransportResult> SendAsync(LlmTransportRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class LLlmRecordingTransport : ILlmChatTransport
    {
        private readonly Lock _gate = new();
        private int _count;

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _count;
                }
            }
        }

        public Task<LlmTransportResult> SendAsync(LlmTransportRequest request,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _count++;
            }

            return Task.FromResult(LlmTransportResult.Success("ok", request.Model, "stop", LlmUsage.Empty));
        }
    }
}
