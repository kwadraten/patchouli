using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Patchouli.Ocr;
using Patchouli.UI;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Settings;
using Patchouli.UI.Views;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public sealed class OcrProviderSettingsUiTests : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    [Fact]
    public async Task Ocr_combo_boxes_show_selected_engine_and_save_a_control_selection()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<int>(async () =>
        {
            string databasePath = _settings.CreateDatabasePath("ocr-combo-ui");
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
                OcrProviderSettingsViewModel settings = main.Settings.OcrProviderSettings;
                settings.SelectedDocumentEngine = OcrEngineIds.NdlLite;
                settings.IsDirty.Should().BeTrue("this covers an OCR edit made before the first catalog load");

                await main.Settings.SelectSectionAsync("ocr");
                await main.Settings.WaitForActiveSectionLoadAsync();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                settings.AvailableEngines.Should().NotBeEmpty();
                ComboBox[] selectors = window.GetVisualDescendants().OfType<ComboBox>()
                    .Where(combo => ReferenceEquals(combo.DataContext, settings) &&
                                    ReferenceEquals(combo.ItemsSource, settings.AvailableEngines))
                    .ToArray();
                selectors.Should().HaveCount(3);

                string[] expectedValues =
                [
                    OcrEngineIds.NdlLite,
                    OcrEngineIds.NdlKoten,
                    OcrEngineIds.NdlKoten
                ];
                settings.SelectedDocumentEngine.Should().Be(expectedValues[0]);
                settings.SelectedPageEngine.Should().Be(expectedValues[1]);
                settings.SelectedRegionEngine.Should().Be(expectedValues[2]);
                for (int i = 0; i < selectors.Length; i++)
                {
                    ComboBox selector = selectors[i];
                    selector.Items.Should().NotBeEmpty();
                    selector.SelectedValue.Should().Be(expectedValues[i]);
                    selector.SelectedItem.Should().BeSameAs(settings.AvailableEngines.Single(option =>
                        option.EngineId == expectedValues[i]));
                }

                settings.SelectedDocumentEngine.Should().Be(OcrEngineIds.NdlLite);
                settings.IsDirty.Should().BeTrue("loading the catalog must preserve the pre-load edit");

                OcrEngineOption target = settings.AvailableEngines.First(option =>
                    option.EngineId != settings.SelectedDocumentEngine);
                string targetEngineId = target.EngineId;
                selectors[0].SelectedItem = target;
                Dispatcher.UIThread.RunJobs();
                settings.SelectedDocumentEngine.Should().Be(targetEngineId);
                settings.IsDirty.Should().BeTrue();

                await settings.SaveAsync();
                settings.SaveState.Should().Be(SettingsSaveState.Saved);
                PatchouliAppSettings.Load(_settings.Path).OcrEngines.DocumentOcrEngine.Should().Be(targetEngineId);

                await settings.LoadAsync();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                selectors[0].SelectedValue.Should().Be(targetEngineId);
                selectors[0].SelectedItem.Should().BeSameAs(settings.AvailableEngines.Single(option =>
                    option.EngineId == targetEngineId));
                settings.SelectedDocumentEngine.Should().Be(targetEngineId);
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
