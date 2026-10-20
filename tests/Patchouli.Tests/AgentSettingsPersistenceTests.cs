using System.Text.Json;
using FluentAssertions;
using Patchouli.Core.Agents;
using Patchouli.UI;

namespace Patchouli.Tests;

/// <summary>
/// Issue #19 D: the <c>Agents</c> section of the application settings file. A missing, partially
/// malformed or legacy section must load safely, and a save must round-trip without touching the
/// other settings sections.
/// </summary>
public sealed class AgentSettingsPersistenceTests
{
    [Fact]
    public void The_agents_section_round_trips_enabled_selection_and_overrides()
    {
        using TemporaryAppSettingsFile settings = new();
        PatchouliAppSettings current = PatchouliAppSettings.Load(settings.Path);
        AgentIntegrationAppSettings agents = new(
            false,
            "claude-acp",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["opencode"] = " C:\\tools\\opencode.exe ",
                ["claude-acp"] = "C:\\tools\\claude.exe"
            });

        (current with { Agents = agents }).Save(settings.Path).IsSuccess.Should().BeTrue();

        string json = File.ReadAllText(settings.Path);
        json.Should().Contain("\"Agents\"");
        using (JsonDocument document = JsonDocument.Parse(json))
        {
            JsonElement section = document.RootElement.GetProperty("Agents");
            section.GetProperty("Enabled").GetBoolean().Should().BeFalse();
            section.GetProperty("SelectedAgentId").GetString().Should().Be("claude-acp");
            section.GetProperty("CustomPaths").GetProperty("opencode").GetString()
                .Should().Be("C:\\tools\\opencode.exe");
        }

        AgentIntegrationAppSettings reloaded = PatchouliAppSettings.Load(settings.Path).Agents;
        reloaded.Enabled.Should().BeFalse();
        reloaded.SelectedAgentId.Should().Be("claude-acp");
        reloaded.FindCustomPath("opencode").Should().Be("C:\\tools\\opencode.exe");
        reloaded.FindCustomPath("claude-acp").Should().Be("C:\\tools\\claude.exe");
    }

    [Fact]
    public void Saving_a_default_section_writes_an_empty_override_map()
    {
        using TemporaryAppSettingsFile settings = new();

        (PatchouliAppSettings.Load(settings.Path) with { Agents = AgentIntegrationAppSettings.Default() })
            .Save(settings.Path).IsSuccess.Should().BeTrue();

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(settings.Path));
        JsonElement section = document.RootElement.GetProperty("Agents");
        section.GetProperty("Enabled").GetBoolean().Should().BeTrue();
        section.GetProperty("SelectedAgentId").GetString().Should().Be("opencode");
        section.GetProperty("CustomPaths").EnumerateObject().Should().BeEmpty();

        PatchouliAppSettings reloaded = PatchouliAppSettings.Load(settings.Path);
        reloaded.Agents.Enabled.Should().BeTrue();
        reloaded.Agents.SelectedAgentId.Should().Be("opencode");
        reloaded.Agents.CustomPaths.Should().BeEmpty();
    }

    [Fact]
    public void A_missing_agents_section_uses_the_defaults()
    {
        using TemporaryAppSettingsFile settings = new();
        WriteJson(settings.Path, "{}");

        AgentIntegrationAppSettings agents = PatchouliAppSettings.Load(settings.Path).Agents;

        agents.Enabled.Should().BeTrue();
        agents.SelectedAgentId.Should().Be("opencode");
        agents.CustomPaths.Should().BeNull();
    }

    [Fact]
    public void A_section_with_wrong_value_types_falls_back_field_by_field()
    {
        using TemporaryAppSettingsFile settings = new();
        WriteJson(settings.Path,
            """
            {
              "Agents": {
                "Enabled": "not-a-bool",
                "SelectedAgentId": 42,
                "CustomPaths": {
                  "opencode": 7,
                  " claude-acp ": " C:\\tools\\claude.exe ",
                  "blank": "   "
                }
              }
            }
            """);

        AgentIntegrationAppSettings agents = PatchouliAppSettings.Load(settings.Path).Agents;

        agents.Enabled.Should().BeTrue("a non-boolean Enabled is ignored");
        agents.SelectedAgentId.Should().Be("opencode", "a non-string id is ignored");
        agents.CustomPaths.Should().ContainSingle();
        agents.FindCustomPath("claude-acp").Should().Be("C:\\tools\\claude.exe");
        agents.FindCustomPath("blank").Should().BeNull();
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"agents\"")]
    [InlineData("null")]
    public void A_non_object_agents_section_uses_the_defaults(string section)
    {
        using TemporaryAppSettingsFile settings = new();
        WriteJson(settings.Path, $"{{\"Agents\": {section}}}");

        AgentIntegrationAppSettings agents = PatchouliAppSettings.Load(settings.Path).Agents;

        agents.Enabled.Should().BeTrue();
        agents.SelectedAgentId.Should().Be("opencode");
    }

    [Fact]
    public void An_unknown_selected_agent_normalizes_to_the_catalog_default_on_load()
    {
        using TemporaryAppSettingsFile settings = new();
        WriteJson(settings.Path,
            """{"Agents":{"Enabled":true,"SelectedAgentId":"not-a-catalog-agent"}}""");

        PatchouliAppSettings.Load(settings.Path).Agents.SelectedAgentId.Should().Be("opencode");
    }

    [Fact]
    public void The_legacy_AgentIntegration_alias_is_read()
    {
        using TemporaryAppSettingsFile settings = new();
        WriteJson(settings.Path,
            """
            {
              "AgentIntegration": {
                "Enabled": false,
                "SelectedAgentId": "claude-acp",
                "CustomPaths": { "claude-acp": "C:\\tools\\claude.exe" }
              }
            }
            """);

        AgentIntegrationAppSettings agents = PatchouliAppSettings.Load(settings.Path).Agents;

        agents.Enabled.Should().BeFalse();
        agents.SelectedAgentId.Should().Be("claude-acp");
        agents.FindCustomPath("claude-acp").Should().Be("C:\\tools\\claude.exe");
    }

    [Fact]
    public void The_Agents_section_wins_over_the_legacy_alias()
    {
        using TemporaryAppSettingsFile settings = new();
        WriteJson(settings.Path,
            """
            {
              "Agents": { "Enabled": true, "SelectedAgentId": "opencode" },
              "AgentIntegration": { "Enabled": false, "SelectedAgentId": "claude-acp" }
            }
            """);

        AgentIntegrationAppSettings agents = PatchouliAppSettings.Load(settings.Path).Agents;

        agents.Enabled.Should().BeTrue();
        agents.SelectedAgentId.Should().Be("opencode");
    }

    [Fact]
    public void Invalid_json_falls_back_to_defaults_and_reports_a_diagnostic()
    {
        using TemporaryAppSettingsFile settings = new();
        File.WriteAllText(settings.Path, "{ not valid json");

        SettingsLoadResult<PatchouliAppSettings> result = PatchouliAppSettings.LoadWithResult(settings.Path);

        result.IsSuccess.Should().BeFalse();
        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "settings_json_invalid");
        result.Settings.Agents.Enabled.Should().BeTrue();
        result.Settings.Agents.SelectedAgentId.Should().Be("opencode");
    }

    [Fact]
    public async Task Saving_only_the_agents_field_preserves_the_other_sections()
    {
        using TemporaryAppSettingsFile settings = new();
        PatchouliAppSettings initial = PatchouliAppSettings.Load(settings.Path);
        initial.Save(settings.Path).IsSuccess.Should().BeTrue();
        string originalRuntime = initial.Runtime.RuntimeDatabasePath;

        PatchouliAppSettings updated = initial with
        {
            Agents = new AgentIntegrationAppSettings(false, "claude-acp",
                new Dictionary<string, string> { ["claude-acp"] = "C:\\tools\\claude.exe" })
        };
        SettingsSaveResult save = await updated.SaveFieldLevelAsync(settings.Path,
            new HashSet<string>(StringComparer.Ordinal) { "Agents" });

        save.IsSuccess.Should().BeTrue(save.ErrorMessage);
        PatchouliAppSettings reloaded = PatchouliAppSettings.Load(settings.Path);
        reloaded.Runtime.RuntimeDatabasePath.Should().Be(originalRuntime);
        reloaded.MinerU.BaseUrl.Should().Be(initial.MinerU.BaseUrl);
        reloaded.Agents.Enabled.Should().BeFalse();
        reloaded.Agents.SelectedAgentId.Should().Be("claude-acp");
        reloaded.Agents.FindCustomPath("claude-acp").Should().Be("C:\\tools\\claude.exe");
    }

    [Fact]
    public void Saving_normalizes_a_hand_edited_override_map()
    {
        using TemporaryAppSettingsFile settings = new();
        PatchouliAppSettings current = PatchouliAppSettings.Load(settings.Path);
        AgentIntegrationAppSettings messy = new(true, "opencode",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [" opencode "] = " C:\\a.exe ",
                ["OPENCODE"] = "C:\\b.exe",
                ["blank"] = "   "
            });

        (current with { Agents = messy }).Save(settings.Path).IsSuccess.Should().BeTrue();

        AgentIntegrationAppSettings reloaded = PatchouliAppSettings.Load(settings.Path).Agents;
        reloaded.CustomPaths.Should().ContainSingle();
        reloaded.CustomPaths["opencode"].Should().Be("C:\\a.exe");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(settings.Path));
        document.RootElement.GetProperty("Agents").GetProperty("CustomPaths").EnumerateObject()
            .Select(property => property.Name).Should().Equal("opencode");
    }

    private static void WriteJson(string path, string json)
    {
        File.WriteAllText(path, json);
    }
}
