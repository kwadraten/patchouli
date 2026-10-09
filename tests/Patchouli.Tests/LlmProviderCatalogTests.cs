using FluentAssertions;
using LlmTornado.Code;
using Patchouli.Llm;

namespace Patchouli.Tests;

public sealed class LlmProviderCatalogTests
{
    [Fact]
    public void Catalog_covers_every_llmtornado_provider()
    {
        LLmProviders[] missing = LlmProviderCatalog.SelectableVendors
            .Where(vendor => LlmProviderCatalog.ByVendor(vendor) is null)
            .ToArray();

        missing.Should().BeEmpty(
            "every provider LlmTornado can talk to must be selectable in settings (D3); add a catalog entry " +
            "for each new LLmProviders value");
        LlmProviderCatalog.All.Select(entry => entry.Vendor).Distinct().Should()
            .HaveCount(LlmProviderCatalog.SelectableVendors.Count);
        LlmProviderCatalog.ByVendor(LLmProviders.OpenAi)!.ProviderId.Should().Be("openai");
        LlmSubscriptionCatalog.All.Should()
            .OnlyContain(subscription => LlmProviderCatalog.Find(subscription.ProviderId) != null);
    }

    [Fact]
    public void Catalog_excludes_enum_sentinels()
    {
        LlmProviderCatalog.All.Should().NotContain(entry => entry.Vendor == LLmProviders.Unknown);
        LlmProviderCatalog.All.Should().NotContain(entry => entry.Vendor == LLmProviders.Length);
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
        LlmProviderCatalog.Find("custom")!.Vendor.Should().Be(LLmProviders.Custom);
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
