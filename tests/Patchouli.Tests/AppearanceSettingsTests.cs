using FluentAssertions;
using Patchouli.UI;
using Patchouli.UI.Themes;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Settings;

namespace Patchouli.Tests;

/// <summary>The 外观与显示 settings section: palette catalog integrity, persistence, and section
/// dirty/save/discard behavior.</summary>
public sealed class AppearanceSettingsTests : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    [Fact]
    public void Palette_id_round_trips_through_the_settings_file()
    {
        PatchouliAppSettings loaded = PatchouliAppSettings.Load(_settings.Path);
        loaded.Ui.PaletteId.Should().Be(UiColorPalettes.DefaultPaletteId);

        PatchouliAppSettings updated = loaded with { Ui = loaded.Ui with { PaletteId = "radix-teal" } };
        updated.Save(_settings.Path).IsSuccess.Should().BeTrue();

        PatchouliAppSettings reloaded = PatchouliAppSettings.Load(_settings.Path);
        reloaded.Ui.PaletteId.Should().Be("radix-teal");
    }

    [Fact]
    public void Unknown_or_missing_palette_ids_resolve_to_the_default_palette()
    {
        UiColorPalettes.ResolveId(null).Should().Be(UiColorPalettes.DefaultPaletteId);
        UiColorPalettes.ResolveId("  ").Should().Be(UiColorPalettes.DefaultPaletteId);
        UiColorPalettes.ResolveId("radix-does-not-exist").Should().Be(UiColorPalettes.DefaultPaletteId);
        UiColorPalettes.Resolve("radix-violet").Id.Should().Be("radix-violet");
    }

    [Fact]
    public void Every_palette_provides_all_semantic_colors_as_valid_hex()
    {
        UiColorPalettes.All.Should().NotBeEmpty();
        UiColorPalettes.All.Select(palette => palette.Id).Should().OnlyHaveUniqueItems();
        foreach (UiColorPalette palette in UiColorPalettes.All)
        {
            palette.Colors.Keys.Should().BeEquivalentTo(UiColorPalettes.SemanticColorKeys,
                $"palette '{palette.Id}' must define every semantic color");
            foreach (string hex in palette.Colors.Values)
            {
                Action parse = () => _ = Avalonia.Media.Color.Parse(hex);
                parse.Should().NotThrow($"palette '{palette.Id}' color '{hex}' must be valid");
            }
        }
    }

    [Fact]
    public async Task Appearance_section_save_persists_the_palette_choice()
    {
        MainWindowViewModel vm = new(settingsPath: _settings.Path);
        AppearanceSettingsViewModel section = vm.Settings.AppearanceSettings;

        section.IsDirty.Should().BeFalse();
        section.SelectedPalette.PaletteId.Should().Be(UiColorPalettes.DefaultPaletteId);

        PaletteOption target = section.Palettes.First(option => option.PaletteId == "radix-teal");
        section.SelectedPalette = target;
        section.IsDirty.Should().BeTrue();
        section.CanSave.Should().BeTrue();

        await section.SaveAsync();

        section.SaveState.Should().Be(SettingsSaveState.Saved);
        section.IsDirty.Should().BeFalse();
        PatchouliAppSettings.Load(_settings.Path).Ui.PaletteId.Should().Be("radix-teal");
    }

    [Fact]
    public async Task Appearance_section_discard_reverts_the_selection()
    {
        MainWindowViewModel vm = new(settingsPath: _settings.Path);
        AppearanceSettingsViewModel section = vm.Settings.AppearanceSettings;

        PaletteOption original = section.SelectedPalette;
        section.SelectedPalette = section.Palettes.First(option => option.PaletteId == "radix-slate");
        section.IsDirty.Should().BeTrue();

        await section.DiscardAsync();

        section.IsDirty.Should().BeFalse();
        section.SelectedPalette.PaletteId.Should().Be(original.PaletteId);
        PatchouliAppSettings.Load(_settings.Path).Ui.PaletteId.Should().Be(original.PaletteId);
    }

    public void Dispose()
    {
        _settings.Dispose();
    }
}
