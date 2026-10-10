using FluentAssertions;
using Patchouli.Core.Credentials;
using Patchouli.Llm;

namespace Patchouli.Tests;

public sealed class LlmAppSettingsTests
{
    [Fact]
    public void Default_has_one_row_per_catalog_provider_and_translation_defaults()
    {
        LlmAppSettings settings = LlmAppSettings.Default();

        settings.Providers.Should().HaveCount(LlmProviderCatalog.All.Count);
        settings.Providers.Select(provider => provider.ProviderId).Should()
            .Equal(LlmProviderCatalog.ProviderIds);
        settings.OcrSelection.ProviderId.Should().Be(LlmAppSettings.DefaultProviderId);
        settings.TranslationSelection.ProviderId.Should().Be(LlmAppSettings.DefaultProviderId);
        settings.EffectiveTargetLanguage.Should().Be(LlmAppSettings.FallbackTargetLanguage);
        settings.EffectiveTranslationWindowRadius.Should().Be(1);
        settings.BackfillPreviousWindowTranslation.Should().BeTrue();
        settings.Providers.Should().OnlyContain(provider => provider.ContextWindowTokens == 256000);
        settings.ToolResultMaxCharacters.Should().Be(32768);
    }

    [Fact]
    public void Default_settings_never_carry_a_secret()
    {
        string json = System.Text.Json.JsonSerializer.Serialize(LlmAppSettings.Default());

        json.Should().NotContain("apiKey").And.NotContain("ApiKey").And.NotContain("secret");
    }

    [Theory]
    [InlineData(-3, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(5, 5)]
    [InlineData(99, 5)]
    public void Window_radius_is_clamped_to_the_accepted_range(int configured, int expected)
    {
        LlmAppSettings settings = LlmAppSettings.Default() with { TranslationWindowRadius = configured };

        settings.EffectiveTranslationWindowRadius.Should().Be(expected);
        LlmAppSettings.ClampWindowRadius(configured).Should().Be(expected);
    }

    [Fact]
    public void Normalize_fills_missing_provider_rows_and_repairs_unknown_selections()
    {
        LlmAppSettings sparse = new([], "not-a-provider", "", "nope", "", "  ", 42, false);

        LlmAppSettings normalized = sparse.Normalize();

        normalized.Providers.Should().HaveCount(LlmProviderCatalog.All.Count);
        normalized.OcrProviderId.Should().Be(LlmAppSettings.DefaultProviderId);
        normalized.TranslationProviderId.Should().Be(LlmAppSettings.DefaultProviderId);
        normalized.TargetLanguage.Should().Be(LlmAppSettings.FallbackTargetLanguage);
        normalized.TranslationWindowRadius.Should().Be(LlmAppSettings.MaxTranslationWindowRadius);
        normalized.BackfillPreviousWindowTranslation.Should().BeFalse();
    }

    [Fact]
    public void Normalize_keeps_configured_rows_and_uses_their_model_as_the_selection_fallback()
    {
        LlmAppSettings settings = LlmAppSettings.Default() with
        {
            Providers =
            [
                new LlmProviderAppSettings("anthropic", "Anthropic", "https://proxy.example", "claude-x")
            ],
            OcrProviderId = "anthropic",
            OcrModel = ""
        };

        LlmAppSettings normalized = settings.Normalize();

        normalized.FindProvider("anthropic")!.BaseUrl.Should().Be("https://proxy.example");
        normalized.OcrSelection.Should().Be(("anthropic", "claude-x"));
    }

    [Fact]
    public void With_provider_replaces_a_row_without_reordering_the_catalog()
    {
        LlmAppSettings settings = LlmAppSettings.Default()
            .WithProvider(new LlmProviderAppSettings("deepseek", "", "", "deepseek-chat"));

        settings.Providers.Select(provider => provider.ProviderId).Should().Equal(LlmProviderCatalog.ProviderIds);
        settings.FindProvider("deepseek")!.Model.Should().Be("deepseek-chat");
        settings.FindProvider("DEEPSEEK")!.Model.Should().Be("deepseek-chat");
    }

    [Fact]
    public void With_provider_appends_a_row_that_is_not_in_the_catalog_yet()
    {
        LlmAppSettings settings = LlmAppSettings.Default()
            .WithProvider(new LlmProviderAppSettings("future-provider", "Future", "https://future.example", "m"));

        settings.Providers.Should().Contain(provider => provider.ProviderId == "future-provider");
        settings.Providers.Take(LlmProviderCatalog.All.Count).Select(provider => provider.ProviderId).Should()
            .Equal(LlmProviderCatalog.ProviderIds);
    }

    [Fact]
    public void Credential_provider_id_is_derived_from_the_provider_id()
    {
        LlmProviderAppSettings provider = new("  OpenAI  ");

        provider.CredentialProviderId.Should().Be("openai");
    }

    [Fact]
    public async Task Resolve_reports_missing_credential_without_touching_the_network()
    {
        LlmAppSettings settings = LlmAppSettings.Default();
        StubCredentialStore store = new();

        Core.Results.Result<LlmProviderRuntimeSettings> result =
            await LlmProviderClientFactory.ResolveAsync(settings, "openai", null, store);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(LlmFailureCodes.AuthFailed);
        LlmFailureClassifier.RequiresManualRepair(result.ErrorCode).Should().BeTrue();
    }

    [Fact]
    public async Task Resolve_rejects_an_unknown_provider()
    {
        Core.Results.Result<LlmProviderRuntimeSettings> result =
            await LlmProviderClientFactory.ResolveAsync(LlmAppSettings.Default(), "nope", null,
                new StubCredentialStore());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(LlmFailureCodes.BadEndpointConfig);
    }

    [Fact]
    public async Task Resolve_requires_azure_subscription_and_deployment()
    {
        LlmAppSettings settings = LlmAppSettings.Default();
        StubCredentialStore store = new() { Secret = "key" };

        Core.Results.Result<LlmProviderRuntimeSettings> missingDeployment =
            await LlmProviderClientFactory.ResolveAsync(settings, "azure-openai", "gpt-4o", store);
        Core.Results.Result<LlmProviderRuntimeSettings> complete = await LlmProviderClientFactory
            .ResolveAsync(settings.WithProvider(new LlmProviderAppSettings("azure-openai", "Azure OpenAI", "",
                "gpt-4o", "my-resource", "my-deployment")), "azure-openai", null, store);

        missingDeployment.IsFailure.Should().BeTrue();
        missingDeployment.ErrorCode.Should().Be(LlmFailureCodes.BadEndpointConfig);
        complete.IsSuccess.Should().BeTrue();
        complete.Value.BaseUrl.Should()
            .Be("https://my-resource.openai.azure.com/openai/deployments/my-deployment/{0}/{1}");
        complete.Value.IsAzure.Should().BeTrue();
        LlmProviderClientFactory.ResolveApiVersion(complete.Value)
            .Should().Be(LlmProviderClientFactory.DefaultAzureApiVersion);
    }

    [Fact]
    public async Task Resolve_requires_a_base_url_for_an_openai_compatible_custom_provider()
    {
        LlmAppSettings settings = LlmAppSettings.Default();
        StubCredentialStore store = new() { Secret = "key" };

        Core.Results.Result<LlmProviderRuntimeSettings> unset =
            await LlmProviderClientFactory.ResolveAsync(settings, "custom", "local-model", store);
        Core.Results.Result<LlmProviderRuntimeSettings> configured = await LlmProviderClientFactory
            .ResolveAsync(settings.WithProvider(new LlmProviderAppSettings("custom", "Custom",
                "http://localhost:11434", "llama3")), "custom", null, store);

        unset.IsFailure.Should().BeTrue();
        unset.ErrorCode.Should().Be(LlmFailureCodes.BadEndpointConfig);
        configured.IsSuccess.Should().BeTrue();
        configured.Value.BaseUrl.Should().Be("http://localhost:11434/{0}/{1}");
    }

    [Fact]
    public void Resolve_base_url_leaves_explicit_format_placeholders_untouched()
    {
        LlmProviderCatalogEntry openAi = LlmProviderCatalog.Find("openai")!;

        LlmProviderClientFactory.ResolveBaseUrl(openAi, "https://proxy.example/{0}/{1}", "", "")
            .Should().Be("https://proxy.example/{0}/{1}");
        LlmProviderClientFactory.ResolveBaseUrl(openAi, "", "", "")
            .Should().Be("https://api.openai.com/{0}/{1}");
    }

    [Fact]
    public async Task Inspect_reports_readiness_per_provider_without_secrets()
    {
        LlmAppSettings settings = LlmAppSettings.Default();
        StubCredentialStore store = new() { Secret = "key" };

        IReadOnlyList<LlmProviderReadiness> readiness =
            await LlmProviderClientFactory.InspectAsync(settings, store);

        LlmProviderReadiness ById(string providerId)
        {
            return readiness.Single(item => item.ProviderId == providerId);
        }

        readiness.Should().HaveCount(LlmProviderCatalog.All.Count);
        ById("openai").IsConfigured.Should().BeTrue();
        ById("openai").HasCredential.Should().BeTrue();
        ById("openai").Diagnostic.Should().Be("Ready.");
        ById("litellm").Diagnostic.Should().Contain("model");
        ById("custom").Diagnostic.Should().Contain("base URL");
        ById("azure-openai").Diagnostic.Should().Contain("Azure subscription/deployment");
        ById("google").IsConfigured.Should().BeFalse();
        ById("google").Diagnostic.Should().Contain("model");
        readiness.Select(item => item.Diagnostic)
            .Should().NotContain(text => text.Contains("sk-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Inspect_marks_a_fully_configured_provider_ready()
    {
        LlmAppSettings settings = LlmAppSettings.Default()
            .WithProvider(new LlmProviderAppSettings("google", "Google Gemini", "", "gemini-2.5-pro"));

        IReadOnlyList<LlmProviderReadiness> readiness =
            await LlmProviderClientFactory.InspectAsync(settings, new StubCredentialStore { Secret = "key" });

        LlmProviderReadiness google = readiness.Single(item => item.ProviderId == "google");
        google.IsConfigured.Should().BeTrue();
        google.HasModel.Should().BeTrue();
        google.Diagnostic.Should().Be("Ready.");
    }

    [Fact]
    public async Task Inspect_reports_a_missing_credential_per_provider()
    {
        StubCredentialStore store = new();

        IReadOnlyList<LlmProviderReadiness> readiness =
            await LlmProviderClientFactory.InspectAsync(LlmAppSettings.Default(), store);

        readiness.Should().OnlyContain(item => !item.HasCredential && !item.IsConfigured);
        readiness.Should().OnlyContain(item => item.Diagnostic.Contains("API key", StringComparison.Ordinal) ||
                                               item.Diagnostic.Contains("subscription login",
                                                   StringComparison.Ordinal));
    }

    [Fact]
    public void Catalog_entry_readiness_needs_model_and_endpoint()
    {
        LlmProviderCatalogEntry entry = LlmProviderCatalog.Find("openai")!;

        LlmProviderReadiness.Evaluate(entry, new LlmProviderAppSettings("openai"), true).IsConfigured.Should()
            .BeFalse();
        LlmProviderReadiness.Evaluate(entry, new LlmProviderAppSettings("openai", "", "", "gpt-4o"), true)
            .IsConfigured.Should().BeTrue();
        LlmProviderReadiness.Evaluate(entry, new LlmProviderAppSettings("openai", "", "", "gpt-4o"), false)
            .Diagnostic.Should().Contain("API key");
    }

    private sealed class StubCredentialStore : ICredentialStore
    {
        public string? Secret { get; init; }

        public Task<Core.Results.Result<ProviderCredentialMetadata>> SaveAsync(string providerId,
            string displayName, string secretValue, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Core.Results.Result<string>> GetActiveSecretForProviderAsync(string providerId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(string.IsNullOrWhiteSpace(Secret)
                ? Core.Results.Result<string>.Failure(Core.Results.AppErrorCodes.NotFound,
                    "Credential was not found.")
                : Core.Results.Result<string>.Success(Secret));
        }

        public Task<Core.Results.Result> RemoveAsync(string providerId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Core.Results.Result<IReadOnlyList<ProviderCredentialMetadata>>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ProviderCredentialMetadata> metadata = string.IsNullOrWhiteSpace(Secret)
                ? []
                : LlmProviderCatalog.ProviderIds.Select(providerId => new ProviderCredentialMetadata(
                    Core.Ids.CredentialId.New(), providerId, providerId,
                    ProviderCredentialStatus.Active, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)).ToArray();
            return Task.FromResult(
                Core.Results.Result<IReadOnlyList<ProviderCredentialMetadata>>.Success(metadata));
        }
    }
}
