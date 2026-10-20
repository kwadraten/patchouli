using System.Runtime.InteropServices;
using FluentAssertions;
using Patchouli.Core.Update;

namespace Patchouli.Tests;

/// <summary>
/// Issue #15: installer asset selection per OS/architecture. Windows must pick
/// the formal setup (never the -smoke diagnostic variant), macOS arm64 the
/// .dmg, Linux x64 the .AppImage; a release without a matching asset must fail
/// explicitly instead of handing out a wrong file.
/// </summary>
public sealed class UpdateAssetSelectorTests
{
    private static ReleaseInfo MakeRelease(string tag, params ReleaseAssetInfo[] assets)
    {
        return new ReleaseInfo(tag, UpdateVersion.Parse(tag), "title", "body",
            IsDraft: false, IsPrerelease: false, PublishedAt: null, assets);
    }

    private static ReleaseAssetInfo Asset(string name)
    {
        return new ReleaseAssetInfo(name, $"https://example.test/{name}", 1024);
    }

    [Fact]
    public void Windows_selects_formal_setup_and_excludes_smoke_variant()
    {
        ReleaseAssetInfo formal = Asset("Patchouli.NET-0.3.13-win-x64-setup.exe");
        ReleaseAssetInfo smoke = Asset("Patchouli.NET-0.3.13-win-x64-smoke-setup.exe");
        ReleaseInfo release = MakeRelease("v0.3.13", smoke, formal);

        var result = UpdateAssetSelector.SelectAsset(release, OSPlatform.Windows, System.Runtime.InteropServices.Architecture.X64);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(formal);
    }

    [Fact]
    public void Windows_rejects_release_where_only_a_smoke_candidate_matches()
    {
        // Ends with the setup suffix but carries the smoke marker: selecting it
        // would ship the diagnostic build to users.
        ReleaseInfo release = MakeRelease("v0.3.13",
            Asset("Patchouli-0.3.13-smoke-win-x64-setup.exe"));

        var result = UpdateAssetSelector.SelectAsset(release, OSPlatform.Windows, System.Runtime.InteropServices.Architecture.X64);

        result.IsFailure.Should().BeTrue();
        result.ErrorMessage.Should().Contain("安装包");
    }

    [Fact]
    public void MacOS_arm64_selects_dmg()
    {
        ReleaseAssetInfo dmg = Asset("Patchouli.NET-0.3.13-osx-arm64.dmg");
        ReleaseInfo release = MakeRelease("v0.3.13",
            Asset("Patchouli.NET-0.3.13-win-x64-setup.exe"), dmg);

        var result = UpdateAssetSelector.SelectAsset(release, OSPlatform.OSX, System.Runtime.InteropServices.Architecture.Arm64);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(dmg);
    }

    [Fact]
    public void MacOS_x64_has_no_supported_asset()
    {
        ReleaseInfo release = MakeRelease("v0.3.13",
            Asset("Patchouli.NET-0.3.13-osx-arm64.dmg"));

        var result = UpdateAssetSelector.SelectAsset(release, OSPlatform.OSX, System.Runtime.InteropServices.Architecture.X64);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Linux_x64_selects_appimage()
    {
        ReleaseAssetInfo appImage = Asset("Patchouli.NET-0.3.13-linux-x64.AppImage");
        ReleaseInfo release = MakeRelease("v0.3.13",
            Asset("Patchouli.NET-0.3.13-win-x64-setup.exe"), appImage);

        var result = UpdateAssetSelector.SelectAsset(release, OSPlatform.Linux, System.Runtime.InteropServices.Architecture.X64);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(appImage);
    }

    [Fact]
    public void Release_without_assets_fails_explicitly()
    {
        ReleaseInfo release = MakeRelease("v0.3.13");

        var result = UpdateAssetSelector.SelectAsset(release, OSPlatform.Linux, System.Runtime.InteropServices.Architecture.X64);

        result.IsFailure.Should().BeTrue();
        result.ErrorMessage.Should().Contain("-linux-x64.AppImage");
    }

    [Fact]
    public void Prefers_asset_whose_name_contains_the_release_version()
    {
        ReleaseAssetInfo older = Asset("Patchouli.NET-0.3.12-win-x64-setup.exe");
        ReleaseAssetInfo current = Asset("Patchouli.NET-0.3.13-win-x64-setup.exe");
        ReleaseInfo release = MakeRelease("v0.3.13", older, current);

        var result = UpdateAssetSelector.SelectAsset(release, OSPlatform.Windows, System.Runtime.InteropServices.Architecture.X64);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(current);
    }

    [Fact]
    public void Null_release_is_rejected()
    {
        FluentActions.Invoking(() =>
                UpdateAssetSelector.SelectAsset(null!, OSPlatform.Linux, System.Runtime.InteropServices.Architecture.X64))
            .Should().Throw<ArgumentNullException>();
    }
}
