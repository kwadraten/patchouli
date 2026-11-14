using Avalonia.Headless;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Patchouli.UI.ViewModels;
using Patchouli.UI.Views;

namespace Patchouli.Tests;

[Collection("RetainedTabUI")]
public sealed class LayoutRecognitionSettingsTests(AvaloniaSharedSessionFixture headless)
{
    [Fact]
    public async Task Models_have_an_independent_catalog_and_ai_has_no_ocr_card()
    {
        await headless.Session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            string database = settings.CreateDatabasePath("layout-settings");
            await using MainWindowViewModel main = new(settingsPath: settings.Path) { RuntimeDatabasePath = database };
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            SettingsPage page = new() { DataContext = main.Settings };
            Window window = new() { Width = 1280, Height = 900, Content = page };
            window.Show();
            try
            {
                await main.Settings.SelectSectionAsync("ocr_components");
                Pump(window);
                main.Settings.LayoutModelCatalog.Components.Select(row => row.ExtensionId).Should()
                    .Contain(["layout.pp-doclayout-v3", "layout.doclayout-yolo"]);
                window.GetVisualDescendants().OfType<TextBlock>().Should().Contain(block =>
                    block.IsEffectivelyVisible && block.Text == "版面识别模型");
                using WriteableBitmap? frame = window.CaptureRenderedFrame();
                string preview = TestPaths.FromRepositoryRoot(".tmp", "layout-recognition", "settings.png");
                Directory.CreateDirectory(Path.GetDirectoryName(preview)!);
                frame!.Save(preview);
                await main.Settings.SelectSectionAsync("llm");
                Pump(window);
                window.GetVisualDescendants().OfType<TextBlock>().Should().NotContain(block =>
                    block.IsEffectivelyVisible && block.Text == "OCR 默认值");
            }
            finally
            {
                window.Close();
            }

            return 0;
        }, CancellationToken.None);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
