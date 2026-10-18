using System.Reactive.Concurrency;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Reactive.Testing;
using Patchouli.Core.Csl;
using Patchouli.Host.Composition;
using Patchouli.UI;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Csl;
using Patchouli.UI.ViewModels.Settings;
using Patchouli.UI.Views;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public sealed class CslSettingsIntegrationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("fr-FR")]
    [InlineData("zz-ZZ")]
    public async Task Default_localization_preserves_saved_values_and_auto_saves_selection_and_style_default(
        string? savedLocale)
    {
        await RunAsync(async (main, services, _) =>
        {
            (await services.CslStore.SaveSettingsAsync(null, savedLocale)).IsSuccess.Should().BeTrue();
            TestScheduler scheduler = new();
            using SettingsViewModel settings = new(main, scheduler, ImmediateScheduler.Instance);
            Window window = new() { Width = 1040, Height = 680, Content = new SettingsPage { DataContext = settings } };
            window.Show();
            try
            {
                await settings.SelectSectionAsync("csl_localization");
                Pump(window);
                CslLocalizationSettingsViewModel section = settings.CslLocalizationSettings;
                ComboBox picker = window.GetVisualDescendants().OfType<ComboBox>()
                    .Single(combo => combo.Name == "DefaultCslLocaleEditor");
                section.DefaultLocaleOptions[0].DisplayName.Should().Be("使用样式默认");
                section.DefaultLocaleOptions[0].Locale.Should().BeEmpty();
                section.SelectedDefaultLocale.Locale.Should().Be(savedLocale ?? "");
                picker.SelectedItem.Should().BeSameAs(section.SelectedDefaultLocale);
                section.IsDirty.Should().BeFalse();

                picker.SelectedItem = section.DefaultLocaleOptions.Single(option => option.Locale == "de-DE");
                picker.SelectedItem = null;
                section.SelectedDefaultLocale.Locale.Should().Be("de-DE");
                section.AddMappingCommand.Execute(null); // Keep an incomplete draft from auto-saving while switching.
                CslLanguageLocaleMappingRowViewModel draftRow = section.Rows.Last();
                await settings.SelectSectionAsync("csl_installed");
                await settings.SelectSectionAsync("csl_online");
                await settings.SelectSectionAsync("csl_localization");
                Pump(window);
                section.Rows.Last().Should().BeSameAs(draftRow);
                section.SelectedDefaultLocale.Locale.Should().Be("de-DE");
                window.GetVisualDescendants().OfType<ComboBox>()
                    .Single(combo => combo.Name == "DefaultCslLocaleEditor").Should().BeSameAs(picker);
                draftRow.RemoveCommand.Execute(null);

                await DrainAutoSaveAsync(section, scheduler);
                (await services.CslStore.GetSettingsAsync()).Value.Locale.Should().Be("de-DE");
                picker.SelectedItem = section.DefaultLocaleOptions[0];
                await DrainAutoSaveAsync(section, scheduler);
                (await services.CslStore.GetSettingsAsync()).Value.Locale.Should().BeNull();
                await section.LoadAsync();
                section.SelectedDefaultLocale.DisplayName.Should().Be("使用样式默认");
                if (savedLocale is null)
                {
                    SavePreview(window, "localization.png");
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Style_routes_share_settings_and_online_state_survives_switches_and_style_changes()
    {
        await RunAsync(async (main, services, databasePath) =>
        {
            string xml = await File.ReadAllTextAsync(TestPaths.FromRepositoryRoot(
                "tests", "Patchouli.Tests", "Fixtures", "Csl", "GB-T-7714-2015-bilingual.csl"));
            CslCatalogStyle[] catalog = Enumerable.Range(0, 8).Select(index => new CslCatalogStyle(
                $"sample-{index}", $"历史研究与世界历史：较长的引用样式名称示例 {index}",
                "https://example.test/sample.csl", services.CslCatalog.CurrentSource.SourceId)).ToArray();
            foreach (CslCatalogStyle style in catalog)
            {
                (await services.CslStore.InstallStyleAsync(style, xml)).IsSuccess.Should().BeTrue();
            }

            string cacheRoot = Path.Combine(Path.GetDirectoryName(databasePath)!,
                Path.GetFileNameWithoutExtension(databasePath) + ".csl-styles");
            await File.WriteAllTextAsync(Path.Combine(cacheRoot,
                $"catalog-cache-{services.CslCatalog.CurrentSource.SourceId}.json"), JsonSerializer.Serialize(catalog));

            await main.OpenCslStyleManagerCommand.ExecuteAsync();
            AssertInstalledRoute(main);
            await main.NavigateToVfsUriAsync("patchouli://csl-styles/sample-0.csl");
            AssertInstalledRoute(main);
            await main.NavigateToVfsUriAsync("patchouli://csl-styles/");
            AssertInstalledRoute(main);
            main.OpenTabs.Count(tab => tab.Kind == WorkspaceTabKind.Settings).Should().Be(1);

            Window window = new()
                { Width = 1040, Height = 680, Content = new SettingsPage { DataContext = main.Settings } };
            window.Show();
            try
            {
                Pump(window);
                SavePreview(window, "installed.png");
                CslStyleManagerViewModel styles = main.Settings.CslStyleManager;
                await main.Settings.SelectSectionAsync("csl_online");
                styles.CatalogSources.Should().NotBeEmpty();
                styles.SearchQuery = "sample";
                await styles.SearchCommand.ExecuteAsync();
                styles.RemoteStyles.Should().HaveCount(8).And.OnlyContain(style => style.IsInstalled);
                Pump(window);
                TextBox search = window.GetVisualDescendants().OfType<TextBox>()
                    .Single(box => box.Name == "CslCatalogSearchEditor");
                ScrollViewer body = search.GetVisualAncestors().OfType<ScrollViewer>().First();
                body.Offset = new Vector(0, 160);
                Pump(window);
                double offset = body.Offset.Y;
                offset.Should().BeGreaterThan(0);
                CslCatalogStyleViewModel firstResult = styles.RemoteStyles[0];
                await main.Settings.SelectSectionAsync("csl_installed");
                await styles.InstalledStyles[0].SetDefaultCommand.ExecuteAsync();
                (await services.CslStore.GetSettingsAsync()).Value.DefaultStyleId.Should().Be("sample-0");
                await styles.InstalledStyles[0].RemoveCommand.ExecuteAsync();
                styles.InstalledStyles.Should().HaveCount(7);
                firstResult.IsInstalled.Should().BeFalse();
                (await services.CslStore.GetSettingsAsync()).Value.DefaultStyleId.Should().BeNull();
                await main.Settings.SelectSectionAsync("csl_online");
                Pump(window);
                search.Text.Should().Be("sample");
                styles.RemoteStyles[0].Should().BeSameAs(firstResult);
                body.Offset.Y.Should().BeApproximately(offset, 1);
                body.Offset = default;
                Pump(window);
                SavePreview(window, "online.png");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void AssertInstalledRoute(MainWindowViewModel main)
    {
        main.ActiveTab!.Kind.Should().Be(WorkspaceTabKind.Settings);
        main.Settings.ActiveCategory.Title.Should().Be("引用与题录");
        main.Settings.ActiveTabbedGroup!.ActiveEntry.Id.Should().Be("csl_installed");
    }

    private static async Task DrainAutoSaveAsync(CslLocalizationSettingsViewModel section, TestScheduler scheduler)
    {
        TaskCompletionSource saved = new(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(section.IsDirty) && !section.IsDirty)
            {
                saved.TrySetResult();
            }
        }

        section.PropertyChanged += OnChanged;
        try
        {
            scheduler.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);
            await saved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            section.PropertyChanged -= OnChanged;
        }
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void SavePreview(Window window, string fileName)
    {
        Pump(window);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using WriteableBitmap? frame = window.CaptureRenderedFrame();
        frame.Should().NotBeNull();
        string directory = TestPaths.FromRepositoryRoot(".tmp", "csl-settings");
        Directory.CreateDirectory(directory);
        frame!.Save(Path.Combine(directory, fileName));
    }

    private static async Task RunAsync(Func<MainWindowViewModel, HostServices, string, Task> action)
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            string databasePath = Path.Combine(Path.GetTempPath(), $"csl-settings-{Guid.NewGuid():N}.sqlite");
            await using MainWindowViewModel main = new(settingsPath: settings.Path)
                { RuntimeDatabasePath = databasePath };
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            await action(main, await main.ServicesAsync(), databasePath);
            return 0;
        }, CancellationToken.None);
    }
}
