using FluentAssertions;
using Patchouli.Llm;

namespace Patchouli.Tests;

public sealed class LlmProviderCatalogTests
{
    [Fact]
    public void Catalog_projects_the_modelsdev_snapshot_exactly()
    {
        IReadOnlyList<string> libraryIds = FSharp.ModelsDev.Catalog.SnapshotCatalog.Default.ProviderIds;

        LlmProviderCatalog.ProviderIds.Should().Equal(libraryIds,
            "the selectable list is owned entirely by the library snapshot");
        LlmProviderCatalog.All.Select(entry => entry.ProviderId).Should().Equal(libraryIds);

        foreach (string providerId in libraryIds)
        {
            FSharp.ModelsDev.Catalog.SnapshotCatalog.Default
                .TryGetProvider(providerId, out FSharp.ModelsDev.Catalog.ProviderDefinition definition)
                .Should().BeTrue();
            LlmProviderCatalogEntry entry = LlmProviderCatalog.Find(providerId)!;
            entry.DisplayName.Should().Be(definition.Name, providerId);
            entry.Protocol.Should().Be(definition.ProtocolId, providerId);
            entry.DefaultBaseUrl.Should().Be(definition.BaseUrl ?? "", providerId);
        }
    }

    [Fact]
    public void Selectable_catalog_contains_no_host_only_ids()
    {
        LlmProviderCatalog.All.Select(entry => entry.ProviderId).Should()
            .NotContain([
                "azure-openai", "openai-subscription", "custom", "blablador", "litellm",
                "typesafe", "voyage"
            ]);
        LlmProviderCatalog.FallbackAll.Select(entry => entry.ProviderId).Should()
            .Contain(["azure-openai", "openai-subscription", "custom"]);
    }

    [Fact]
    public void Retired_host_ids_stay_resolvable_for_persisted_state_but_are_not_selectable()
    {
        LlmProviderCatalog.Find("azure-openai")!.IsAzure.Should().BeTrue();
        LlmProviderCatalog.Find("azure-openai")!.DefaultBaseUrl.Should()
            .Be("https://{resource}.openai.azure.com/openai/deployments/{deployment}");
        LlmProviderCatalog.Find("openai-subscription")!.Protocol.Should().Be("codex-responses");
        LlmProviderCatalog.Find("custom")!.Protocol.Should().Be("openai-chat");
        LlmProviderCatalog.Find("custom")!.DefaultBaseUrl.Should().BeEmpty();
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
    public void Only_the_azure_fallback_entry_asks_for_subscription_and_deployment()
    {
        LlmProviderCatalog.All.Where(entry => entry.IsAzure).Should().BeEmpty();
        LlmProviderCatalog.FallbackAll.Where(entry => entry.IsAzure).Select(entry => entry.ProviderId)
            .Should().Equal("azure-openai");
    }

    [Fact]
    public void Protocol_support_is_explicit_and_unknown_protocols_are_not_transport_ready()
    {
        LlmProviderCatalog.IsSupportedApiProtocol("openai-chat").Should().BeTrue();
        LlmProviderCatalog.IsSupportedApiProtocol("anthropic-messages").Should().BeTrue();
        LlmProviderCatalog.IsSupportedApiProtocol("gemini").Should().BeTrue();
        LlmProviderCatalog.IsSupportedApiProtocol("unknown").Should().BeFalse();
        LlmProviderCatalog.IsSupportedApiProtocol("vertex-gemini").Should().BeFalse();
        LlmProviderCatalog.IsSupportedApiProtocol("codex-responses").Should().BeFalse();
        LlmProviderCatalog.IsSupportedProtocol("codex-responses").Should().BeTrue(
            "the codex protocol only works through the subscription sign-in path");
        LlmProviderCatalog.All.Where(entry => entry.Protocol == "unknown").Should()
            .OnlyContain(entry => !entry.IsSupportedProtocol);
    }

    [Fact]
    public void Subscription_catalog_entries_resolve_to_the_fallback_catalog()
    {
        LlmSubscriptionCatalog.All.Should()
            .OnlyContain(subscription => LlmProviderCatalog.Find(subscription.ProviderId) != null);
        LlmSubscriptionCatalog.All.Should()
            .OnlyContain(subscription =>
                !LlmProviderCatalog.All.Any(entry => entry.ProviderId == subscription.ProviderId));
    }

    [Fact]
    public void Entry_flags_track_the_definition_data_across_library_and_fallback_entries()
    {
        foreach (LlmProviderCatalogEntry entry in LlmProviderCatalog.All.Concat(LlmProviderCatalog.FallbackAll))
        {
            entry.IsAzure.Should().Be(entry.RequiresSubscription || entry.RequiresDeployment);
            entry.RequiresBaseUrl.Should().Be(string.IsNullOrWhiteSpace(entry.DefaultBaseUrl));
        }
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
        LlmProviderCatalog.Resolve("azure-openai", LlmAppSettings.DefaultProviderId).ProviderId.Should()
            .Be("azure-openai", "persisted legacy ids still resolve");
        LlmProviderCatalog.Find("  ").Should().BeNull();
        LlmProviderCatalog.Find(null).Should().BeNull();
    }
}
