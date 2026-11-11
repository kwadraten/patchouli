using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Patchouli.Core.Credentials;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Llm;
using Patchouli.Ocr;

namespace Patchouli.Tests;

public sealed class LlmSubscriptionTests
{
    private const string SubscriptionProvider = "openai-subscription";

    [Fact]
    public void Codex_remains_a_separate_subscription_connection()
    {
        LlmSubscriptionCatalog.All.Should().ContainSingle().Which.ProviderId.Should().Be(SubscriptionProvider);
        LlmProviderAppSettings.FromCatalog(LlmProviderCatalog.Find(SubscriptionProvider)!).AuthenticationMode
            .Should().Be(LlmAuthenticationModes.Subscription);
    }

    [Fact]
    public async Task Rotating_credentials_replace_the_complete_set_and_signout_preserves_the_api_key()
    {
        MemoryCredentialStore store = new();
        await store.SaveAsync("openai", "OpenAI", "api-key");
        LlmCodexCredentialStore adapter = new(store, SubscriptionProvider);
        await adapter.SaveAsync(Credentials());
        LlmCodexCredentials rotated = Credentials();
        rotated.AccessToken = "new-access";
        rotated.RefreshToken = "new-refresh";
        await adapter.SaveAsync(rotated);
        LlmCodexCredentials? loaded = await adapter.LoadAsync();
        loaded!.AccessToken.Should().Be("new-access");
        loaded.RefreshToken.Should().Be("new-refresh");
        loaded.AccountId.Should().Be("account");
        await adapter.ClearAsync();
        (await adapter.LoadAsync()).Should().BeNull();
        (await store.GetActiveSecretForProviderAsync("openai")).Value.Should().Be("api-key");
    }

    [Fact]
    public async Task Expired_legacy_credentials_are_refreshed_and_rotated_before_inference()
    {
        MemoryCredentialStore store = new();
        await store.SaveAsync(LlmSubscriptionCatalog.CredentialProviderId(SubscriptionProvider), "Subscription",
            """
            {"AccessToken":"old","RefreshToken":"refresh","AccountId":"account","ExpiresAtUtc":"2000-01-01T00:00:00+00:00"}
            """);
        LlmCodexCredentialStore credentials = new(store, SubscriptionProvider);
        using HttpClient http = new(new RefreshHandler());
        LlmCodexAuthentication auth = new(credentials, http);

        IReadOnlyDictionary<string, string> headers = await auth.GetHeadersAsync(false, CancellationToken.None);

        headers["Authorization"].Should().Be("Bearer new-access");
        headers["ChatGPT-Account-ID"].Should().Be("account");
        LlmCodexCredentials? rotated = await credentials.LoadAsync();
        rotated!.RefreshToken.Should().Be("new-refresh");
        rotated.ExpiresAtUtc.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Missing_or_corrupt_logins_are_repairable_and_only_local_login_metadata_is_used_for_discovery()
    {
        MemoryCredentialStore store = new();
        LlmAppSettings settings = SubscriptionSettings();
        (await LlmProviderClientFactory.ResolveAsync(settings, SubscriptionProvider, null, store)).ErrorCode
            .Should().Be(LlmFailureCodes.AuthFailed);
        await store.SaveAsync(LlmSubscriptionCatalog.CredentialProviderId(SubscriptionProvider), "Subscription",
            "invalid-secret");
        (await LlmProviderClientFactory.ResolveAsync(settings, SubscriptionProvider, null, store)).ErrorCode
            .Should().Be(LlmFailureCodes.AuthFailed);
        await new LlmCodexCredentialStore(store, SubscriptionProvider).SaveAsync(Credentials());
        Result<LlmProviderRuntimeSettings> resolved =
            await LlmProviderClientFactory.ResolveAsync(settings, SubscriptionProvider, null, store);
        resolved.IsSuccess.Should().BeTrue();
        resolved.Value.ApiKey.Should().BeEmpty();
        LlmClientFactory.CreateTransport(resolved.Value).Should().BeOfType<LlmModelsDevTransport>();
        IReadOnlyList<LlmProviderReadiness> readiness = await LlmProviderClientFactory.InspectAsync(settings, store);
        readiness.Single(item => item.ProviderId == SubscriptionProvider).IsConfigured.Should().BeTrue();
        readiness.Single(item => item.ProviderId == SubscriptionProvider).SupportsVision.Should().BeFalse();
        MultimodalLlmOcrRuntime ocr = new(() => settings, store);
        (await ocr.CreateClientAsync(SubscriptionProvider, "test-model")).ErrorCode.Should()
            .Be(LlmFailureCodes.UnsupportedInput);
        (await ocr.InspectProvidersAsync()).Single(item => item.ProviderId == SubscriptionProvider).IsConfigured
            .Should().BeFalse();
    }

    [Fact]
    public async Task Subscription_transport_uses_upstream_protocol_and_preserves_the_full_ordered_transcript()
    {
        MemoryCredentialStore store = new();
        await new LlmCodexCredentialStore(store, SubscriptionProvider).SaveAsync(Credentials());
        Result<LlmProviderRuntimeSettings> resolved = await LlmProviderClientFactory.ResolveAsync(
            SubscriptionSettings(), SubscriptionProvider, null, store);
        ProtocolHandler handler = new();
        using HttpClient http = new(handler);
        LlmModelsDevTransport transport = new(resolved.Value, http);
        LlmTransportMessage[] messages =
        [
            Message(LlmChatRole.System, "Keep Markdown structure."),
            Message(LlmChatRole.User, "用户消息"),
            new(LlmChatRole.Assistant,
            [
                new LlmMessagePart.LlmTextPart("fetch request"),
                new LlmMessagePart.LlmToolCallPart("call_1", "fetch", "{}")
            ]),
            new(LlmChatRole.Tool,
                [new LlmMessagePart.LlmToolResultPart("call_1", "fetch", "fetch: document text", false)]),
            Message(LlmChatRole.System, "An appended instruction")
        ];
        string[] tools =
        [
            """{"type":"function","function":{"name":"fetch","description":"Fetch one resource.","parameters":{"type":"object","properties":{"uris":{"type":"array","items":{"type":"string"}}}}}}"""
        ];
        LlmTransportResult result = await transport.SendAsync(new LlmTransportRequest(resolved.Value, "test-model",
            messages, tools, null, null, true, null, null) { ReasoningEffort = "high" });
        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Text.Should().Be("translated");
        result.Usage.Should().Be(new LlmUsage(10, 4, 14, 8, null));
        using JsonDocument payload = JsonDocument.Parse(handler.Payload!);
        payload.RootElement.GetProperty("reasoning").GetProperty("effort").GetString().Should().Be("high");
        payload.RootElement.GetProperty("tools").GetArrayLength().Should()
            .BeGreaterThan(0, "native declarations expose tools without SDK-owned execution");
        payload.RootElement.GetProperty("store").GetBoolean().Should().BeFalse();
        payload.RootElement.GetProperty("input")[0].GetProperty("role").GetString().Should().Be("developer");
        payload.RootElement.GetProperty("input")[0].GetProperty("content").GetString()
            .Should().Contain("Keep Markdown structure.",
                "the host instruction prefix must also retain protocol-level authority");
        JsonElement input = payload.RootElement.GetProperty("input");
        input[1].GetProperty("content").GetString().Should().Be("用户消息");
        input[2].GetProperty("content")[0].GetProperty("text").GetString().Should().Be("fetch request");
        input[3].GetProperty("call_id").GetString().Should().Be("call_1");
        input[4].GetProperty("output").GetString().Should().Be("fetch: document text");
        payload.RootElement.GetProperty("input")[5].GetProperty("content").GetString()
            .Should().Be("An appended instruction");
    }

    [Fact]
    public async Task Vision_is_rejected_before_creating_a_subscription_connection()
    {
        LlmProviderRuntimeSettings provider = new(LlmProviderCatalog.Find(SubscriptionProvider)!, "", "", "test-model",
            "", "", "",
            LlmAuthenticationModes.Subscription);
        LlmModelsDevTransport transport = new(provider);
        LlmTransportResult result = await transport.SendAsync(new LlmTransportRequest(provider, "test-model",
            [
                new LlmTransportMessage(LlmChatRole.User,
                    [new LlmMessagePart.LlmImagePart("data:image/png;base64,AA==", "image/png", "auto")])
            ],
            [], null, null, false, null, null));
        result.ErrorCode.Should().Be(LlmFailureCodes.UnsupportedInput);
    }

    private static LlmAppSettings SubscriptionSettings()
    {
        return LlmAppSettings.Default().WithProvider(
                LlmProviderAppSettings.FromCatalog(LlmProviderCatalog.Find(SubscriptionProvider)!) with
                {
                    Model = "test-model"
                }) with
            {
                ChatProviderId = SubscriptionProvider, ChatModel = "test-model"
            };
    }

    private static LlmCodexCredentials Credentials()
    {
        return new LlmCodexCredentials
        {
            AccessToken = "access", RefreshToken = "refresh", AccountId = "account", Email = "test@example.com",
            PlanType = "pro",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1), LastRefreshUtc = DateTimeOffset.UtcNow
        };
    }

    private static LlmTransportMessage Message(LlmChatRole role, string text)
    {
        return new LlmTransportMessage(role, [new LlmMessagePart.LlmTextPart(text)]);
    }

    private sealed class ProtocolHandler : HttpMessageHandler
    {
        public string? Payload { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                                                {"models":[{"slug":"test-model","display_name":"Test","base_instructions":"Provider instructions","visibility":"list","supported_in_api":true,"is_default":true}]}
                                                """, Encoding.UTF8, "application/json")
                };
            }

            request.RequestUri.AbsolutePath.Should().EndWith("/responses");
            request.Headers.Authorization!.Parameter.Should().Be("access");
            Payload = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                                            event: response.output_text.delta
                                            data: {"type":"response.output_text.delta","delta":"translated"}

                                            event: response.completed
                                            data: {"type":"response.completed","response":{"id":"response-1","status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"translated"}]}],"usage":{"input_tokens":10,"output_tokens":4,"total_tokens":14,"input_tokens_details":{"cached_tokens":8}}}}


                                            """, Encoding.UTF8, "text/event-stream")
            };
        }
    }

    private sealed class RefreshHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.RequestUri.Should().Be(new Uri("https://auth.openai.com/oauth/token"));
            using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            body.RootElement.GetProperty("refresh_token").GetString().Should().Be("refresh");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                                            {"access_token":"new-access","refresh_token":"new-refresh","expires_in":3600}
                                            """, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class MemoryCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _secrets = new(StringComparer.OrdinalIgnoreCase);

        public Task<Result<ProviderCredentialMetadata>> SaveAsync(string providerId, string displayName,
            string secretValue,
            CancellationToken cancellationToken = default)
        {
            _secrets[providerId] = secretValue;
            return Task.FromResult(Result<ProviderCredentialMetadata>.Success(Metadata(providerId)));
        }

        public Task<Result<string>> GetActiveSecretForProviderAsync(string providerId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_secrets.TryGetValue(providerId, out string? secret)
                ? Result<string>.Success(secret)
                : Result<string>.Failure(AppErrorCodes.NotFound, "No credential."));
        }

        public Task<Result> RemoveAsync(string providerId, CancellationToken cancellationToken = default)
        {
            _secrets.Remove(providerId);
            return Task.FromResult(Result.Success());
        }

        public Task<Result<IReadOnlyList<ProviderCredentialMetadata>>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                Result<IReadOnlyList<ProviderCredentialMetadata>>.Success(_secrets.Keys.Select(Metadata).ToArray()));
        }

        private static ProviderCredentialMetadata Metadata(string providerId)
        {
            return new ProviderCredentialMetadata(CredentialId.New(), providerId, providerId,
                ProviderCredentialStatus.Active, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        }
    }
}
