using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using LlmTornado;
using LlmTornado.Codex;
using Patchouli.Core.Credentials;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Host.Agent;
using Patchouli.Llm;
using Patchouli.Ocr;

namespace Patchouli.Tests;

public sealed class LlmSubscriptionTests
{
    private const string SubscriptionProvider = "openai-subscription";

    [Fact]
    public void Every_public_oauth_session_in_the_pinned_library_has_a_subscription_adapter()
    {
        typeof(TornadoApi).Assembly.GetExportedTypes()
            .Where(type => type.Name.EndsWith("OAuthSession", StringComparison.Ordinal))
            .Should().Equal(typeof(CodexOAuthSession));
        LlmSubscriptionCatalog.All.Should().ContainSingle().Which.ProviderId.Should().Be(SubscriptionProvider);
        LlmAppSettings.Default().FindProvider(SubscriptionProvider)!.AuthenticationMode.Should()
            .Be(LlmAuthenticationModes.Subscription);
    }

    [Fact]
    public async Task Rotating_credentials_replace_the_complete_set_and_signout_preserves_the_api_key()
    {
        MemoryCredentialStore store = new();
        await store.SaveAsync("openai", "OpenAI", "api-key");
        LlmCodexCredentialStore adapter = new(store, SubscriptionProvider);
        await adapter.SaveAsync(Credentials());
        CodexOAuthCredentials rotated = Credentials();
        rotated.AccessToken = "new-access";
        rotated.RefreshToken = "new-refresh";
        await adapter.SaveAsync(rotated);
        CodexOAuthCredentials? loaded = await adapter.LoadAsync();
        loaded!.AccessToken.Should().Be("new-access");
        loaded.RefreshToken.Should().Be("new-refresh");
        loaded.AccountId.Should().Be("account");
        await adapter.ClearAsync();
        (await adapter.LoadAsync()).Should().BeNull();
        (await store.GetActiveSecretForProviderAsync("openai")).Value.Should().Be("api-key");
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
        LlmClientFactory.CreateTransport(resolved.Value).Should().BeOfType<LlmTornadoSubscriptionTransport>();
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
    public async Task Agent_client_refreshes_when_the_selected_provider_changes_to_subscription()
    {
        MemoryCredentialStore store = new();
        await store.SaveAsync("openai", "OpenAI", "api-key");
        await new LlmCodexCredentialStore(store, SubscriptionProvider).SaveAsync(Credentials());
        LlmAppSettings[] settings = [LlmAppSettings.Default()];
        LlmProviderAgentClientProvider clients = new(() => settings[0], store);
        Result<ILlmChatClient> apiClient = await clients.TryGetAsync(CancellationToken.None);
        apiClient.Value.Should().BeOfType<LlmChatClient>().Which.ProviderId.Should().Be("openai");
        settings[0] = SubscriptionSettings();
        Result<ILlmChatClient> subscriptionClient = await clients.TryGetAsync(CancellationToken.None);
        subscriptionClient.Value.Should().BeOfType<LlmChatClient>().Which.ProviderId.Should().Be(SubscriptionProvider);
        subscriptionClient.Value.Should().NotBeSameAs(apiClient.Value);
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
        CodexOAuthOptions httpOptions = new() { HttpClient = http };
        LlmTornadoSubscriptionTransport transport = new(store, credentials => new CodexOAuthOptions
        {
            CredentialStore = credentials,
            HttpClient = httpOptions.HttpClient
        });
        LlmTransportMessage[] messages =
        [
            Message(LlmChatRole.System, "Keep Markdown structure."),
            Message(LlmChatRole.User, "用户消息"),
            Message(LlmChatRole.Assistant, "fetch request"),
            Message(LlmChatRole.Tool, "fetch: document text"),
            Message(LlmChatRole.System, "An appended instruction")
        ];
        LlmTransportResult result = await transport.SendAsync(new LlmTransportRequest(resolved.Value, "test-model",
            messages, AgentNativeTools.Definitions, null, null, true, null, null));
        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Text.Should().Be("translated");
        result.Usage.Should().Be(new LlmUsage(10, 4, 14, 8, null));
        using JsonDocument payload = JsonDocument.Parse(handler.Payload!);
        payload.RootElement.GetProperty("tools").GetArrayLength().Should()
            .BeGreaterThan(0, "native declarations expose tools without SDK-owned execution");
        payload.RootElement.GetProperty("store").GetBoolean().Should().BeFalse();
        payload.RootElement.GetProperty("input")[0].GetProperty("role").GetString().Should().Be("developer");
        payload.RootElement.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString()
            .Should().Contain("Keep Markdown structure.",
                "the host instruction prefix must also retain protocol-level authority");
        string nativeInput = payload.RootElement.GetProperty("input").GetRawText();
        nativeInput.Should().Contain("用户消息").And.Contain("fetch request").And.Contain("fetch: document text");
        payload.RootElement.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString()
            .Should().Contain("An appended instruction");
    }

    [Fact]
    public async Task Vision_is_rejected_before_creating_a_subscription_connection()
    {
        bool connected = false;
        LlmTornadoSubscriptionTransport transport = new(new MemoryCredentialStore(), _ =>
        {
            connected = true;
            throw new InvalidOperationException();
        });
        LlmProviderRuntimeSettings provider = new(LlmProviderCatalog.Find(SubscriptionProvider)!, "", "", "test-model",
            "", "", "",
            LlmAuthenticationModes.Subscription);
        LlmTransportResult result = await transport.SendAsync(new LlmTransportRequest(provider, "test-model",
            [
                new LlmTransportMessage(LlmChatRole.User,
                    [new LlmMessagePart.LlmImagePart("data:image/png;base64,AA==", "image/png", "auto")])
            ],
            [], null, null, false, null, null));
        result.ErrorCode.Should().Be(LlmFailureCodes.UnsupportedInput);
        connected.Should().BeFalse();
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

    private static CodexOAuthCredentials Credentials()
    {
        return new CodexOAuthCredentials
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
                                            data: {"type":"response.completed","response":{"id":"response-1","status":"completed","usage":{"input_tokens":10,"output_tokens":4,"total_tokens":14,"input_tokens_details":{"cached_tokens":8}}}}


                                            """, Encoding.UTF8, "text/event-stream")
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
