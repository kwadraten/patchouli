using System.Text.Json.Nodes;
using FluentAssertions;
using Patchouli.Host.Settings;
using Patchouli.UI;

namespace Patchouli.Tests.CitationInterop;

/// <summary>CitationInterop device-local settings (issue #14 §5/§9).</summary>
public sealed class CitationInteropSettingsTests
{
    [Fact]
    public async Task Old_settings_file_without_the_section_loads_defaults()
    {
        using TemporaryAppSettingsFile file = new();
        await File.WriteAllTextAsync(file.Path,
            """{"Mcp":{"Port":4537}}""");

        PatchouliAppSettings loaded = PatchouliAppSettings.Load(file.Path);

        loaded.CitationInterop.Enabled.Should().BeFalse();
        loaded.CitationInterop.Port.Should().Be(23119);
        loaded.CitationInterop.Endpoint.Should().Be("http://127.0.0.1:23119");
        loaded.Mcp.Port.Should().Be(4537);
    }

    [Fact]
    public async Task Update_and_save_persists_and_preserves_other_sections()
    {
        using TemporaryAppSettingsFile file = new();
        await File.WriteAllTextAsync(file.Path,
            """{"Mcp":{"Port":4537,"BindAddress":"127.0.0.1"}}""");
        PatchouliAppSettings initial = PatchouliAppSettings.Load(file.Path);
        await using AppSettingsStore store = new(file.Path, initial, TimeSpan.FromHours(1));

        store.Update(
            settings => settings with { CitationInterop = new CitationInteropAppSettings(true, 23120) },
            "CitationInterop");
        SettingsSaveResult saved = await store.SaveImmediatelyAsync();
        saved.IsSuccess.Should().BeTrue();

        PatchouliAppSettings reloaded = PatchouliAppSettings.Load(file.Path);
        reloaded.CitationInterop.Enabled.Should().BeTrue();
        reloaded.CitationInterop.Port.Should().Be(23120);
        reloaded.Mcp.Port.Should().Be(4537); // the MCP section is untouched
    }

    [Fact]
    public async Task Field_level_save_writes_only_the_citation_interop_section()
    {
        using TemporaryAppSettingsFile file = new();
        await File.WriteAllTextAsync(file.Path,
            """{"Mcp":{"Port":4537},"CitationInterop":{"Enabled":false,"Port":23119},"CustomMarker":{"Keep":true}}""");
        PatchouliAppSettings settings = PatchouliAppSettings.Load(file.Path) with
        {
            CitationInterop = new CitationInteropAppSettings(true, 24000),
            Mcp = PatchouliAppSettings.Load(file.Path).Mcp with { Port = 9999 }
        };

        // Only CitationInterop is dirty: the in-memory MCP change must NOT be
        // written, and unknown sections in the file must survive.
        SettingsSaveResult saved = await settings.SaveFieldLevelAsync(
            file.Path, new HashSet<string> { "CitationInterop" });
        saved.IsSuccess.Should().BeTrue();

        JsonObject root = JsonNode.Parse(await File.ReadAllTextAsync(file.Path))!.AsObject();
        root["CitationInterop"]!["Enabled"]!.GetValue<bool>().Should().BeTrue();
        root["CitationInterop"]!["Port"]!.GetValue<int>().Should().Be(24000);
        root["Mcp"]!["Port"]!.GetValue<int>().Should().Be(4537);
        root["CustomMarker"]!["Keep"]!.GetValue<bool>().Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(70000)]
    public void NormalizePort_falls_back_to_the_default(int port)
    {
        CitationInteropAppSettings.NormalizePort(port).Should().Be(23119);
    }

    [Fact]
    public void NormalizePort_keeps_valid_ports()
    {
        CitationInteropAppSettings.NormalizePort(8080).Should().Be(8080);
    }
}
