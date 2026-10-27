using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Patchouli.Host;
using Patchouli.Host.Composition;
using Patchouli.Ocr;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Settings;
using Patchouli.UI.Views;

namespace Patchouli.Tests;

/// <summary>
///     Headless coverage of the new "OCR 预设编排" surface: four per-scope preset pickers (document,
///     page, region, working), the inline composition grid, and the persisted per-library scope
///     selection. Component choices come from the extension registry — never OcrAdapters.
/// </summary>
[Collection("Avalonia")]
public sealed class OcrProviderSettingsUiTests : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    [Fact]
    public async Task Ocr_tab_shows_scope_pickers_and_composition_grid_and_saves_selections()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<int>(async () =>
        {
            string databasePath = _settings.CreateDatabasePath("ocr-compose-ui");
            await using MainWindowViewModel main = new(settingsPath: _settings.Path)
            {
                RuntimeDatabasePath = databasePath
            };
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();

            SettingsPage page = new() { DataContext = main.Settings };
            Window window = new() { Width = 1280, Height = 900, Content = page };
            window.Show();
            try
            {
                await main.Settings.SelectSectionAsync("ocr");
                await main.Settings.WaitForActiveSectionLoadAsync();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                OcrProviderSettingsViewModel settings = main.Settings.OcrProviderSettings;
                OcrPresetScopePickerViewModel[] pickers =
                [
                    settings.DocumentPresetPicker, settings.PagePresetPicker,
                    settings.RegionPresetPicker
                ];
                foreach (OcrPresetScopePickerViewModel picker in pickers)
                {
                    picker.Options.Should().ContainSingle(option =>
                        ReferenceEquals(option, OcrPresetOption.Automatic));
                    window.GetVisualDescendants().OfType<ComboBox>().Should().Contain(combo =>
                        ReferenceEquals(combo.ItemsSource, picker.Options));
                }

                // The inline composition table — a real DataGrid, not an ItemsControl stand-in.
                DataGrid grid = window.GetVisualDescendants().OfType<DataGrid>().Single(dataGrid =>
                    ReferenceEquals(dataGrid.ItemsSource, settings.PresetRows));

                // Add + save one row: the first save creates the preset and its initial immutable
                // version, then the scope pickers offer it as a real choice. Managed defaults
                // for registered engines are already materialized rows; the draft is the new one.
                await settings.AddPresetRowCommand.ExecuteAsync();
                OcrPresetCompositionRowViewModel row = settings.PresetRows.Should()
                    .ContainSingle(candidate => candidate.IsNew).Subject;
                row.IsNew.Should().BeTrue();
                row.Name = "扫描文献预设";

                await settings.SavePresetRowAsync(row);

                row.IsNew.Should().BeFalse();
                row.HasError.Should().BeFalse(row.StatusText);
                row.IsDirty.Should().BeFalse();
                grid.ItemsSource.Should().BeSameAs(settings.PresetRows);

                HostServices services = await main.ServicesAsync();
                OcrPreset persisted = (await services.OcrPresets.ListPresetsAsync(false)).Value
                    .Should().ContainSingle(preset => preset.Name == "扫描文献预设").Subject;
                persisted.Name.Should().Be("扫描文献预设");
                persisted.CurrentVersionId.Should().NotBeNull();

                settings.RegionPresetPicker.Options.Should().Contain(option =>
                    option.PresetId == persisted.PresetId.ToString());
                settings.RegionPresetPicker.SelectedOption = settings.RegionPresetPicker.Options
                    .Single(option => option.PresetId == persisted.PresetId.ToString());
                settings.IsDirty.Should().BeTrue();

                await settings.SaveAsync();
                settings.SaveState.Should().Be(SettingsSaveState.Saved);

                OcrPresetSelectionService selectionService = new(
                    new OcrPresetSelectionStore(_settings.Path), services.OcrPresets,
                    services.Library);
                (await selectionService.ReadStoredSelectionsAsync()).Value.RegionPresetId.Should()
                    .Be(persisted.PresetId.ToString());
            }
            finally
            {
                window.Close();
            }

            return 0;
        }, CancellationToken.None);
    }

    public void Dispose()
    {
        _settings.Dispose();
    }
}
