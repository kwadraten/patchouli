using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Reading;
using Patchouli.UI;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Settings;

namespace Patchouli.Tests;

/// <summary>The reading-mode compare layout (并排对照 / 段落下方) in the 外观与显示 settings
/// section: dirty/save/discard behavior, the persistence round-trip, and workspaces picking the
/// persisted layout up at construction.</summary>
[Collection("Avalonia")]
public sealed class ReadingCompareModeSettingsTests : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    [Fact]
    public async Task Compare_mode_change_is_dirty_until_saved_and_persists()
    {
        MainWindowViewModel vm = new(settingsPath: _settings.Path);
        AppearanceSettingsViewModel section = vm.Settings.AppearanceSettings;

        section.IsDirty.Should().BeFalse();
        section.SelectedReadingCompareMode.Should().Be("并排对照", "side-by-side is the default layout");

        section.SelectedReadingCompareMode = "段落下方";
        section.IsDirty.Should().BeTrue();
        section.CanSave.Should().BeTrue();

        await section.SaveAsync();

        section.SaveState.Should().Be(SettingsSaveState.Saved);
        section.IsDirty.Should().BeFalse();
        section.SelectedReadingCompareMode.Should().Be("段落下方");

        // The in-memory AppOptions reflect the just-persisted value.
        vm.AppOptions.Ui.ReadingCompareMode.Should().Be(UiPreferences.ReadingCompareModeStacked);

        // The value also round-trips through the settings file.
        PatchouliAppSettings reloaded = PatchouliAppSettings.Load(_settings.Path);
        reloaded.Ui.ReadingCompareMode.Should().Be(UiPreferences.ReadingCompareModeStacked);
    }

    [Fact]
    public async Task Discard_reverts_compare_mode_to_the_persisted_value()
    {
        MainWindowViewModel vm = new(settingsPath: _settings.Path);
        AppearanceSettingsViewModel section = vm.Settings.AppearanceSettings;

        section.SelectedReadingCompareMode = "段落下方";
        section.IsDirty.Should().BeTrue();

        await section.DiscardAsync();

        section.IsDirty.Should().BeFalse();
        section.SelectedReadingCompareMode.Should().Be("并排对照");

        // Discard must not write anything to disk.
        PatchouliAppSettings reloaded = PatchouliAppSettings.Load(_settings.Path);
        reloaded.Ui.ReadingCompareMode.Should().Be(UiPreferences.ReadingCompareModeSideBySide);
    }

    [Fact]
    public void Unrecognized_persisted_compare_mode_reads_back_as_side_by_side()
    {
        UiPreferences.NormalizeReadingCompareMode("garbage").Should()
            .Be(UiPreferences.ReadingCompareModeSideBySide);
        UiPreferences.NormalizeReadingCompareMode(null).Should()
            .Be(UiPreferences.ReadingCompareModeSideBySide);
        UiPreferences.NormalizeReadingCompareMode(UiPreferences.ReadingCompareModeStacked).Should()
            .Be(UiPreferences.ReadingCompareModeStacked);
    }

    [Fact]
    public async Task Workspace_reads_the_persisted_compare_mode_at_construction()
    {
        MainWindowViewModel main = new(settingsPath: _settings.Path);
        (await main.SaveReadingCompareModeImmediatelyAsync(UiPreferences.ReadingCompareModeStacked))
            .Should().BeTrue();

        LibraryItemViewModel item = new(
            ItemId.New().ToString(), "对照布局测试题录", "book", "", "", "", null,
            "00000000-0000-0000-0000-000000000001", null, "source.pdf", "", 0, 0, "",
            _ => Task.CompletedTask, _ => Task.CompletedTask);
        PdfWorkspaceViewModel workspace = new(main, item);

        workspace.BookReadingCompareMode.Should().Be(ReadingCompareMode.Stacked);
    }

    public void Dispose()
    {
        _settings.Dispose();
    }
}
