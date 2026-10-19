using System.Runtime.InteropServices;
using Patchouli.Core.Results;

namespace Patchouli.Core.Update;

/// <summary>
/// Issue #15: pure, testable selection of the installer asset matching the
/// current OS/architecture. Asset naming follows the repository's release
/// workflow (see <c>.github/workflows/release.yml</c>):
/// <list type="bullet">
/// <item>Windows x64: <c>Patchouli.NET-{version}-win-x64-setup.exe</c>
/// (the <c>-smoke-setup</c> diagnostic variant is explicitly excluded).</item>
/// <item>macOS arm64: <c>Patchouli.NET-{version}-osx-arm64.dmg</c>.</item>
/// <item>Linux x64: <c>Patchouli.NET-{version}-linux-x64.AppImage</c>
/// (AppImage is the default; deb/rpm/flatpak need elevated or
/// distro-specific installers and are not auto-launchable — see ADR 0048).</item>
/// </list>
/// </summary>
public static class UpdateAssetSelector
{
    private const string SmokeMarker = "-smoke";

    public static Result<ReleaseAssetInfo> SelectAsset(
        ReleaseInfo release,
        OSPlatform platform,
        Architecture architecture)
    {
        ArgumentNullException.ThrowIfNull(release);

        string? suffix = (platform, architecture) switch
        {
            var (os, arch) when os == OSPlatform.Windows && arch == Architecture.X64 =>
                "-win-x64-setup.exe",
            var (os, arch) when os == OSPlatform.OSX && arch == Architecture.Arm64 =>
                "-osx-arm64.dmg",
            var (os, arch) when os == OSPlatform.Linux && arch == Architecture.X64 =>
                "-linux-x64.AppImage",
            _ => null,
        };

        if (suffix is null)
        {
            return Result<ReleaseAssetInfo>.Failure(AppErrorCodes.ValidationFailed,
                $"版本 {release.Version} 没有为当前平台 ({DescribePlatform(platform, architecture)}) " +
                "提供安装包，无法自动更新。");
        }

        List<ReleaseAssetInfo> candidates = release.Assets
            .Where(asset => asset.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .Where(asset => !asset.Name.Contains(SmokeMarker, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
        {
            return Result<ReleaseAssetInfo>.Failure(AppErrorCodes.ValidationFailed,
                $"版本 {release.Version} 的发布页中没有找到当前平台的安装包 " +
                $"（期望文件名以 \"{suffix}\" 结尾），无法自动更新。");
        }

        // Prefer the asset whose embedded version matches the release version;
        // fall back to the first candidate if naming ever drifts.
        string versionText = release.Version.ToString();
        ReleaseAssetInfo selected = candidates.FirstOrDefault(
            asset => asset.Name.Contains(versionText, StringComparison.OrdinalIgnoreCase))
            ?? candidates[0];

        return Result<ReleaseAssetInfo>.Success(selected);
    }

    public static string DescribePlatform(OSPlatform platform, Architecture architecture)
    {
        string os = platform == OSPlatform.Windows ? "Windows"
            : platform == OSPlatform.OSX ? "macOS"
            : platform == OSPlatform.Linux ? "Linux"
            : platform.ToString();
        string arch = architecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => architecture.ToString().ToLowerInvariant(),
        };
        return $"{os} {arch}";
    }

    /// <summary>The platform/architecture this process is running on.</summary>
    public static (OSPlatform Platform, Architecture Architecture) CurrentPlatform()
    {
        OSPlatform platform = OperatingSystem.IsWindows() ? OSPlatform.Windows
            : OperatingSystem.IsMacOS() ? OSPlatform.OSX
            : OperatingSystem.IsLinux() ? OSPlatform.Linux
            : default;
        return (platform, RuntimeInformation.OSArchitecture);
    }
}
