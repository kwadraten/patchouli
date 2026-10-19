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

    [Fact]
    public void Theme_mode_round_trips_through_the_settings_file()
    {
        PatchouliAppSettings loaded = PatchouliAppSettings.Load(_settings.Path);
        loaded.Ui.ThemeMode.Should().Be(UiPreferences.ThemeModeSystem);

        PatchouliAppSettings updated = loaded with { Ui = loaded.Ui with { ThemeMode = UiPreferences.ThemeModeDark } };
        updated.Save(_settings.Path).IsSuccess.Should().BeTrue();

        PatchouliAppSettings reloaded = PatchouliAppSettings.Load(_settings.Path);
        reloaded.Ui.ThemeMode.Should().Be(UiPreferences.ThemeModeDark);
    }

    [Fact]
    public void Theme_mode_defaults_to_system_and_normalizes_unknown_values()
    {
        UiPreferences.NormalizeThemeMode(null).Should().Be(UiPreferences.ThemeModeSystem);
        UiPreferences.NormalizeThemeMode("").Should().Be(UiPreferences.ThemeModeSystem);
        UiPreferences.NormalizeThemeMode("invalid_mode").Should().Be(UiPreferences.ThemeModeSystem);
        UiPreferences.NormalizeThemeMode("LIGHT").Should().Be(UiPreferences.ThemeModeLight);
        UiPreferences.NormalizeThemeMode("Dark").Should().Be(UiPreferences.ThemeModeDark);
        UiPreferences.NormalizeThemeMode("sYsTeM").Should().Be(UiPreferences.ThemeModeSystem);
    }

    [Fact]
    public void InvertHexColor_correctly_inverts_rgb_and_preserves_alpha()
    {
        UiColorPalettes.InvertHexColor("#000000").Should().Be("#FFFFFF");
        UiColorPalettes.InvertHexColor("#FFFFFF").Should().Be("#000000");
        UiColorPalettes.InvertHexColor("#102030").Should().Be("#EFDFCF");

        // Alpha preservation (8-digit hex)
        string semiTransparent = "#80102030";
        string inverted = UiColorPalettes.InvertHexColor(semiTransparent);
        inverted.Should().Be("#80EFDFCF");

        // 3-digit hex
        UiColorPalettes.InvertHexColor("#000").Should().Be("#FFFFFF");
        UiColorPalettes.InvertHexColor("#FFF").Should().Be("#000000");

        // Double inversion is identity
        UiColorPalettes.InvertHexColor(UiColorPalettes.InvertHexColor("#1A2B3C")).Should().Be("#1A2B3C");
        UiColorPalettes.InvertHexColor(UiColorPalettes.InvertHexColor("#7F1A2B3C")).Should().Be("#7F1A2B3C");
    }

    [Fact]
    public void Palette_with_explicit_dual_mode_uses_provided_colors_without_inversion()
    {
        Dictionary<string, string> light = new() { ["SurfaceColor"] = "#FFFFFF", ["OnSurfaceColor"] = "#000000" };
        Dictionary<string, string> dark = new() { ["SurfaceColor"] = "#121212", ["OnSurfaceColor"] = "#E0E0E0" };

        UiColorPalette palette = new("test-dual", "Test Dual", light, dark);
        palette.HasExplicitLight.Should().BeTrue();
        palette.HasExplicitDark.Should().BeTrue();

        palette.GetColors(false)["SurfaceColor"].Should().Be("#FFFFFF");
        palette.GetColors(true)["SurfaceColor"].Should().Be("#121212");
        palette.GetColors(true)["OnSurfaceColor"].Should().Be("#E0E0E0");
    }

    [Fact]
    public void Palette_with_only_light_colors_generates_dark_mode_via_inversion_including_unknown_keys()
    {
        Dictionary<string, string> light = new()
        {
            ["SurfaceColor"] = "#FFFFFF",
            ["CustomSpecialBadgeColor"] = "#80FF0000"
        };

        UiColorPalette palette = new("test-light-only", "Test Light Only", light);
        palette.HasExplicitLight.Should().BeTrue();
        palette.HasExplicitDark.Should().BeFalse();

        // Dark mode is generated by algorithm inversion
        palette.GetColors(false)["SurfaceColor"].Should().Be("#FFFFFF");
        palette.GetColors(true)["SurfaceColor"].Should().Be("#000000");

        // Unknown / custom key is processed with alpha preserved
        palette.GetColors(true)["CustomSpecialBadgeColor"].Should().Be("#8000FFFF");
    }

    [Fact]
    public void Palette_with_only_dark_colors_generates_light_mode_via_inversion()
    {
        Dictionary<string, string> dark = new()
        {
            ["SurfaceColor"] = "#101010",
            ["AccentColor"] = "#00FF00"
        };

        UiColorPalette palette = new("test-dark-only", "Test Dark Only", null, dark);
        palette.HasExplicitLight.Should().BeFalse();
        palette.HasExplicitDark.Should().BeTrue();

        palette.GetColors(true)["SurfaceColor"].Should().Be("#101010");
        palette.GetColors(false)["SurfaceColor"].Should().Be("#EFEFEF");
        palette.GetColors(false)["AccentColor"].Should().Be("#FF00FF");
    }

    [Fact]
    public void Palette_color_access_does_not_drift_across_repeated_switches()
    {
        Dictionary<string, string> light = new() { ["SurfaceColor"] = "#FAFAFA" };
        UiColorPalette palette = new("test-drift", "Test Drift", light);

        for (int i = 0; i < 20; i++)
        {
            palette.GetColors(false)["SurfaceColor"].Should().Be("#FAFAFA");
            palette.GetColors(true)["SurfaceColor"].Should().Be("#050505");
        }
    }

    [Fact]
    public void Every_palette_provides_all_semantic_colors_in_both_light_and_dark_modes()
    {
        UiColorPalettes.All.Should().NotBeEmpty();
        UiColorPalettes.All.Select(palette => palette.Id).Should().OnlyHaveUniqueItems();
        foreach (UiColorPalette palette in UiColorPalettes.All)
        {
            palette.LightColors.Keys.Should().BeEquivalentTo(UiColorPalettes.SemanticColorKeys,
                $"palette '{palette.Id}' must define every semantic color in light mode");
            palette.DarkColors.Keys.Should().BeEquivalentTo(UiColorPalettes.SemanticColorKeys,
                $"palette '{palette.Id}' must define every semantic color in dark mode");

            foreach (string hex in palette.LightColors.Values)
            {
                Action parse = () => _ = Avalonia.Media.Color.Parse(hex);
                parse.Should().NotThrow($"palette '{palette.Id}' light color '{hex}' must be valid");
            }

            foreach (string hex in palette.DarkColors.Values)
            {
                Action parse = () => _ = Avalonia.Media.Color.Parse(hex);
                parse.Should().NotThrow($"palette '{palette.Id}' dark color '{hex}' must be valid");
            }
        }
    }

    [Fact]
    public void Bright_accent_scales_have_dark_text_on_primary_in_both_modes()
    {
        string[] brightScales = ["radix-amber", "radix-yellow", "radix-lime", "radix-mint", "radix-sky"];
        foreach (string id in brightScales)
        {
            UiColorPalette palette = UiColorPalettes.Resolve(id);
            // In light mode and dark mode, OnPrimaryColor should be dark text
            Avalonia.Media.Color lightOnPrimary = Avalonia.Media.Color.Parse(palette.LightColors["OnPrimaryColor"]);
            Avalonia.Media.Color darkOnPrimary = Avalonia.Media.Color.Parse(palette.DarkColors["OnPrimaryColor"]);

            // Both light and dark OnPrimaryColor for bright accents should have low brightness (dark text)
            (lightOnPrimary.R + lightOnPrimary.G + lightOnPrimary.B).Should().BeLessThan(300,
                $"bright scale '{id}' light OnPrimary should be dark text");
            (darkOnPrimary.R + darkOnPrimary.G + darkOnPrimary.B).Should().BeLessThan(300,
                $"bright scale '{id}' dark OnPrimary should be dark text");
        }
    }

    [Fact]
    public void Every_builtin_mode_has_readable_button_and_status_text()
    {
        static double Luminance(string hex)
        {
            Avalonia.Media.Color color = Avalonia.Media.Color.Parse(hex);

            static double Linear(byte value)
            {
                double channel = value / 255.0;
                return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }

        foreach (UiColorPalette palette in UiColorPalettes.All)
        {
            foreach (IReadOnlyDictionary<string, string> colors in new[] { palette.LightColors, palette.DarkColors })
            {
                foreach ((string foregroundKey, string backgroundKey) in new[]
                         {
                             ("OnPrimaryColor", "PrimaryContainerColor"),
                             ("InverseOnSurfaceColor", "InverseSurfaceColor")
                         })
                {
                    double foreground = Luminance(colors[foregroundKey]);
                    double background = Luminance(colors[backgroundKey]);
                    double contrast = (Math.Max(foreground, background) + 0.05) /
                                      (Math.Min(foreground, background) + 0.05);
                    contrast.Should().BeGreaterThanOrEqualTo(4.5,
                        $"{palette.Id} {foregroundKey} needs readable text in both modes");
                }
            }
        }
    }

    [Fact]
    public void Dark_background_layers_use_the_palettes_own_primary_scale_and_status_uses_primary()
    {
        foreach (UiColorPalette palette in UiColorPalettes.All)
        {
            string scale = palette.Id["radix-".Length..];
            string design =
                File.ReadAllText(TestPaths.FromRepositoryRoot(".agents", "palettes", $"radix_{scale}", "DESIGN.md"));
            string darkDefinition = System.Text.RegularExpressions.Regex.Match(design,
                @"(?ms)^  dark:\r?\n(?<steps>.*?)^---").Groups["steps"].Value;
            Dictionary<int, string> steps = System.Text.RegularExpressions.Regex.Matches(darkDefinition,
                    @"-(?<step>\d+): '(?<hex>#[0-9a-fA-F]{6})'")
                .ToDictionary(match => int.Parse(match.Groups["step"].Value), match => match.Groups["hex"].Value);
            string[] layers =
            [
                "SurfaceColor", "SurfaceContainerLowColor", "SurfaceContainerColor",
                "SurfaceContainerHighColor", "SurfaceContainerHighestColor"
            ];
            for (int layer = 0; layer < layers.Length; layer++)
            {
                palette.DarkColors[layers[layer]].Should().Be(steps[layer + 1],
                    $"{palette.Id} must use its own dark accent backgrounds");
            }

            palette.DarkColors["SurfaceContainerLowestColor"].Should().Be(steps[1]);
            palette.DarkColors["SurfaceVariantColor"].Should().Be(steps[3]);
            palette.DarkColors["InverseSurfaceColor"].Should().Be(palette.DarkColors["PrimaryColor"]);
        }
    }

    [Fact]
    public async Task Appearance_section_save_and_discard_manage_theme_mode()
    {
        MainWindowViewModel vm = new(settingsPath: _settings.Path);
        AppearanceSettingsViewModel section = vm.Settings.AppearanceSettings;

        section.SelectedThemeMode.Mode.Should().Be(UiPreferences.ThemeModeSystem);
        section.IsDirty.Should().BeFalse();

        // Switch to Dark
        ThemeModeOption darkOption = section.AvailableThemeModes.First(m => m.Mode == UiPreferences.ThemeModeDark);
        section.SelectedThemeMode = darkOption;
        section.IsDirty.Should().BeTrue();
        section.CanSave.Should().BeTrue();

        await section.SaveAsync();
        section.IsDirty.Should().BeFalse();
        PatchouliAppSettings.Load(_settings.Path).Ui.ThemeMode.Should().Be(UiPreferences.ThemeModeDark);

        // Switch to Light and Discard
        ThemeModeOption lightOption = section.AvailableThemeModes.First(m => m.Mode == UiPreferences.ThemeModeLight);
        section.SelectedThemeMode = lightOption;
        section.IsDirty.Should().BeTrue();

        await section.DiscardAsync();
        section.IsDirty.Should().BeFalse();
        section.SelectedThemeMode.Mode.Should().Be(UiPreferences.ThemeModeDark);
        PatchouliAppSettings.Load(_settings.Path).Ui.ThemeMode.Should().Be(UiPreferences.ThemeModeDark);
    }

    [Fact]
    public void ThemePaletteApplier_resolves_effective_is_dark_correctly()
    {
        ThemePaletteApplier.ResolveEffectiveIsDark(null, UiPreferences.ThemeModeLight).Should().BeFalse();
        ThemePaletteApplier.ResolveEffectiveIsDark(null, UiPreferences.ThemeModeDark).Should().BeTrue();
        ThemePaletteApplier.ResolveEffectiveIsDark(null, UiPreferences.ThemeModeSystem).Should().BeFalse();
    }

    [Fact]
    public void Default_palette_preserves_auxiliary_light_color_values()
    {
        UiColorPalette defaultPalette = UiColorPalettes.Resolve(UiColorPalettes.DefaultPaletteId);
        defaultPalette.LightColors["SurfaceContainerLowestColor"].Should().Be("#FFFFFF");
        defaultPalette.LightColors["SurfaceVariantColor"].Should().Be("#EFEDED");
        defaultPalette.LightColors["OnErrorContainerColor"].Should().Be("#BA1A1A");
    }

    [Fact]
    public void All_built_in_palettes_include_auxiliary_colors_in_both_modes()
    {
        foreach (UiColorPalette palette in UiColorPalettes.All)
        {
            palette.LightColors.Should().ContainKey("SurfaceContainerLowestColor");
            palette.LightColors.Should().ContainKey("SurfaceVariantColor");
            palette.LightColors.Should().ContainKey("OnErrorContainerColor");
            palette.DarkColors.Should().ContainKey("SurfaceContainerLowestColor");
            palette.DarkColors.Should().ContainKey("SurfaceVariantColor");
            palette.DarkColors.Should().ContainKey("OnErrorContainerColor");
        }
    }

    public void Dispose()
    {
        _settings.Dispose();
    }
}
