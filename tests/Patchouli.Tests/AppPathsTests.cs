using FluentAssertions;
using Patchouli.UI;

namespace Patchouli.Tests;

public sealed class AppPathsTests
{
    [Fact]
    public void MacOS_uses_application_support_and_caches()
    {
        var paths = new PlatformAppPaths(AppPlatform.MacOS, "/Users/test", "/Users/test/Library/Application Support", "/Applications/Patchouli.Net.app/Contents/MacOS").Resolve();

        paths.UserSettingsPath.Should().Be(Path.GetFullPath("/Users/test/Library/Application Support/net.patchouli.app/settings.json"));
        paths.CacheDirectory.Should().Be(Path.GetFullPath("/Users/test/Library/Caches/net.patchouli.app"));
        paths.BundledDefaultsPath.Should().Be(Path.GetFullPath("/Applications/Patchouli.Net.app/Contents/Resources/appsettings.json"));
    }

    [Fact]
    public void Linux_uses_valid_absolute_xdg_locations()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "xdg"));
        var values = new Dictionary<string, string?>
        {
            ["XDG_CONFIG_HOME"] = Path.Combine(root, "config"),
            ["XDG_DATA_HOME"] = Path.Combine(root, "data"),
            ["XDG_CACHE_HOME"] = Path.Combine(root, "cache")
        };
        var paths = new PlatformAppPaths(AppPlatform.Linux, Path.Combine(root, "home"), Path.Combine(root, "unused"), Path.Combine(root, "app"), name => values[name]).Resolve();

        paths.UserSettingsPath.Should().Be(Path.Combine(root, "config", "patchouli", "settings.json"));
        paths.DataDirectory.Should().Be(Path.Combine(root, "data", "patchouli"));
        paths.CacheDirectory.Should().Be(Path.Combine(root, "cache", "patchouli"));
    }

    [Fact]
    public void Linux_rejects_relative_or_blank_xdg_locations()
    {
        var paths = new PlatformAppPaths(AppPlatform.Linux, "/home/test", "/unused", "/opt/patchouli", _ => "relative/path").Resolve();

        paths.UserSettingsPath.Should().Be(Path.GetFullPath("/home/test/.config/patchouli/settings.json"));
        paths.DataDirectory.Should().Be(Path.GetFullPath("/home/test/.local/share/patchouli"));
        paths.CacheDirectory.Should().Be(Path.GetFullPath("/home/test/.cache/patchouli"));
    }

    [Fact]
    public void Windows_uses_local_application_data()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "local-app-data"));
        var paths = new PlatformAppPaths(AppPlatform.Windows, Path.GetTempPath(), root, Path.GetTempPath()).Resolve();

        paths.UserSettingsPath.Should().Be(Path.Combine(root, "Patchouli", "settings.json"));
        paths.DataDirectory.Should().Be(Path.Combine(root, "Patchouli"));
    }

    [Fact]
    public void Settings_layer_code_then_bundle_then_user_and_save_only_user()
    {
        var root = Path.Combine(Path.GetTempPath(), $"patchouli-paths-{Guid.NewGuid():N}");
        var appPaths = new TestAppPaths(root);
        var locations = appPaths.Resolve();
        Directory.CreateDirectory(Path.GetDirectoryName(locations.BundledDefaultsPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(locations.UserSettingsPath)!);
        File.WriteAllText(locations.BundledDefaultsPath, """{"Mcp":{"Port":5000},"MinerU":{"BaseUrl":"https://bundle.test"}}""");
        File.WriteAllText(locations.UserSettingsPath, """{"Mcp":{"Port":6000}}""");
        var bundledBefore = File.ReadAllText(locations.BundledDefaultsPath);

        try
        {
            var settings = PatchouliAppSettings.Load(appPaths);
            settings.Mcp.Port.Should().Be(6000);
            settings.MinerU.BaseUrl.Should().Be("https://bundle.test");

            settings.Save(locations.UserSettingsPath);
            File.ReadAllText(locations.BundledDefaultsPath).Should().Be(bundledBefore);
            Directory.EnumerateFiles(Path.GetDirectoryName(locations.UserSettingsPath)!, "*.tmp").Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Database_inside_sync_is_rejected_before_parent_is_created()
    {
        var root = Path.Combine(Path.GetTempPath(), $"patchouli-guard-{Guid.NewGuid():N}");
        var sync = Path.Combine(root, "sync");
        var database = Path.Combine(sync, "runtime.sqlite");

        var action = () => AppPathGuard.ValidateDatabasePath(database, sync);

        action.Should().Throw<InvalidOperationException>();
        Directory.Exists(sync).Should().BeFalse();
    }
}
