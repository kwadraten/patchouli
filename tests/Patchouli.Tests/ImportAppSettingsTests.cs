using FluentAssertions;
using Patchouli.Host.Settings;
using Patchouli.UI;

namespace Patchouli.Tests;

public sealed class ImportAppSettingsTests
{
    [Fact]
    public void New_settings_file_loads_default_import_settings()
    {
        using TemporaryAppSettingsFile file = new();

        PatchouliAppSettings settings = PatchouliAppSettings.Load(file.Path);

        settings.Import.MaxFailedPageRatio.Should().Be(0.2);
    }

    [Fact]
    public void Legacy_settings_file_without_import_section_falls_back_to_default()
    {
        string root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"patchouli-import-settings-{Guid.NewGuid():N}")).FullName;
        string path = Path.Combine(root, "settings.json");
        try
        {
            File.WriteAllText(path, """
                                    {
                                      "Ui": {
                                        "PaletteId": "legacy-palette"
                                      }
                                    }
                                    """);

            PatchouliAppSettings settings = PatchouliAppSettings.Load(path);

            settings.Import.MaxFailedPageRatio.Should().Be(0.2);
            settings.Ui.PaletteId.Should().Be("legacy-palette");
        }
        finally
        {
            TestTempFileCleanup.DeleteDirectoryWithRetry(root);
        }
    }

    [Fact]
    public async Task Update_and_save_round_trips_import_settings()
    {
        using TemporaryAppSettingsFile file = new();
        PatchouliAppSettings initial = PatchouliAppSettings.Load(file.Path);
        await using AppSettingsStore store = new(file.Path, initial, TimeSpan.FromMilliseconds(30));

        store.Update(settings => settings with
        {
            Import = settings.Import with { MaxFailedPageRatio = 0.7 }
        }, "Import");

        await Task.Delay(100);
        (await store.FlushAsync()).IsSuccess.Should().BeTrue();

        PatchouliAppSettings.Load(file.Path).Import.MaxFailedPageRatio.Should().Be(0.7);
    }

    [Fact]
    public async Task Import_field_level_save_preserves_other_sections()
    {
        using TemporaryAppSettingsFile file = new();
        PatchouliAppSettings initial = PatchouliAppSettings.Load(file.Path);
        await using AppSettingsStore store = new(file.Path, initial, TimeSpan.FromMilliseconds(30));

        store.Update(settings => settings with
        {
            Import = settings.Import with { MaxFailedPageRatio = 0.5 }
        }, "Import");

        await Task.Delay(100);
        (await store.FlushAsync()).IsSuccess.Should().BeTrue();

        PatchouliAppSettings reloaded = PatchouliAppSettings.Load(file.Path);
        reloaded.Import.MaxFailedPageRatio.Should().Be(0.5);
        reloaded.Ui.PaletteId.Should().Be(initial.Ui.PaletteId);
        reloaded.Ui.ReadingFontSize.Should().Be(initial.Ui.ReadingFontSize);
        reloaded.Ui.ShowLibraryLeftSidebar.Should().Be(initial.Ui.ShowLibraryLeftSidebar);
    }

    [Theory]
    [InlineData(1.5, 0.9)]
    [InlineData(-0.3, 0.0)]
    public void Out_of_range_ratio_is_clamped_on_load(double stored, double expected)
    {
        string root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"patchouli-import-settings-{Guid.NewGuid():N}")).FullName;
        string path = Path.Combine(root, "settings.json");
        try
        {
            File.WriteAllText(path, $$"""
                                      {
                                        "Import": {
                                          "MaxFailedPageRatio": {{stored}}
                                        }
                                      }
                                      """);

            PatchouliAppSettings.Load(path).Import.MaxFailedPageRatio.Should().Be(expected);
        }
        finally
        {
            TestTempFileCleanup.DeleteDirectoryWithRetry(root);
        }
    }
}
