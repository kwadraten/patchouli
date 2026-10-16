using FluentAssertions;
using Patchouli.Core.Mcp;
using Patchouli.Core.Results;
using Patchouli.Mcp;

namespace Patchouli.Tests;

public sealed class McpVfsAddressTests
{
    private const string Item = "a6e25380-9c17-480f-b1de-fb38b3358844";

    [Theory]
    [InlineData("items/", "patchouli://items/")]
    [InlineData("/items", "patchouli://items/")]
    [InlineData(" ./library.yml ", "patchouli://library.yml")]
    [InlineData("library.toon", "patchouli://library.yml")]
    [InlineData("patchouli://library.toon", "patchouli://library.yml")]
    public void Root_relative_addresses_resolve_to_canonical_resources(string address, string canonical)
    {
        Result<McpUriParseResult> parsed = McpResourceUris.Parse(address);
        parsed.IsSuccess.Should().BeTrue(parsed.ErrorMessage);
        parsed.Value.CanonicalUri.Should().Be(canonical);
    }

    [Theory]
    [InlineData("../items/")]
    [InlineData("items/../library.yml")]
    [InlineData("C:\\library.yml")]
    [InlineData("//server/library.yml")]
    [InlineData("file:///library.yml")]
    [InlineData("https://items/")]
    [InlineData("texts/%2e%2e/library.yml")]
    [InlineData("patchouli://items/?unknown=1")]
    public void Ambiguous_or_non_vfs_addresses_are_rejected(string address)
    {
        McpResourceUris.Parse(address).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Legacy_alias_keeps_format_but_returns_new_identity_and_versioned_warning()
    {
        string legacy = $"./items/{Item}.bib";
        McpUriParseResult parsed = McpResourceUris.Parse(legacy).Value;
        parsed.IsLegacy.Should().BeTrue();
        parsed.ContentType.Should().Be("application/x-bibtex");
        parsed.CanonicalUri.Should().Be($"patchouli://items/{Item}.yml");
        McpResourceUris.ObsoleteWarning(legacy).Should().Contain("[obsoleted]").And.Contain("0.4.0")
            .And.Contain(parsed.CanonicalUri).And.NotContain(".bib");
        McpResourceUris.ObsoleteWarning(parsed.CanonicalUri).Should().BeNull();
    }

    [Fact]
    public void All_address_forms_and_legacy_library_alias_share_permission_domain()
    {
        McpPermissionPolicy.DomainOf("./library.toon").Should().Be("library.yml");
        McpPermissionPolicy.DomainOf($"/items/{Item}.bib").Should().Be("items");
        McpServerSettings settings = new(4536, "127.0.0.1", false, [], false, null, [], DateTimeOffset.UtcNow)
        {
            DomainPermissions =
                [new McpDomainPermission("items", "put", false), new McpDomainPermission("library.yml", "fetch", false)]
        };
        foreach (string address in new[] { $"items/{Item}.yml", $"/items/{Item}.bib", $"patchouli://items/{Item}.yml" })
        {
            McpPermissionPolicy.Allows(settings, "put", address).Should().BeFalse();
        }

        McpPermissionPolicy.Allows(settings, "fetch", "library.toon").Should().BeFalse();
    }

    [Fact]
    public void Duplicate_evidence_query_parameters_are_rejected()
    {
        McpResourceUris.Parse($"texts/{Item}/page-1.md?rev={Item}&rev={Item}").IsFailure.Should().BeTrue();
        McpResourceUris.Parse($"texts/{Item}/page-1.xml?box={Item}&box={Item}").IsFailure.Should().BeTrue();
    }
}
