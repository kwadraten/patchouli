using FluentAssertions;
using Patchouli.UI;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Settings;

namespace Patchouli.Tests;

/// <summary>The 导入 settings section: default value, dirty/save/discard behavior, and percent
/// clamping for the whole-book failed-page ratio threshold.</summary>
[Collection("Avalonia")]
public sealed class ImportSettingsTests : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    [Fact]
    public void Import_section_defaults_to_the_persisted_failed_page_ratio_percent()
    {
        MainWindowViewModel vm = new(settingsPath: _settings.Path);
        ImportSettingsViewModel section = vm.Settings.ImportSettings;

        section.IsDirty.Should().BeFalse();
        section.MaxFailedPageRatioPercent.Should().Be(20, "the default failed page ratio is 0.2");
    }

    [Fact]
    public async Task Failed_page_ratio_changes_are_dirty_until_saved_and_persist()
    {
        MainWindowViewModel vm = new(settingsPath: _settings.Path);
        ImportSettingsViewModel section = vm.Settings.ImportSettings;

        section.MaxFailedPageRatioPercent = 45;
        section.IsDirty.Should().BeTrue();
        section.CanSave.Should().BeTrue();

        await section.SaveAsync();

        section.SaveState.Should().Be(SettingsSaveState.Saved);
        section.IsDirty.Should().BeFalse();

        // The in-memory AppOptions reflect the just-persisted value.
        vm.AppOptions.Import.MaxFailedPageRatio.Should().Be(0.45);

        // The value also round-trips through the settings file.
        PatchouliAppSettings reloaded = PatchouliAppSettings.Load(_settings.Path);
        reloaded.Import.MaxFailedPageRatio.Should().Be(0.45);
    }

    [Fact]
    public void Failed_page_ratio_percent_is_clamped_to_the_supported_range()
    {
        MainWindowViewModel vm = new(settingsPath: _settings.Path);
        ImportSettingsViewModel section = vm.Settings.ImportSettings;

        section.MaxFailedPageRatioPercent = -5;
        section.MaxFailedPageRatioPercent.Should().Be(0);

        section.MaxFailedPageRatioPercent = 150;
        section.MaxFailedPageRatioPercent.Should().Be(90);

        section.MaxFailedPageRatioPercent = 30;
        section.MaxFailedPageRatioPercent.Should().Be(30);
    }

    [Fact]
    public async Task Discard_reverts_failed_page_ratio_to_the_persisted_value()
    {
        MainWindowViewModel vm = new(settingsPath: _settings.Path);
        ImportSettingsViewModel section = vm.Settings.ImportSettings;
        double persistedPercent = section.MaxFailedPageRatioPercent;

        section.MaxFailedPageRatioPercent = 60;
        section.IsDirty.Should().BeTrue();

        await section.DiscardAsync();

        section.IsDirty.Should().BeFalse();
        section.MaxFailedPageRatioPercent.Should().Be(persistedPercent);

        // Discard must not write anything to disk.
        PatchouliAppSettings reloaded = PatchouliAppSettings.Load(_settings.Path);
        reloaded.Import.MaxFailedPageRatio.Should().Be(0.2);
    }

    public void Dispose()
    {
        _settings.Dispose();
    }
}
