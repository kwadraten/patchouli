using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Host.Plugins;
using Patchouli.Testing;
using Patchouli.UI;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Settings;
using Patchouli.UI.Views;

namespace Patchouli.Tests;

[Collection("RetainedTabUI")]
public sealed class PluginStoreSettingsTests(AvaloniaSharedSessionFixture headless)
{
    [Fact]
    public async Task Plugin_store_is_a_settings_category_with_the_existing_section_contract()
    {
        await headless.Session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            FakePluginStore store = new();
            string storage = Path.Combine(RepositoryTestEnvironment.TestRoot, "plugin-store-ui",
                Guid.NewGuid().ToString("N"));
            await using MainWindowViewModel main = new(settingsPath: settings.Path,
                appPaths: new TestAppPaths(storage),
                infrastructure: new HostInfrastructureOptions { PluginStore = store });
            await main.OpenSettingsAsync("plugin_store");

            main.Settings.ActiveCategory.Title.Should().Be("插件");
            main.Settings.ActiveCategory.Content.Should().BeSameAs(main.Settings.PluginStore);
            main.Settings.PluginStore.SupportsEditing.Should().BeFalse();
            main.Settings.PluginStore.IsEmpty.Should().BeTrue();
            store.Refreshes.Should().Be(1);
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Plugin_store_renders_existing_cards_and_keeps_actions_inside_the_window()
    {
        await headless.Session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            FakePluginStore store = new(true);
            string storage = Path.Combine(RepositoryTestEnvironment.TestRoot, "plugin-store-ui",
                Guid.NewGuid().ToString("N"));
            await using MainWindowViewModel main = new(settingsPath: settings.Path,
                appPaths: new TestAppPaths(storage),
                infrastructure: new HostInfrastructureOptions { PluginStore = store });
            await main.Settings.SelectSectionAsync("plugin_store");
            SettingsPage page = new() { DataContext = main.Settings };
            Window window = new() { Width = 1280, Height = 900, Content = page };
            window.Show();
            try
            {
                Pump(window);
                TextBlock heading = window.GetVisualDescendants().OfType<TextBlock>()
                    .Single(block => block.Text == "社区插件" && block.IsEffectivelyVisible);
                heading.GetVisualAncestors().OfType<Border>().Should()
                    .Contain(border => border.Classes.Contains("Card"));
                Button install = window.GetVisualDescendants().OfType<Button>()
                    .Single(button => Equals(button.Content, "安装") && button.IsEffectivelyVisible);
                install.Bounds.Width.Should().BeGreaterThan(0);
                Point position = install.TranslatePoint(default, window)!.Value;
                position.X.Should().BeGreaterThan(260);
                (position.X + install.Bounds.Width).Should().BeLessThanOrEqualTo(window.ClientSize.Width);
                using WriteableBitmap? frame = window.CaptureRenderedFrame();
                frame.Should().NotBeNull();
                string preview = TestPaths.FromRepositoryRoot(".tmp", "plugin-store", "settings-plugin-store.png");
                Directory.CreateDirectory(Path.GetDirectoryName(preview)!);
                frame!.Save(preview);
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

    private sealed class FakePluginStore : IPluginStore
    {
        private readonly IReadOnlyList<PluginStoreItem> _items;

        public FakePluginStore(bool withPlugin = false)
        {
            _items = withPlugin
                ?
                [
                    new PluginStoreItem(new PluginCatalogEntry("example.test-plugin", "示例插件",
                        "验证现有设置页布局与插件操作。",
                        "0.1.0", "MIT", "https://github.com/example/plugin", new string('a', 40),
                        ["find", "fetch", "send"],
                        [
                            new PluginCatalogArtifact("win-x64", "https://example.test/plugin.zip", 1,
                                new string('a', 64))
                        ]), null, false, false, [], null)
                ]
                : [];
        }

        public int Refreshes { get; private set; }

        public Task<Result<PluginStoreSnapshot>> RefreshAsync(bool force,
            CancellationToken cancellationToken = default)
        {
            Refreshes++;
            return Task.FromResult(Result<PluginStoreSnapshot>.Success(
                new PluginStoreSnapshot(_items, DateTimeOffset.UnixEpoch, false, null)));
        }

        public Task<Result<PluginDefinition>> InstallAsync(string pluginId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result> SetEnabledAsync(string pluginId, bool enabled,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<PluginDefinition>> ActivateAsync(string pluginId, string version,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
