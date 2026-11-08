using FluentAssertions;
using Patchouli.Llm;

namespace Patchouli.Tests;

public sealed class LlmProviderCatalogTests
{
    [Fact]
    public void Catalog_defines_modelsdev_protocols_for_persisted_provider_ids()
    {
        LlmProviderCatalog.All.Should().OnlyContain(entry => entry.Protocol == "openai-chat" ||
                                                             entry.Protocol == "anthropic-messages" ||
                                                             entry.Protocol == "gemini" ||
                                                             entry.Protocol == "codex-responses");
        LlmProviderCatalog.Find("anthropic")!.Protocol.Should().Be("anthropic-messages");
        LlmProviderCatalog.Find("google")!.Protocol.Should().Be("gemini");
        LlmSubscriptionCatalog.All.Should()
            .OnlyContain(subscription => LlmProviderCatalog.Find(subscription.ProviderId) != null);
    }

    [Fact]
    public void Catalog_ids_are_unique_lowercase_and_resolvable()
    {
        LlmProviderCatalog.All.Select(entry => entry.ProviderId).Should()
            .OnlyHaveUniqueItems()
            .And.OnlyContain(id => id == id.ToLowerInvariant() && id.Length > 0);
        foreach (LlmProviderCatalogEntry entry in LlmProviderCatalog.All)
        {
            LlmProviderCatalog.Find(entry.ProviderId).Should().BeSameAs(entry);
            LlmProviderCatalog.Find(entry.ProviderId.ToUpperInvariant()).Should().BeSameAs(entry);
        }
    }

    [Fact]
    public void Azure_entry_is_the_only_one_asking_for_subscription_and_deployment()
    {
        LlmProviderCatalog.All.Where(entry => entry.IsAzure).Select(entry => entry.ProviderId)
            .Should().Equal("azure-openai");
        LlmProviderCatalog.All.Where(entry => entry.RequiresDeployment).Select(entry => entry.ProviderId)
            .Should().Equal("azure-openai");
    }

    [Fact]
    public void Only_the_openai_compatible_custom_provider_needs_a_user_supplied_endpoint()
    {
        foreach (LlmProviderCatalogEntry entry in LlmProviderCatalog.All.Where(item => !item.RequiresBaseUrl))
        {
            entry.DefaultBaseUrl.Should().NotBeNullOrWhiteSpace(
                $"'{entry.ProviderId}' has a usable default endpoint; only the custom provider may start empty");
        }

        LlmProviderCatalog.All.Where(entry => entry.RequiresBaseUrl).Select(entry => entry.ProviderId)
            .Should().Equal("custom");
        LlmProviderCatalog.Find("custom")!.DefaultBaseUrl.Should().BeEmpty();
        LlmProviderCatalog.Find("custom")!.Protocol.Should().Be("openai-chat");
    }

    [Fact]
    public void Unknown_or_blank_provider_falls_back_to_the_default()
    {
        LlmProviderCatalog.Resolve("does-not-exist", LlmAppSettings.DefaultProviderId).ProviderId.Should()
            .Be(LlmAppSettings.DefaultProviderId);
        LlmProviderCatalog.Resolve("", LlmAppSettings.DefaultProviderId).ProviderId.Should()
            .Be(LlmAppSettings.DefaultProviderId);
        LlmProviderCatalog.Resolve("anthropic", LlmAppSettings.DefaultProviderId).ProviderId.Should()
            .Be("anthropic");
        LlmProviderCatalog.Find("  ").Should().BeNull();
        LlmProviderCatalog.Find(null).Should().BeNull();
    }
}
