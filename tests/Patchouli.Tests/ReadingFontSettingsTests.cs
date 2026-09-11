using System.Collections.Generic;
using System.Threading;
using Avalonia.Headless;
using FluentAssertions;
using Patchouli.UI;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Settings;

namespace Patchouli.Tests;

/// <summary>The reading-mode font family and size additions to the 外观与显示 settings section:
/// dirty/save/discard behavior, range clamping, and the font family picker catalog.</summary>
[Collection("Avalonia")]
public sealed class ReadingFontSettingsTests : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    [Fact]
    public async Task Reading_font_changes_are_dirty_until_saved_and_persist()
    {
        MainWindowViewModel vm = new(settingsPath: _settings.Path);
        AppearanceSettingsViewModel section = vm.Settings.AppearanceSettings;

        section.IsDirty.Should().BeFalse();
        section.SelectedReadingFontFamily.Should().BeEmpty("the default is the system font");
        section.ReadingFontSize.Should().Be(UiPreferences.DefaultReadingFontSize);

        section.SelectedReadingFontFamily = "Test Reading Font";
        section.ReadingFontSize = 18;
        section.IsDirty.Should().BeTrue();
        section.CanSave.Should().BeTrue();

        await section.SaveAsync();

        section.SaveState.Should().Be(SettingsSaveState.Saved);
        section.IsDirty.Should().BeFalse();
        section.SelectedReadingFontFamily.Should().Be("Test Reading Font");
        section.ReadingFontSize.Should().Be(18);

        // The in-memory AppOptions reflect the just-persisted values.
        vm.AppOptions.Ui.ReadingFontFamily.Should().Be("Test Reading Font");
        vm.AppOptions.Ui.ReadingFontSize.Should().Be(18);

        // The values also round-trip through the settings file.
        PatchouliAppSettings reloaded = PatchouliAppSettings.Load(_settings.Path);
        reloaded.Ui.ReadingFontFamily.Should().Be("Test Reading Font");
        reloaded.Ui.ReadingFontSize.Should().Be(18);
    }

    [Fact]
    public async Task Discard_reverts_reading_font_and_size_to_the_persisted_values()
    {
        MainWindowViewModel vm = new(settingsPath: _settings.Path);
        AppearanceSettingsViewModel section = vm.Settings.AppearanceSettings;
        string persistedFont = section.SelectedReadingFontFamily;
        double persistedSize = section.ReadingFontSize;

        section.SelectedReadingFontFamily = "Discarded Font";
        section.ReadingFontSize = 24;
        section.IsDirty.Should().BeTrue();

        await section.DiscardAsync();

        section.IsDirty.Should().BeFalse();
        section.SelectedReadingFontFamily.Should().Be(persistedFont);
        section.ReadingFontSize.Should().Be(persistedSize);

        // Discard must not write anything to disk.
        PatchouliAppSettings reloaded = PatchouliAppSettings.Load(_settings.Path);
        reloaded.Ui.ReadingFontFamily.Should().Be(persistedFont);
        reloaded.Ui.ReadingFontSize.Should().Be(persistedSize);
    }

    [Fact]
    public void Reading_font_size_is_clamped_to_the_supported_range()
    {
        MainWindowViewModel vm = new(settingsPath: _settings.Path);
        AppearanceSettingsViewModel section = vm.Settings.AppearanceSettings;

        section.ReadingFontSize = 4;
        section.ReadingFontSize.Should().Be(10);

        section.ReadingFontSize = 200;
        section.ReadingFontSize.Should().Be(28);

        section.ReadingFontSize = 20;
        section.ReadingFontSize.Should().Be(20);
    }

    [Fact]
    public async Task Reading_font_family_list_starts_with_system_default_and_is_non_empty()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        IReadOnlyList<string> families = await session.Dispatch(() =>
        {
            MainWindowViewModel vm = new(settingsPath: _settings.Path);
            return vm.Settings.AppearanceSettings.ReadingFontFamilies;
        }, CancellationToken.None);

        families.Should().NotBeEmpty();
        families[0].Should().Be("系统默认");
        families.Should().OnlyHaveUniqueItems("system font family names must be de-duplicated");
    }

    public void Dispose()
    {
        _settings.Dispose();
    }
}
