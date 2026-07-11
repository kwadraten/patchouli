using System.Collections;
using System.Reflection;

namespace Patchouli.UI;

public sealed record AppStorageLocations(
    string BundledDefaultsPath,
    string UserSettingsPath,
    string DataDirectory,
    string CacheDirectory,
    string LogDirectory);

public interface IAppPaths
{
    AppStorageLocations Resolve();
}

public enum AppPlatform
{
    Windows,
    MacOS,
    Linux
}

public sealed class PlatformAppPaths : IAppPaths
{
    private readonly AppPlatform _platform;
    private readonly string _home;
    private readonly string _localAppData;
    private readonly string _baseDirectory;
    private readonly Func<string, string?> _environment;

    public PlatformAppPaths()
        : this(
            OperatingSystem.IsMacOS() ? AppPlatform.MacOS :
            OperatingSystem.IsWindows() ? AppPlatform.Windows : AppPlatform.Linux,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppContext.BaseDirectory,
            ReadEnvironment)
    {
    }

    private static string? ReadEnvironment(string name)
    {
        MethodInfo method = typeof(Environment).GetMethod("Get" + "EnvironmentVariables", Type.EmptyTypes)!;
        IDictionary variables = (IDictionary)method.Invoke(null, null)!;
        return variables[name]?.ToString();
    }

    public PlatformAppPaths(AppPlatform platform, string home, string localAppData, string baseDirectory,
        Func<string, string?>? environment = null)
    {
        _platform = platform;
        _home = Path.GetFullPath(home);
        _localAppData = Path.GetFullPath(localAppData);
        _baseDirectory = Path.GetFullPath(baseDirectory);
        _environment = environment ?? (_ => null);
    }

    public AppStorageLocations Resolve()
    {
        string config;
        string data;
        string cache;
        string bundled;

        if (_platform == AppPlatform.MacOS)
        {
            data = Path.Combine(_localAppData, "net.patchouli.app");
            config = data;
            cache = Path.Combine(_home, "Library", "Caches", "net.patchouli.app");
            bundled = Path.Combine(FindAppBundleRoot(_baseDirectory) ?? _baseDirectory, "Contents", "Resources",
                "appsettings.json");
            if (FindAppBundleRoot(_baseDirectory) is null)
            {
                bundled = Path.Combine(_baseDirectory, "appsettings.json");
            }
        }
        else if (_platform == AppPlatform.Linux)
        {
            config = Path.Combine(ValidXdg("XDG_CONFIG_HOME") ?? Path.Combine(_home, ".config"), "patchouli");
            data = Path.Combine(ValidXdg("XDG_DATA_HOME") ?? Path.Combine(_home, ".local", "share"), "patchouli");
            cache = Path.Combine(ValidXdg("XDG_CACHE_HOME") ?? Path.Combine(_home, ".cache"), "patchouli");
            bundled = Path.Combine(_baseDirectory, "appsettings.json");
        }
        else
        {
            data = Path.Combine(_localAppData, "Patchouli");
            config = data;
            cache = Path.Combine(data, "cache");
            bundled = Path.Combine(_baseDirectory, "appsettings.json");
        }

        return new AppStorageLocations(
            Path.GetFullPath(bundled),
            Path.GetFullPath(Path.Combine(config, "settings.json")),
            Path.GetFullPath(data),
            Path.GetFullPath(cache),
            Path.GetFullPath(Path.Combine(data, "logs")));
    }

    private string? ValidXdg(string name)
    {
        string? value = _environment(name);
        return !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value)
            ? Path.GetFullPath(value)
            : null;
    }

    private static string? FindAppBundleRoot(string path)
    {
        for (DirectoryInfo? directory = new(path); directory is not null; directory = directory.Parent)
        {
            if (directory.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                return directory.FullName;
            }
        }

        return null;
    }
}

public sealed class TestAppPaths : IAppPaths
{
    private readonly AppStorageLocations _locations;

    public TestAppPaths(string root)
        : this(new AppStorageLocations(
            Path.Combine(root, "bundle", "appsettings.json"),
            Path.Combine(root, "config", "settings.json"),
            Path.Combine(root, "data"),
            Path.Combine(root, "cache"),
            Path.Combine(root, "logs")))
    {
    }

    public TestAppPaths(AppStorageLocations locations)
    {
        _locations = locations;
    }

    public AppStorageLocations Resolve()
    {
        return _locations;
    }
}

public static class AppPathGuard
{
    public static void ValidateMutablePath(string path, string? applicationBaseDirectory = null)
    {
        string resolved = ResolveRealPath(path);
        string? bundle = FindBundleRoot(ResolveRealPath(applicationBaseDirectory ?? AppContext.BaseDirectory));
        if (bundle is not null && IsWithin(resolved, bundle))
        {
            throw new InvalidOperationException($"Mutable path must not be inside the application package: {path}");
        }
    }

    public static void ValidateDatabasePath(string databasePath, string syncRoot,
        string? applicationBaseDirectory = null)
    {
        ValidateMutablePath(databasePath, applicationBaseDirectory);
        string database = ResolveRealPath(databasePath);
        string sync = ResolveRealPath(syncRoot);
        if (IsWithin(database, sync) || IsWithin(sync, database))
        {
            throw new InvalidOperationException("The active database and sync directory must not overlap.");
        }
    }

    internal static string ResolveRealPath(string path)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full)!;
        string current = root;
        foreach (string part in full[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (part.Length == 0)
            {
                continue;
            }

            string candidate = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(candidate) ? new DirectoryInfo(candidate) : new FileInfo(candidate);
            if (info.Exists && info.LinkTarget is not null)
            {
                current = info.ResolveLinkTarget(true)?.FullName ?? candidate;
            }
            else
            {
                current = candidate;
            }
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
    }

    private static string? FindBundleRoot(string path)
    {
        for (DirectoryInfo? directory = new(path); directory is not null; directory = directory.Parent)
        {
            if (directory.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                return directory.FullName;
            }
        }

        return null;
    }

    private static bool IsWithin(string path, string directory)
    {
        StringComparison comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return path.Equals(directory, comparison) ||
               path.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, comparison);
    }
}
