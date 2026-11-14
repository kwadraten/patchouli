using FluentAssertions;
using Patchouli.Core.Settings;
using Patchouli.UI;

namespace Patchouli.Tests;

/// <summary>Issue #56: typed "Network" proxy settings — normalization, validation, persistence.</summary>
public sealed class NetworkProxySettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(),
        $"patchouli-network-settings-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("manual", NetworkProxyModes.Manual)]
    [InlineData("MANUAL", NetworkProxyModes.Manual)]
    [InlineData("none", NetworkProxyModes.None)]
    [InlineData("off", NetworkProxyModes.System)]
    [InlineData("", NetworkProxyModes.System)]
    [InlineData("system", NetworkProxyModes.System)]
    public void Unknown_or_cased_modes_normalize_safely(string input, string expected)
    {
        NetworkProxyModes.Normalize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("http://proxy.corp:8080")]
    [InlineData("https://proxy.corp:8443")]
    [InlineData("socks5://127.0.0.1:1080")]
    [InlineData("socks5h://127.0.0.1:1080")]
    public void Manual_proxy_address_accepts_supported_schemes(string address)
    {
        Uri? uri = NetworkAppSettings.TryCreateManualProxyUri(address);
        uri.Should().NotBeNull();
    }

    [Fact]
    public void Socks5h_maps_to_the_dotnet_socks5_scheme()
    {
        NetworkAppSettings.TryCreateManualProxyUri("socks5h://127.0.0.1:1080")!.Scheme
            .Should().Be("socks5");
    }

    [Theory]
    [InlineData("ftp://proxy.corp:21")]
    [InlineData("pac://proxy.corp/proxy.pac")]
    [InlineData("http://user:secret@proxy.corp:8080")]
    [InlineData("not a url")]
    [InlineData("proxy.corp:8080")]
    public void Manual_proxy_address_rejects_unsupported_or_credentialed_uris(string address)
    {
        NetworkAppSettings.TryCreateManualProxyUri(address).Should().BeNull();
    }

    [Fact]
    public void Validation_flags_an_incomplete_manual_configuration()
    {
        NetworkAppSettings settings = new(NetworkProxyModes.Manual, "", "", false, []);
        settings.Validate().Should().Contain("proxy_address_required");
    }

    [Fact]
    public void Validation_flags_bad_scheme_and_url_credentials()
    {
        new NetworkAppSettings(NetworkProxyModes.Manual, "ftp://x", "", false, [])
            .Validate().Should().Contain("proxy_scheme_unsupported");
        new NetworkAppSettings(NetworkProxyModes.Manual, "http://u:p@h:1", "", false, [])
            .Validate().Should().Contain("proxy_credentials_in_url");
    }

    [Fact]
    public void Validation_is_silent_for_system_and_valid_manual()
    {
        NetworkAppSettings.Default().Validate().Should().BeEmpty();
        new NetworkAppSettings(NetworkProxyModes.Manual, "http://proxy:8080", "", false, [])
            .Validate().Should().BeEmpty();
    }

    [Fact]
    public void Network_section_round_trips_through_the_settings_file()
    {
        string path = Path.Combine(_directory, "settings.json");
        PatchouliAppSettings defaults = PatchouliAppSettings.Default();
        PatchouliAppSettings settings = defaults with
        {
            Network = new NetworkAppSettings(NetworkProxyModes.Manual, "socks5h://127.0.0.1:1080",
                "svc-proxy", true, ["*.internal", "localhost"])
        };
        settings.Save(path).IsSuccess.Should().BeTrue();

        PatchouliAppSettings loaded = PatchouliAppSettings.Load(path);
        loaded.Network.ProxyMode.Should().Be(NetworkProxyModes.Manual);
        loaded.Network.ProxyAddress.Should().Be("socks5h://127.0.0.1:1080");
        loaded.Network.ProxyUsername.Should().Be("svc-proxy");
        loaded.Network.HasProxyPassword.Should().BeTrue();
        loaded.Network.BypassList.Should().Equal("*.internal", "localhost");
    }

    [Fact]
    public async Task A_field_level_network_save_does_not_touch_other_sections()
    {
        string path = Path.Combine(_directory, "settings.json");
        PatchouliAppSettings settings = PatchouliAppSettings.Default() with
        {
            Network = new NetworkAppSettings(NetworkProxyModes.None, "", "", false, [])
        };
        await settings.SaveFieldLevelAsync(path, new HashSet<string> { "Network" });

        PatchouliAppSettings loaded = PatchouliAppSettings.Load(path);
        loaded.Network.ProxyMode.Should().Be(NetworkProxyModes.None);
        loaded.Runtime.RuntimeDatabasePath.Should()
            .Be(PatchouliAppSettings.Default().Runtime.RuntimeDatabasePath);
    }

    [Fact]
    public async Task The_settings_json_never_carries_a_proxy_password()
    {
        string path = Path.Combine(_directory, "settings.json");
        PatchouliAppSettings settings = PatchouliAppSettings.Default() with
        {
            Network = new NetworkAppSettings(NetworkProxyModes.Manual, "http://proxy:8080", "alice",
                true, [])
        };
        settings.Save(path).IsSuccess.Should().BeTrue();

        string json = await File.ReadAllTextAsync(path);
        json.Should().Contain("\"HasProxyPassword\": true");
        // The password itself never persists in the settings file — only the flag does.
        // ("ProxyPassword" alone would false-positive inside "HasProxyPassword".)
        json.Should().NotContain("\"ProxyPassword\"");
    }

    [Fact]
    public void The_diagnostic_description_drops_url_and_username()
    {
        NetworkAppSettings settings = new(NetworkProxyModes.Manual, "http://alice:pw@corp:8080",
            "alice", true, []);
        settings.DescribeForDiagnostics().Should().Be("manual (address set)")
            .And.NotContain("alice").And.NotContain("corp");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }
}
