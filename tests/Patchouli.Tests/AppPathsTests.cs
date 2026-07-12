using FluentAssertions;
using Patchouli.UI;

namespace Patchouli.Tests;

public sealed class AppPathsTests
{
    [Fact]
    public void MacOS_uses_application_support_and_caches()
    {
        AppStorageLocations paths = new PlatformAppPaths(AppPlatform.MacOS, "/Users/test",
            "/Users/test/Library/Application Support", "/Applications/Patchouli.Net.app/Contents/MacOS").Resolve();

        paths.UserSettingsPath.Should()
            .Be(Path.GetFullPath("/Users/test/Library/Application Support/net.patchouli.app/settings.json"));
        paths.CacheDirectory.Should().Be(Path.GetFullPath("/Users/test/Library/Caches/net.patchouli.app"));
        paths.BundledDefaultsPath.Should()
            .Be(Path.GetFullPath("/Applications/Patchouli.Net.app/Contents/Resources/appsettings.json"));
    }

    [Fact]
    public void Linux_uses_valid_absolute_xdg_locations()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "xdg"));
        Dictionary<string, string?> values = new()
        {
            ["XDG_CONFIG_HOME"] = Path.Combine(root, "config"),
            ["XDG_DATA_HOME"] = Path.Combine(root, "data"),
            ["XDG_CACHE_HOME"] = Path.Combine(root, "cache")
        };
        AppStorageLocations paths = new PlatformAppPaths(AppPlatform.Linux, Path.Combine(root, "home"),
            Path.Combine(root, "unused"), Path.Combine(root, "app"), name => values[name]).Resolve();

        paths.UserSettingsPath.Should().Be(Path.Combine(root, "config", "patchouli", "settings.json"));
        paths.DataDirectory.Should().Be(Path.Combine(root, "data", "patchouli"));
        paths.CacheDirectory.Should().Be(Path.Combine(root, "cache", "patchouli"));
    }

    [Fact]
    public void Linux_rejects_relative_or_blank_xdg_locations()
    {
        AppStorageLocations paths =
            new PlatformAppPaths(AppPlatform.Linux, "/home/test", "/unused", "/opt/patchouli", _ => "relative/path")
                .Resolve();

        paths.UserSettingsPath.Should().Be(Path.GetFullPath("/home/test/.config/patchouli/settings.json"));
        paths.DataDirectory.Should().Be(Path.GetFullPath("/home/test/.local/share/patchouli"));
        paths.CacheDirectory.Should().Be(Path.GetFullPath("/home/test/.cache/patchouli"));
    }

    [Fact]
    public void Windows_uses_local_application_data()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "local-app-data"));
        AppStorageLocations paths =
            new PlatformAppPaths(AppPlatform.Windows, Path.GetTempPath(), root, Path.GetTempPath()).Resolve();

        paths.UserSettingsPath.Should().Be(Path.Combine(root, "Patchouli", "settings.json"));
        paths.DataDirectory.Should().Be(Path.Combine(root, "Patchouli"));
    }

    [Fact]
    public void Settings_layer_code_then_bundle_then_user_and_save_only_user()
    {
        string root = Path.Combine(Path.GetTempPath(), $"patchouli-paths-{Guid.NewGuid():N}");
        TestAppPaths appPaths = new(root);
        AppStorageLocations locations = appPaths.Resolve();
        Directory.CreateDirectory(Path.GetDirectoryName(locations.BundledDefaultsPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(locations.UserSettingsPath)!);
        File.WriteAllText(locations.BundledDefaultsPath,
            """{"Mcp":{"Port":5000},"MinerU":{"BaseUrl":"https://bundle.test"}}""");
        File.WriteAllText(locations.UserSettingsPath, """{"Mcp":{"Port":6000}}""");
        string bundledBefore = File.ReadAllText(locations.BundledDefaultsPath);

        try
        {
            PatchouliAppSettings settings = PatchouliAppSettings.Load(appPaths);
            settings.Mcp.Port.Should().Be(6000);
            settings.MinerU.BaseUrl.Should().Be("https://bundle.test");

            settings.Save(locations.UserSettingsPath);
            File.ReadAllText(locations.BundledDefaultsPath).Should().Be(bundledBefore);
            Directory.EnumerateFiles(Path.GetDirectoryName(locations.UserSettingsPath)!, "*.tmp").Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public void Database_inside_sync_is_rejected_before_parent_is_created()
    {
        string root = Path.Combine(Path.GetTempPath(), $"patchouli-guard-{Guid.NewGuid():N}");
        string sync = Path.Combine(root, "sync");
        string database = Path.Combine(sync, "runtime.sqlite");

        Action action = () => AppPathGuard.ValidateDatabasePath(database, sync);

        action.Should().Throw<InvalidOperationException>();
        Directory.Exists(sync).Should().BeFalse();
    }

    [Fact]
    public void Settings_save_returns_structured_failure_and_leaves_no_temporary_file()
    {
        string root = Path.Combine(Path.GetTempPath(), $"patchouli-settings-failure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string blockingFile = Path.Combine(root, "not-a-directory");
        File.WriteAllText(blockingFile, "occupied");
        try
        {
            SettingsSaveResult result = PatchouliAppSettings.Default(new TestAppPaths(root))
                .Save(Path.Combine(blockingFile, "settings.json"));

            result.IsSuccess.Should().BeFalse();
            result.ErrorCode.Should().Be("settings_io_failed");
            result.PathCategory.Should().Be("user_settings");
            Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Settings_load_reports_invalid_json_and_returns_defaults()
    {
        string root = Path.Combine(Path.GetTempPath(), $"patchouli-settings-load-{Guid.NewGuid():N}");
        TestAppPaths appPaths = new(root);
        AppStorageLocations locations = appPaths.Resolve();
        Directory.CreateDirectory(Path.GetDirectoryName(locations.UserSettingsPath)!);
        File.WriteAllText(locations.UserSettingsPath, "{ invalid json");
        SettingsLoadFailure? failure = null;
        try
        {
            PatchouliAppSettings settings = PatchouliAppSettings.Load(appPaths, value => failure = value);

            settings.Should().BeEquivalentTo(PatchouliAppSettings.Default(appPaths));
            failure.Should().NotBeNull();
            failure!.ErrorCode.Should().Be("settings_json_invalid");
            failure.PathCategory.Should().Be("user_settings");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Mutable_path_guard_uses_canonical_resolver_for_alias_boundary()
    {
        string bundle = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Patchouli.Net.app"));
        string alias = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "alias", "settings.json"));
        IRealPathResolver resolver = new MappingRealPathResolver(alias,
            Path.Combine(bundle, "Contents", "Resources", "settings.json"));

        Action action = () => AppPathGuard.ValidateMutablePath(alias, Path.Combine(bundle, "Contents", "MacOS"),
            resolver);

        action.Should().Throw<InvalidOperationException>();
    }

    private sealed class MappingRealPathResolver(string source, string destination) : IRealPathResolver
    {
        public string Resolve(string path)
        {
            return string.Equals(Path.GetFullPath(path), source, StringComparison.Ordinal)
                ? destination
                : Path.GetFullPath(path);
        }
    }
}
