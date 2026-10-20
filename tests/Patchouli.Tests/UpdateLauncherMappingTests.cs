using FluentAssertions;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Update;

namespace Patchouli.Tests;

/// <summary>
/// Issue #15 (review round 2): the downloader→launcher extension contract.
/// <see cref="UpdateDownloader"/> keeps the asset's installer extension on the
/// temp file (P1 fix: a hardcoded ".tmp" missed every launcher branch);
/// <see cref="UpdateLauncher"/> dispatches on it — <c>.exe</c> → Windows,
/// <c>.dmg</c> → macOS, <c>.AppImage</c> → Linux. Anything else falls back to
/// <c>.tmp</c>, which matches none of the launcher's platform branches and is
/// therefore never executed as an installer. These tests cover the pure
/// mapping logic (plus the launcher's no-launch guards); no process is ever
/// started.
/// </summary>
public sealed class UpdateLauncherMappingTests
{
    [Theory]
    [InlineData("Patchouli.NET-0.3.13-win-x64-setup.exe", ".exe")]
    // The allowlist match is case-insensitive but the asset's own casing is
    // kept; the launcher branches use OrdinalIgnoreCase as well.
    [InlineData("Patchouli.NET-0.3.13-WIN-X64-SETUP.EXE", ".EXE")]
    [InlineData("Patchouli.NET-0.3.13-osx-arm64.dmg", ".dmg")]
    [InlineData("Patchouli.NET-0.3.13-osx-arm64.DMG", ".DMG")]
    [InlineData("Patchouli.NET-0.3.13-linux-x64.AppImage", ".AppImage")]
    [InlineData("patchouli-0.3.13-linux-x64.appimage", ".appimage")]
    // Unknown kinds fall back to .tmp so the launcher has no branch for them.
    [InlineData("patchouli-0.3.13-release-notes.zip", ".tmp")]
    [InlineData("Patchouli-0.3.13-setup.msi", ".tmp")]
    [InlineData("patchouli-0.3.13.tar.gz", ".tmp")]
    [InlineData("patchouli-0.3.13", ".tmp")] // no extension
    [InlineData("patchouli-0.3.13.", ".tmp")] // dangling dot
    [InlineData("", ".tmp")]
    [InlineData("   ", ".tmp")]
    public void InstallerExtensionFor_maps_asset_name_to_launcher_extension(
        string? assetName, string expected)
    {
        UpdateDownloader.InstallerExtensionFor(assetName).Should().Be(expected);
    }

    [Fact]
    public void InstallerExtensionFor_null_asset_name_falls_back_to_tmp()
    {
        UpdateDownloader.InstallerExtensionFor(null).Should().Be(".tmp");
    }

    [Fact]
    public void Launcher_only_supports_windows_Inno_installers()
    {
        UpdateLauncher.KnownInstallerExtensions.Should().Equal(".exe");
        UpdateLauncher.CanLaunchInstaller(true, "setup.exe").Should().BeTrue();
        UpdateLauncher.CanLaunchInstaller(false, "setup.exe").Should().BeFalse();
        UpdateLauncher.CanLaunchInstaller(true, "update.dmg").Should().BeFalse();
        UpdateLauncher.CanLaunchInstaller(true, "update.AppImage").Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task LaunchAsync_rejects_blank_path_without_starting_a_process(string? path)
    {
        UpdateLauncher launcher = new();

        Result result = await launcher.LaunchAsync(path!);

        result.IsFailure.Should().BeTrue();
        result.ErrorMessage.Should().Contain("不存在");
    }

    [Fact]
    public async Task LaunchAsync_rejects_null_path_without_starting_a_process()
    {
        UpdateLauncher launcher = new();

        Result result = await launcher.LaunchAsync(null!);

        result.IsFailure.Should().BeTrue();
        result.ErrorMessage.Should().Contain("不存在");
    }

    [Fact]
    public async Task LaunchAsync_rejects_missing_file_without_starting_a_process()
    {
        UpdateLauncher launcher = new();
        string missing = Path.Combine(Path.GetTempPath(),
            $"patchouli-update-missing-{Guid.NewGuid():N}.tmp");

        Result result = await launcher.LaunchAsync(missing);

        result.IsFailure.Should().BeTrue();
        result.ErrorMessage.Should().Contain("不存在");
    }

    [Fact]
    public async Task LaunchAsync_propagates_cancellation_without_starting_a_process()
    {
        UpdateLauncher launcher = new();
        CancellationToken cancelled = new(true);

        await Assert.ThrowsAsync<OperationCanceledException>(() => launcher.LaunchAsync(
            Path.Combine(Path.GetTempPath(), "patchouli-update-cancelled.tmp"),
            cancelled));
    }
}
