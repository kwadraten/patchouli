using System.Runtime.InteropServices;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;

namespace Patchouli.Host.Plugins;

public sealed record PluginStoreOptions
{
    public required string DataDirectory { get; init; }

    public string IndexUrl { get; init; } =
        "https://kwadraten.github.io/patchouli-community/index/v1/index.json";

    public string SignatureUrl { get; init; } =
        "https://kwadraten.github.io/patchouli-community/index/v1/index.json.minisig";

    public IReadOnlyList<string> PublicKeys { get; init; } =
        ["RWTZ1DMVvPaw/MopSgCdUn6QcdmZTjXAps4mrvl9eacvDGi9fwZIDAOA"];

    public string CurrentPlatform { get; init; } = DetectPlatform();

    private static string DetectPlatform()
    {
        string architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException(
                $"Plugin packages are not published for {RuntimeInformation.OSArchitecture}.")
        };
        string system = OperatingSystem.IsWindows() ? "win" :
            OperatingSystem.IsMacOS() ? "osx" :
            OperatingSystem.IsLinux() ? "linux" :
            throw new PlatformNotSupportedException("This operating system does not support community plugins.");
        return $"{system}-{architecture}";
    }
}

public sealed record PluginStoreItem(
    PluginCatalogEntry Catalog,
    string? InstalledVersion,
    bool IsEnabled,
    bool IsManaged,
    IReadOnlyList<string> InstalledVersions,
    string? UnavailableReason)
{
    public bool UpdateAvailable => InstalledVersion is not null &&
                                   ExtensionVersion.Parse(Catalog.Version).CompareTo(
                                       ExtensionVersion.Parse(InstalledVersion)) > 0;

    public string? RollbackVersion => InstalledVersion is null
        ? null
        : InstalledVersions.FirstOrDefault(version =>
            ExtensionVersion.Parse(version).CompareTo(ExtensionVersion.Parse(InstalledVersion)) < 0);
}

public sealed record PluginStoreSnapshot(
    IReadOnlyList<PluginStoreItem> Items,
    DateTimeOffset GeneratedAt,
    bool FromCache,
    string? Warning);

public interface IPluginStore
{
    Task<Result<PluginStoreSnapshot>> RefreshAsync(bool force, CancellationToken cancellationToken = default);
    Task<Result<PluginDefinition>> InstallAsync(string pluginId, CancellationToken cancellationToken = default);
    Task<Result> SetEnabledAsync(string pluginId, bool enabled, CancellationToken cancellationToken = default);

    Task<Result<PluginDefinition>> ActivateAsync(string pluginId, string version,
        CancellationToken cancellationToken = default);
}
