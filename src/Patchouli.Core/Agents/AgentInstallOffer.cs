namespace Patchouli.Core.Agents;

/// <summary>One package-manager distribution advertised by the ACP registry.</summary>
/// <param name="Kind"><c>npx</c>, <c>uvx</c>, or another registry key.</param>
/// <param name="Package">Package spec including its pinned version.</param>
/// <param name="Arguments">Arguments the registry uses for its ACP server form.</param>
public sealed record AgentInstallPackageOffer(
    string Kind,
    string Package,
    IReadOnlyList<string> Arguments);

/// <summary>
/// Structured "this agent can be installed" information for the current platform. Detection and the
/// settings UI may display it, but D2 never downloads, extracts or installs anything.
/// </summary>
/// <param name="AgentId">Catalog id.</param>
/// <param name="DisplayName">Registry/catalog display name.</param>
/// <param name="RegistryVersion">Version advertised by the registry snapshot.</param>
/// <param name="PlatformKey">Registry platform key, for example <c>windows-x86_64</c>.</param>
/// <param name="DownloadUrl">Platform binary archive URL, or null when no binary is published.</param>
/// <param name="Sha256">Archive checksum from the registry, when published.</param>
/// <param name="ArchiveCommand">Command inside the archive, for example <c>./opencode.exe</c>.</param>
/// <param name="ArchiveArguments">Arguments the registry advertises for the ACP form, not for the TUI.</param>
/// <param name="PackageManagers">Alternative npx/uvx distributions.</param>
/// <param name="Repository">Upstream repository URL.</param>
/// <param name="License">SPDX license identifier.</param>
/// <param name="LicenseUrl">License URL.</param>
public sealed record AgentInstallOffer(
    string AgentId,
    string DisplayName,
    string RegistryVersion,
    string? PlatformKey,
    string? DownloadUrl,
    string? Sha256,
    string? ArchiveCommand,
    IReadOnlyList<string> ArchiveArguments,
    IReadOnlyList<AgentInstallPackageOffer> PackageManagers,
    string? Repository,
    string? License,
    string? LicenseUrl)
{
    /// <summary>True when the registry gives a platform binary archive for this machine.</summary>
    public bool HasPlatformBinary => !string.IsNullOrWhiteSpace(DownloadUrl);

    /// <summary>True when there is any registry distribution the UI can offer.</summary>
    public bool IsInstallable => HasPlatformBinary || PackageManagers.Count > 0;

    /// <summary>The registry only describes installation; it never implies automatic installation.</summary>
    public bool SupportsAutomaticInstall => false;
}

/// <summary>Provides registry-derived install offers. Implementations must not download anything.</summary>
public interface IAgentInstallOfferProvider
{
    Task<AgentInstallOffer?> GetOfferAsync(AgentCatalogEntry entry,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentInstallOffer>> GetAllOffersAsync(CancellationToken cancellationToken = default);
}

/// <summary>Maps the runtime OS/architecture onto the ACP registry's platform keys.</summary>
public static class AgentRegistryPlatformKey
{
    public static string? TryGet(AgentPlatform platform, AgentArchitecture architecture)
    {
        return (platform, architecture) switch
        {
            (AgentPlatform.Windows, AgentArchitecture.X64) => "windows-x86_64",
            (AgentPlatform.Windows, AgentArchitecture.Arm64) => "windows-aarch64",
            (AgentPlatform.MacOS, AgentArchitecture.X64) => "darwin-x86_64",
            (AgentPlatform.MacOS, AgentArchitecture.Arm64) => "darwin-aarch64",
            (AgentPlatform.Linux, AgentArchitecture.X64) => "linux-x86_64",
            (AgentPlatform.Linux, AgentArchitecture.Arm64) => "linux-aarch64",
            _ => null
        };
    }
}
