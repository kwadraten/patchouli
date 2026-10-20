using FluentAssertions;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels.Dialogs;
using Patchouli.Core.Update;

namespace Patchouli.Tests;

public sealed class UpdateInstallabilityTests
{
    [Fact]
    public void Built_in_update_is_limited_to_a_confirmed_windows_inno_install()
    {
        UpdateInstallability.IsSupportedInstall(true, false,
            true).Should().BeTrue();
        UpdateInstallability.IsSupportedInstall(true, true,
            true).Should().BeFalse();
        UpdateInstallability.IsSupportedInstall(true, false,
            false).Should().BeFalse();
        UpdateInstallability.IsSupportedInstall(false, false,
            true).Should().BeFalse();
        string installPath = Path.Combine(Path.GetTempPath(), "Patchouli.NET", "");
        UpdateInstallability.InstallPathsMatch(installPath + Path.DirectorySeparatorChar,
            installPath).Should().BeTrue();
    }

    [Fact]
    public void Recognized_external_package_manager_roots_are_excluded_by_path_boundary()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] managedRoots =
        [
            Path.Combine(userProfile, "scoop", "apps"),
            Path.Combine(localAppData, "Microsoft", "WinGet", "Packages"),
            Path.Combine(localAppData, "Microsoft", "WinGet", "Links"),
            Path.Combine(commonAppData, "chocolatey", "lib"),
            Path.Combine(programFiles, "WindowsApps")
        ];

        foreach (string root in managedRoots.Where(static path => !string.IsNullOrWhiteSpace(path)))
        {
            UpdateInstallability.IsKnownExternalManagerPath(Path.Combine(root, "Patchouli"))
                .Should().BeTrue(root);
            UpdateInstallability.IsKnownExternalManagerPath(root.TrimEnd(Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) + "-managed" + Path.DirectorySeparatorChar + "Patchouli")
                .Should().BeFalse(root);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Unsupported_update_prompt_exposes_release_link_without_download(bool canDownload)
    {
        ReleaseInfo release = new("v1.2.3", UpdateVersion.Parse("1.2.3"), "release", "notes",
            false, false, null, []);
        ReleaseAssetInfo asset = new("Patchouli.NET-1.2.3-linux-x64.AppImage", "https://example.test/a", 10);
        UpdateAvailableDialogViewModel viewModel = new(UpdateVersion.Parse("1.2.2"), release, asset,
            canDownload);
        string? opened = null;
        viewModel.RequestOpenReleasePage = url => opened = url;

        viewModel.CanDownload.Should().Be(canDownload);
        viewModel.ReleasePageUrl.Should().Be("https://github.com/kwadraten/patchouli/releases/tag/v1.2.3");
        if (!canDownload)
        {
            viewModel.InstallGuidanceText.Should().Contain("不支持应用内自动安装");
            viewModel.AssetText.Should().NotContain(asset.Name);
            viewModel.OpenReleasePageCommand.Execute(null);
            opened.Should().Be(viewModel.ReleasePageUrl);
        }
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public async Task Download_flow_requires_supported_install_and_user_confirmation(
        bool canDownload, bool userRequested, bool expectedStart)
    {
        int downloadCount = 0;
        bool prompted = false;

        bool started = await UpdateFlowService.ContinueDownloadAsync(canDownload,
            () =>
            {
                prompted = true;
                return Task.FromResult(userRequested);
            },
            () =>
            {
                downloadCount++;
                return Task.CompletedTask;
            });

        prompted.Should().BeTrue();
        started.Should().Be(expectedStart);
        downloadCount.Should().Be(expectedStart ? 1 : 0);
    }
}
