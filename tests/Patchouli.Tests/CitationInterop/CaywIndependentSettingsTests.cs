using System.Text.Json.Nodes;
using FluentAssertions;
using Patchouli.Host.Settings;
using Patchouli.UI;

namespace Patchouli.Tests.CitationInterop;

/// <summary>
/// Independent verification of the CitationInterop settings section
/// (issue #14 §5, checklist item 7): hand-edited or malformed sections must
/// load as defaults rather than break settings loading, and a CAYW-only
/// save must leave every other section exactly as it was.
/// </summary>
public sealed class CaywIndependentSettingsTests
{
    [Fact]
    public async Task Non_object_cayw_section_loads_defaults()
    {
        using TemporaryAppSettingsFile file = new();
        await File.WriteAllTextAsync(file.Path,
            """{"Mcp":{"Port":4537},"CitationInterop":[1,2,3]}""");

        PatchouliAppSettings loaded = PatchouliAppSettings.Load(file.Path);

        loaded.CitationInterop.Enabled.Should().BeFalse();
        loaded.CitationInterop.Port.Should().Be(23119);
        loaded.Mcp.Port.Should().Be(4537);
    }

    [Fact]
    public async Task Wrong_typed_cayw_values_fall_back_individually()
    {
        using TemporaryAppSettingsFile file = new();
        await File.WriteAllTextAsync(file.Path,
            """{"CitationInterop":{"Enabled":"yes","Port":"abc"}}""");

        PatchouliAppSettings loaded = PatchouliAppSettings.Load(file.Path);

        loaded.CitationInterop.Enabled.Should().BeFalse();
        loaded.CitationInterop.Port.Should().Be(23119);
    }

    [Fact]
    public async Task Out_of_range_port_in_file_is_normalized_but_valid_values_survive()
    {
        using TemporaryAppSettingsFile file = new();
        await File.WriteAllTextAsync(file.Path,
            """{"CitationInterop":{"Enabled":true,"Port":0}}""");

        PatchouliAppSettings loaded = PatchouliAppSettings.Load(file.Path);

        loaded.CitationInterop.Enabled.Should().BeTrue();
        loaded.CitationInterop.Port.Should().Be(23119, "port 0 is not a usable endpoint and normalizes to the default");
        loaded.CitationInterop.Endpoint.Should().Be("http://127.0.0.1:23119");

        using TemporaryAppSettingsFile second = new();
        await File.WriteAllTextAsync(second.Path,
            """{"CitationInterop":{"Enabled":true,"Port":65535}}""");
        PatchouliAppSettings reloaded = PatchouliAppSettings.Load(second.Path);
        reloaded.CitationInterop.Port.Should().Be(65535);
    }

    [Fact]
    public async Task Cayw_only_save_leaves_other_sections_byte_identical()
    {
        using TemporaryAppSettingsFile file = new();
        const string original =
            """{"Mcp":{"Port":4537,"BindAddress":"127.0.0.1"},"CitationInterop":{"Enabled":false,"Port":23119},"CustomMarker":{"Keep":true,"Note":"do not touch"}}""";
        await File.WriteAllTextAsync(file.Path, original);
        JsonObject before = JsonNode.Parse(original)!.AsObject();
        string mcpBefore = before["Mcp"]!.ToJsonString();
        string markerBefore = before["CustomMarker"]!.ToJsonString();

        PatchouliAppSettings settings = PatchouliAppSettings.Load(file.Path) with
        {
            CitationInterop = new CitationInteropAppSettings(true, 24001)
        };
        SettingsSaveResult saved = await settings.SaveFieldLevelAsync(
            file.Path, new HashSet<string> { "CitationInterop" });
        saved.IsSuccess.Should().BeTrue();

        JsonObject after = JsonNode.Parse(await File.ReadAllTextAsync(file.Path))!.AsObject();
        after["Mcp"]!.ToJsonString().Should().Be(mcpBefore,
            "saving the CAYW section must not rewrite the MCP section");
        after["CustomMarker"]!.ToJsonString().Should().Be(markerBefore,
            "unknown sections must survive a CAYW save untouched");
        after["CitationInterop"]!["Enabled"]!.GetValue<bool>().Should().BeTrue();
        after["CitationInterop"]!["Port"]!.GetValue<int>().Should().Be(24001);
    }
}
