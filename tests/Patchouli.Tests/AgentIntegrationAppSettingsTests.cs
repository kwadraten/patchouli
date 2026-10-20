using FluentAssertions;
using Patchouli.Core.Agents;

namespace Patchouli.Tests;

/// <summary>
/// Issue #19 D: the persisted <c>Agents</c> section model. Normalization must make a hand-edited or
/// partially missing settings file safe: unknown agent ids fall back to the catalog default, path
/// overrides are trimmed, de-duplicated case-insensitively, and emitted in a stable order.
/// </summary>
public sealed class AgentIntegrationAppSettingsTests
{
    [Fact]
    public void Default_settings_enable_the_catalog_default_agent_without_overrides()
    {
        AgentIntegrationAppSettings settings = AgentIntegrationAppSettings.Default();

        settings.Enabled.Should().BeTrue();
        settings.SelectedAgentId.Should().Be(AgentIntegrationAppSettings.DefaultAgentId);
        settings.SelectedAgentId.Should().Be("opencode");
        settings.CustomPaths.Should().BeNull();
        settings.FindCustomPath("opencode").Should().BeNull();
        AgentCatalog.Default!.Id.Should().Be("opencode");
    }

    [Fact]
    public void Normalize_replaces_an_unknown_selected_agent_with_the_catalog_default()
    {
        AgentIntegrationAppSettings normalized =
            new AgentIntegrationAppSettings(true, "not-a-catalog-agent", null).Normalize();

        normalized.SelectedAgentId.Should().Be(AgentCatalog.Default!.Id);
        normalized.Enabled.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_replaces_a_blank_selected_agent_with_the_catalog_default(string selected)
    {
        AgentIntegrationAppSettings normalized =
            new AgentIntegrationAppSettings(false, selected, null).Normalize();

        normalized.SelectedAgentId.Should().Be("opencode");
        normalized.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Normalize_keeps_a_known_selected_agent()
    {
        AgentIntegrationAppSettings normalized =
            new AgentIntegrationAppSettings(true, "claude-acp", null).Normalize();

        normalized.SelectedAgentId.Should().Be("claude-acp");
    }

    [Fact]
    public void Normalize_trims_and_case_insensitively_deduplicates_custom_paths()
    {
        Dictionary<string, string> paths = new(StringComparer.Ordinal)
        {
            [" opencode "] = "  C:\\tools\\opencode.exe  ",
            ["OPENCODE"] = "C:\\other\\opencode.exe",
            ["claude-acp"] = "C:\\tools\\claude.exe",
            ["blank"] = "   ",
            ["   "] = "C:\\ignored.exe"
        };

        AgentIntegrationAppSettings normalized =
            new AgentIntegrationAppSettings(true, "opencode", paths).Normalize();

        normalized.CustomPaths.Should().NotBeNull();
        normalized.CustomPaths!.Should().HaveCount(2);
        normalized.CustomPaths.Keys.Should().Equal("claude-acp", "opencode");
        normalized.CustomPaths["opencode"].Should().Be("C:\\tools\\opencode.exe");
        normalized.FindCustomPath(" OPENCODE ").Should().Be("C:\\tools\\opencode.exe");
        normalized.FindCustomPath("CLAUDE-ACP").Should().Be("C:\\tools\\claude.exe");
    }

    [Fact]
    public void Normalize_emits_custom_paths_in_stable_ordinal_order()
    {
        Dictionary<string, string> first = new(StringComparer.Ordinal)
        {
            ["zebra"] = "C:\\z.exe",
            ["alpha"] = "C:\\a.exe",
            ["middle"] = "C:\\m.exe"
        };
        Dictionary<string, string> second = new(StringComparer.Ordinal)
        {
            ["middle"] = "C:\\m.exe",
            ["alpha"] = "C:\\a.exe",
            ["zebra"] = "C:\\z.exe"
        };

        new AgentIntegrationAppSettings(true, "opencode", first).Normalize().CustomPaths!.Keys
            .Should().Equal("alpha", "middle", "zebra");
        new AgentIntegrationAppSettings(true, "opencode", second).Normalize().CustomPaths!.Keys
            .Should().Equal("alpha", "middle", "zebra");
    }

    [Fact]
    public void Normalize_of_a_null_custom_path_map_yields_an_empty_map()
    {
        AgentIntegrationAppSettings normalized =
            new AgentIntegrationAppSettings(true, "opencode", null).Normalize();

        normalized.CustomPaths.Should().NotBeNull();
        normalized.CustomPaths.Should().BeEmpty();
    }

    [Fact]
    public void FindCustomPath_is_case_insensitive_and_trims_the_lookup_key()
    {
        AgentIntegrationAppSettings settings = new(true, "opencode",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["claude-acp"] = " C:\\tools\\claude.exe "
            });

        settings.FindCustomPath(" CLAUDE-ACP ").Should().Be(" C:\\tools\\claude.exe ");
        settings.FindCustomPath("claude-acp").Should().NotBeNull();
        settings.FindCustomPath("opencode").Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FindCustomPath_returns_null_for_a_blank_or_missing_id(string? agentId)
    {
        AgentIntegrationAppSettings settings = new(true, "opencode",
            new Dictionary<string, string> { ["opencode"] = "C:\\tools\\opencode.exe" });

        settings.FindCustomPath(agentId).Should().BeNull();
    }

    [Fact]
    public void FindCustomPath_ignores_blank_values()
    {
        AgentIntegrationAppSettings settings = new(true, "opencode",
            new Dictionary<string, string> { ["opencode"] = "   " });

        settings.FindCustomPath("opencode").Should().BeNull();
    }

    [Fact]
    public void WithCustomPath_adds_trims_and_sorts_the_override()
    {
        AgentIntegrationAppSettings settings = AgentIntegrationAppSettings.Default()
            .WithCustomPath(" zebra ", " C:\\z.exe ")
            .WithCustomPath("alpha", "C:\\a.exe");

        settings.CustomPaths.Should().NotBeNull();
        settings.CustomPaths!.Keys.Should().Equal("alpha", "zebra");
        settings.CustomPaths["zebra"].Should().Be("C:\\z.exe");
        settings.FindCustomPath("ZEBRA").Should().Be("C:\\z.exe");
    }

    [Fact]
    public void WithCustomPath_replaces_an_existing_override_case_insensitively()
    {
        AgentIntegrationAppSettings settings = AgentIntegrationAppSettings.Default()
            .WithCustomPath("opencode", "C:\\old.exe")
            .WithCustomPath(" OpenCode ", "C:\\new.exe");

        settings.CustomPaths.Should().ContainSingle();
        settings.CustomPaths.Keys.Should().Equal("OpenCode");
        settings.FindCustomPath("opencode").Should().Be("C:\\new.exe");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithCustomPath_removes_the_override_for_a_blank_path(string? path)
    {
        AgentIntegrationAppSettings settings = AgentIntegrationAppSettings.Default()
            .WithCustomPath("opencode", "C:\\tools\\opencode.exe")
            .WithCustomPath("OPENCODE", path);

        settings.CustomPaths.Should().BeEmpty();
        settings.FindCustomPath("opencode").Should().BeNull();
    }

    [Fact]
    public void WithCustomPath_ignores_a_blank_agent_id()
    {
        AgentIntegrationAppSettings original = AgentIntegrationAppSettings.Default()
            .WithCustomPath("opencode", "C:\\tools\\opencode.exe");

        AgentIntegrationAppSettings updated = original.WithCustomPath("  ", "C:\\ignored.exe");

        updated.Should().BeSameAs(original);
        updated.CustomPaths.Should().ContainKey("opencode");
    }

    [Fact]
    public void WithCustomPath_normalizes_existing_entries()
    {
        AgentIntegrationAppSettings original = new(true, "opencode",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["  "] = "C:\\ignored.exe",
                ["blank"] = "   ",
                ["tool"] = " C:\\tool.exe "
            });

        AgentIntegrationAppSettings updated = original.WithCustomPath("opencode", "C:\\oc.exe");

        updated.CustomPaths!.Keys.Should().Equal("opencode", "tool");
        updated.CustomPaths["tool"].Should().Be("C:\\tool.exe");
    }

    [Fact]
    public void Normalize_does_not_mutate_the_original_instance()
    {
        Dictionary<string, string> paths = new(StringComparer.Ordinal)
        {
            [" opencode "] = " C:\\tools\\opencode.exe "
        };
        AgentIntegrationAppSettings original = new(true, "bogus", paths);

        AgentIntegrationAppSettings normalized = original.Normalize();

        original.SelectedAgentId.Should().Be("bogus");
        original.CustomPaths![" opencode "].Should().Be(" C:\\tools\\opencode.exe ");
        normalized.SelectedAgentId.Should().Be("opencode");
        normalized.CustomPaths!["opencode"].Should().Be("C:\\tools\\opencode.exe");
    }

    [Fact]
    public void Empty_custom_paths_map_is_stable_and_empty()
    {
        AgentIntegrationAppSettings.EmptyCustomPaths.Should().BeEmpty();
    }
}
