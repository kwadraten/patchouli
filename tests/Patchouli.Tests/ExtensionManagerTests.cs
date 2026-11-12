using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Composition;
using Patchouli.Host.Plugins;
using Patchouli.Mcp;
using Patchouli.Testing;
using Patchouli.UI;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Dialogs;
using Patchouli.UI.ViewModels.Settings;
using Patchouli.UI.Views;

namespace Patchouli.Tests;

[Collection("RetainedTabUI")]
public sealed class ExtensionManagerTests(AvaloniaSharedSessionFixture headless)
{
    [Fact]
    public async Task Extension_manager_opens_as_a_workspace_tab_and_settings_has_no_plugin_section()
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
            await main.OpenExtensionManagerAsync();

            main.Settings.Categories.Should().NotContain(category => category.Title == "插件");
            main.Settings.SectionEntries.Should().NotContain(entry => entry.Id == "plugin_store");
            main.Layout.Tabs.Should().ContainSingle(tab =>
                tab.Kind == WorkspaceTabKind.ExtensionManager && tab.Title == "插件与扩展管理");
            main.Layout.ActiveTab.Should().BeSameAs(
                main.Layout.Tabs.Single(tab => tab.Kind == WorkspaceTabKind.ExtensionManager));
            main.ExtensionManager.PluginStore.SupportsEditing.Should().BeFalse();
            main.ExtensionManager.PluginStore.IsEmpty.Should().BeTrue();
            store.Refreshes.Should().Be(1);
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Extension_menu_entry_opens_the_management_tab()
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
            await main.RefreshExtensionMenuAsync();

            main.ExtensionMenuEntries.Should().ContainSingle();
            ExtensionMenuEntryViewModel entry =
                (ExtensionMenuEntryViewModel)main.ExtensionMenuEntries[0];
            entry.Header.Should().Be("插件与扩展管理");

            await entry.OpenCommand.ExecuteAsync();

            main.Layout.Tabs.Should().ContainSingle(tab =>
                tab.Kind == WorkspaceTabKind.ExtensionManager);
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Manager_renders_plugin_and_extension_cards_with_filters()
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
            await main.OpenExtensionManagerAsync();
            ExtensionManagerViewModel manager = main.ExtensionManager;
            manager.Extensions.Add(new ExtensionRowViewModel(manager, "ext.demo", "演示扩展",
                "版本 1.0.0", ExtensionKind.Translation, "fixture extension", "未激活", false));
            manager.RebuildVisibleRows();
            ExtensionManagerPage page = new() { DataContext = manager };
            Window window = new() { Width = 1280, Height = 900, Content = page };
            window.Show();
            try
            {
                Pump(window);
                TextBlock heading = window.GetVisualDescendants().OfType<TextBlock>()
                    .Single(block => block.Text == "插件与扩展管理" && block.IsEffectivelyVisible);
                heading.FontSize.Should().Be(24);
                window.GetVisualDescendants().OfType<TextBlock>()
                    .Where(block => block.IsEffectivelyVisible)
                    .Select(block => block.Text).Should()
                    .Contain(["示例插件", "演示扩展"]);
                Button install = window.GetVisualDescendants().OfType<Button>()
                    .Single(button => AutomationProperties.GetName(button) == "安装" && button.IsEffectivelyVisible);
                install.Content.Should().BeOfType<Patchouli.Lucide.Avalonia.Lucide>()
                    .Which.Icon.Should().Be("Download");
                install.Bounds.Width.Should().BeGreaterThan(0);
                Point position = install.TranslatePoint(default, window)!.Value;
                position.X.Should().BeGreaterThan(260);
                (position.X + install.Bounds.Width).Should().BeLessThanOrEqualTo(window.ClientSize.Width);
                Button[] configure = window.GetVisualDescendants().OfType<Button>()
                    .Where(button => AutomationProperties.GetName(button) == "配置" && button.IsEffectivelyVisible)
                    .ToArray();
                configure.Should().NotBeEmpty();
                configure.All(button => button.Content is Patchouli.Lucide.Avalonia.Lucide { Icon: "Settings" })
                    .Should().BeTrue();
                ToggleButton pluginFilter = window.GetVisualDescendants().OfType<ToggleButton>()
                    .Single(button => Equals(button.Content, "插件") && button.IsEffectivelyVisible);
                ToggleButton notInstalled = window.GetVisualDescendants().OfType<ToggleButton>()
                    .Single(button => Equals(button.Content, "未安装") && button.IsEffectivelyVisible);
                pluginFilter.IsChecked.Should().BeFalse();
                notInstalled.IsChecked.Should().BeFalse();
                using WriteableBitmap? frame = window.CaptureRenderedFrame();
                frame.Should().NotBeNull();
                string preview = TestPaths.FromRepositoryRoot(".tmp", "extension-manager",
                    "extension-manager-page.png");
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

    [Fact]
    public async Task Uninstall_button_renders_on_a_managed_installed_row()
    {
        await headless.Session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            FakePluginStore store = new(installed: true);
            string storage = Path.Combine(RepositoryTestEnvironment.TestRoot, "plugin-store-ui",
                Guid.NewGuid().ToString("N"));
            await using MainWindowViewModel main = new(settingsPath: settings.Path,
                appPaths: new TestAppPaths(storage),
                infrastructure: new HostInfrastructureOptions { PluginStore = store });
            await main.OpenExtensionManagerAsync();
            ExtensionManagerPage page = new() { DataContext = main.ExtensionManager };
            Window window = new() { Width = 1280, Height = 900, Content = page };
            window.Show();
            try
            {
                Pump(window);
                Button uninstall = window.GetVisualDescendants().OfType<Button>()
                    .Single(button => AutomationProperties.GetName(button) == "卸载" && button.IsEffectivelyVisible);
                uninstall.Bounds.Width.Should().BeGreaterThan(0);
                Button toggle = window.GetVisualDescendants().OfType<Button>()
                    .Single(button => AutomationProperties.GetName(button) == "停用" && button.IsEffectivelyVisible);
                toggle.Content.Should().BeOfType<Patchouli.Lucide.Avalonia.Lucide>()
                    .Which.Icon.Should().Be("Power");
                Avalonia.Platform.AssetLoader.Exists(new Uri("avares://Patchouli.UI/Assets/Lucide/power.svg"))
                    .Should().BeTrue("the enable/disable action must use a bundled Lucide icon");
            }
            finally
            {
                window.Close();
            }

            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Filters_narrow_visible_rows_by_state_category_and_search()
    {
        await headless.Session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            FakePluginStore store = new(installed: true);
            string storage = Path.Combine(RepositoryTestEnvironment.TestRoot, "plugin-store-ui",
                Guid.NewGuid().ToString("N"));
            await using MainWindowViewModel main = new(settingsPath: settings.Path,
                appPaths: new TestAppPaths(storage),
                infrastructure: new HostInfrastructureOptions { PluginStore = store });
            await main.OpenExtensionManagerAsync();
            ExtensionManagerViewModel manager = main.ExtensionManager;
            manager.Extensions.Clear();
            manager.Extensions.Add(new ExtensionRowViewModel(manager, "ext.demo", "演示扩展",
                "版本 1.0.0", ExtensionKind.Translation, "fixture extension", "未激活", false));
            manager.RebuildVisibleRows();

            manager.VisibleRows.Select(row => row.Category).Should()
                .Equal([ExtensionManagerRowCategory.Extension, ExtensionManagerRowCategory.Plugin],
                    "排序按名称：演示扩展 < 示例插件");
            manager.VisibleRows.Select(row => row.Name).Should().Equal("演示扩展", "示例插件");

            manager.CategoryFilters.Single(option => option.Label == "插件").SelectCommand.Execute(null);
            manager.VisibleRows.Should().ContainSingle()
                .Which.Category.Should().Be(ExtensionManagerRowCategory.Plugin);

            manager.CategoryFilters.Single(option => option.Label == "扩展").SelectCommand.Execute(null);
            manager.VisibleRows.Should().ContainSingle()
                .Which.Name.Should().Be("演示扩展");

            manager.CategoryFilters.Single(option => option.Label == "全部").SelectCommand.Execute(null);
            manager.VisibleRows.Should().HaveCount(2);

            manager.InstallFilters.Single(option => option.Label == "已安装").SelectCommand.Execute(null);
            manager.VisibleRows.Select(row => row.Name).Should().Equal(["演示扩展", "示例插件"],
                "已安装筛选下扩展行与已安装插件行同时可见——扩展始终视为已安装");

            manager.InstallFilters.Single(option => option.Label == "未安装").SelectCommand.Execute(null);
            manager.VisibleRows.Should().BeEmpty("唯一的插件行已安装");

            manager.InstallFilters.Single(option => option.Label == "全部").SelectCommand.Execute(null);
            manager.SearchText = "示例";
            manager.VisibleRows.Should().ContainSingle().Which.Name.Should().Be("示例插件");
            manager.SearchText = "";
            manager.VisibleRows.Should().HaveCount(2);
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Configure_button_opens_the_extension_editor_for_the_row()
    {
        await headless.Session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            string storage = Path.Combine(RepositoryTestEnvironment.TestRoot, "plugin-store-ui",
                Guid.NewGuid().ToString("N"));
            await using MainWindowViewModel main = new(settingsPath: settings.Path,
                appPaths: new TestAppPaths(storage));
            ExtensionManagerViewModel manager = main.ExtensionManager;
            string? openedId = null;
            manager.ExtensionEditorShower = editor =>
            {
                openedId = editor.SelectedExtension?.Id;
                return Task.CompletedTask;
            };
            ExtensionRowViewModel row = new(manager, "ext.demo", "演示扩展", "版本 1.0.0",
                ExtensionKind.Metadata, "fixture", "未激活", false);

            await row.ConfigureCommand.ExecuteAsync();

            openedId.Should().Be("ext.demo");
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public void ShowUninstall_only_applies_to_managed_installed_records()
    {
        using TemporaryAppSettingsFile settings = new();
        using MainWindowViewModel main = new(settingsPath: settings.Path);
        PluginStoreViewModel owner = new(main);
        PluginCatalogEntry catalog = Catalog();

        new PluginStoreRowViewModel(owner,
                new PluginStoreItem(catalog, "1.0.0", true, true, ["1.0.0"], null))
            .ShowUninstall.Should().BeTrue("an enabled managed install can be uninstalled");
        new PluginStoreRowViewModel(owner,
                new PluginStoreItem(catalog, "1.0.0", false, true, ["1.0.0"], null))
            .ShowUninstall.Should().BeTrue("a disabled managed install can be uninstalled");
        new PluginStoreRowViewModel(owner,
                new PluginStoreItem(catalog, "1.0.0", true, false, ["1.0.0"], null))
            .ShowUninstall.Should().BeFalse("dev installs are not store-managed");
        new PluginStoreRowViewModel(owner,
                new PluginStoreItem(catalog, null, false, false, [], null))
            .ShowUninstall.Should().BeFalse("nothing installed means nothing to uninstall");
    }

    [Fact]
    public async Task Uninstall_confirmation_cancel_makes_no_store_call()
    {
        await headless.Session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            FakePluginStore store = new(installed: true);
            ChoiceDialogs dialogs = new() { Choice = ConfirmDialogResult.Cancel };
            string storage = Path.Combine(RepositoryTestEnvironment.TestRoot, "plugin-store-ui",
                Guid.NewGuid().ToString("N"));
            await using MainWindowViewModel main = new(settingsPath: settings.Path,
                appPaths: new TestAppPaths(storage), dialogs: dialogs,
                infrastructure: new HostInfrastructureOptions { PluginStore = store });
            await main.OpenExtensionManagerAsync();
            PluginStoreRowViewModel row = main.ExtensionManager.PluginStore.Plugins.Single();

            await row.UninstallCommand.ExecuteAsync();

            store.Uninstalls.Should().BeEmpty();
            dialogs.Shown.Should().ContainSingle();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Uninstall_confirm_calls_the_store_and_refreshes_to_an_install_row()
    {
        await headless.Session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            FakePluginStore store = new(installed: true);
            ChoiceDialogs dialogs = new() { Choice = ConfirmDialogResult.Confirm };
            string storage = Path.Combine(RepositoryTestEnvironment.TestRoot, "plugin-store-ui",
                Guid.NewGuid().ToString("N"));
            await using MainWindowViewModel main = new(settingsPath: settings.Path,
                appPaths: new TestAppPaths(storage), dialogs: dialogs,
                infrastructure: new HostInfrastructureOptions { PluginStore = store });
            await main.OpenExtensionManagerAsync();
            PluginStoreRowViewModel row = main.ExtensionManager.PluginStore.Plugins.Single();
            row.ShowUninstall.Should().BeTrue();

            await row.UninstallCommand.ExecuteAsync();

            store.Uninstalls.Should().Equal("example.test-plugin");
            PluginStoreRowViewModel refreshed = main.ExtensionManager.PluginStore.Plugins.Single();
            refreshed.ShowInstall.Should().BeTrue();
            refreshed.ShowUninstall.Should().BeFalse();
            refreshed.ShowToggle.Should().BeFalse();
            main.ExtensionManager.PluginStore.Status.Should().Contain("已卸载");
            main.ExtensionManager.PluginStore.SaveState.Should().Be(SettingsSaveState.Clean);
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Uninstall_failure_shows_the_error_not_success()
    {
        await headless.Session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            FakePluginStore store = new(installed: true)
            {
                UninstallResult = Result.Failure("unavailable", "cleanup is incomplete")
            };
            ChoiceDialogs dialogs = new() { Choice = ConfirmDialogResult.Confirm };
            string storage = Path.Combine(RepositoryTestEnvironment.TestRoot, "plugin-store-ui",
                Guid.NewGuid().ToString("N"));
            await using MainWindowViewModel main = new(settingsPath: settings.Path,
                appPaths: new TestAppPaths(storage), dialogs: dialogs,
                infrastructure: new HostInfrastructureOptions { PluginStore = store });
            await main.OpenExtensionManagerAsync();
            PluginStoreRowViewModel row = main.ExtensionManager.PluginStore.Plugins.Single();

            await row.UninstallCommand.ExecuteAsync();

            store.Uninstalls.Should().Equal("example.test-plugin");
            main.ExtensionManager.PluginStore.Status.Should().Contain("卸载失败");
            main.ExtensionManager.PluginStore.Status.Should().Contain("cleanup is incomplete");
            main.ExtensionManager.PluginStore.SaveState.Should().Be(SettingsSaveState.Failed);
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Uninstall_skips_the_store_when_the_library_changes_during_confirmation()
    {
        await headless.Session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            FakePluginStore store = new(installed: true);
            ChoiceDialogs dialogs = new() { Choice = ConfirmDialogResult.Confirm };
            string storage = Path.Combine(RepositoryTestEnvironment.TestRoot, "plugin-store-ui",
                Guid.NewGuid().ToString("N"));
            MainWindowViewModel main = new(settingsPath: settings.Path,
                appPaths: new TestAppPaths(storage), dialogs: dialogs,
                infrastructure: new HostInfrastructureOptions { PluginStore = store });
            dialogs.OnShow = () => BumpLibraryGeneration(main);
            await using (main)
            {
                await main.OpenExtensionManagerAsync();
                PluginStoreRowViewModel row = main.ExtensionManager.PluginStore.Plugins.Single();

                await row.UninstallCommand.ExecuteAsync();

                store.Uninstalls.Should().BeEmpty();
                main.ExtensionManager.PluginStore.Status.Should().NotContain("已卸载");
            }

            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Uninstall_completion_after_library_switch_leaves_views_and_status_alone()
    {
        await headless.Session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            FakePluginStore store = new(installed: true)
            {
                UninstallGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            StubPluginApi api = new();
            api.Allow("demo", "view");
            api.AddView("demo", new McpPluginViewInfo("notes", "插件笔记", "tab",
                "patchouli://plugin/demo/views/notes",
                "patchouli://plugin/demo/ui/notes.html", "ui/notes.html"));
            ChoiceDialogs dialogs = new() { Choice = ConfirmDialogResult.Confirm };
            string storage = Path.Combine(RepositoryTestEnvironment.TestRoot, "plugin-store-ui",
                Guid.NewGuid().ToString("N"));
            await using MainWindowViewModel main = new(settingsPath: settings.Path,
                appPaths: new TestAppPaths(storage), dialogs: dialogs,
                infrastructure: new HostInfrastructureOptions { PluginStore = store, PluginApi = api });
            main.PluginApiProvider = () => api;
            await main.OpenExtensionManagerAsync();
            await main.NavigateToVfsUriAsync("patchouli://plugin/demo/views/notes");
            main.Layout.Tabs.Should().ContainSingle(tab => tab.TabId == "PluginView:demo:notes");
            PluginStoreRowViewModel row = main.ExtensionManager.PluginStore.Plugins.Single();

            Task command = row.UninstallCommand.ExecuteAsync();
            await store.UninstallCalled.Task;
            BumpLibraryGeneration(main);
            store.UninstallGate!.SetResult();
            await command;

            store.Uninstalls.Should().Equal("example.test-plugin");
            main.Layout.Tabs.Should().ContainSingle(tab => tab.TabId == "PluginView:demo:notes",
                "a stale completion must not close views in the new library context");
            main.ExtensionManager.PluginStore.Status.Should().NotContain("已卸载");
            main.ExtensionManager.PluginStore.SaveState.Should().NotBe(SettingsSaveState.Failed);
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Uninstall_failure_with_a_live_mount_keeps_the_plugin_view_open()
    {
        await headless.Session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            FakePluginStore store = new(installed: true)
            {
                UninstallResult = Result.Failure(AppErrorCodes.InvalidArgument, "not managed")
            };
            StubPluginApi api = new();
            api.Allow("example.test-plugin", "view");
            api.Mounts.Add(new McpPluginMountInfo("example.test-plugin", "示例插件", "1.0",
                "patchouli://plugin/example.test-plugin", ["view"], "verified"));
            api.AddView("example.test-plugin", new McpPluginViewInfo("notes", "插件笔记", "tab",
                "patchouli://plugin/example.test-plugin/views/notes",
                "patchouli://plugin/example.test-plugin/ui/notes.html", "ui/notes.html"));
            ChoiceDialogs dialogs = new() { Choice = ConfirmDialogResult.Confirm };
            string storage = Path.Combine(RepositoryTestEnvironment.TestRoot, "plugin-store-ui",
                Guid.NewGuid().ToString("N"));
            await using MainWindowViewModel main = new(settingsPath: settings.Path,
                appPaths: new TestAppPaths(storage), dialogs: dialogs,
                infrastructure: new HostInfrastructureOptions { PluginStore = store, PluginApi = api });
            main.PluginApiProvider = () => api;
            await main.OpenExtensionManagerAsync();
            await main.NavigateToVfsUriAsync("patchouli://plugin/example.test-plugin/views/notes");
            PluginStoreRowViewModel row = main.ExtensionManager.PluginStore.Plugins.Single();

            await row.UninstallCommand.ExecuteAsync();

            main.Layout.Tabs.Should().ContainSingle(tab =>
                    tab.TabId == "PluginView:example.test-plugin:notes",
                "a refused uninstall without revocation keeps the view open");
            main.ExtensionManager.PluginStore.Status.Should().Contain("卸载失败");
            main.ExtensionManager.PluginStore.SaveState.Should().Be(SettingsSaveState.Failed);
            return 0;
        }, CancellationToken.None);
    }

    private static void BumpLibraryGeneration(MainWindowViewModel main)
    {
        FieldInfo field = typeof(MainWindowViewModel)
            .GetField("_libraryGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(main, (int)field.GetValue(main)! + 1);
    }

    private static PluginCatalogEntry Catalog()
    {
        return new PluginCatalogEntry("example.test-plugin", "示例插件", "fixture",
            "1.0.0", "MIT", "https://github.com/example/plugin", new string('a', 40),
            ["find", "fetch"],
            [
                new PluginCatalogArtifact("win-x64", "https://example.test/plugin.zip", 1,
                    new string('a', 64))
            ]);
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
        private IReadOnlyList<PluginStoreItem> _items;

        public FakePluginStore(bool withPlugin = false, bool installed = false)
        {
            _items = withPlugin || installed
                ?
                [
                    new PluginStoreItem(new PluginCatalogEntry("example.test-plugin", "示例插件",
                        "验证现有设置页布局与插件操作。",
                        "0.1.0", "MIT", "https://github.com/example/plugin", new string('a', 40),
                        ["find", "fetch", "send"],
                        [
                            new PluginCatalogArtifact("win-x64", "https://example.test/plugin.zip", 1,
                                new string('a', 64))
                        ]), installed ? "0.1.0" : null, installed, installed, installed ? ["0.1.0"] : [], null)
                ]
                : [];
        }

        public int Refreshes { get; private set; }
        public List<string> Uninstalls { get; } = [];
        public Result UninstallResult { get; set; } = Result.Success();
        public TaskCompletionSource? UninstallGate { get; set; }

        public TaskCompletionSource UninstallCalled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

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

        public async Task<Result> UninstallAsync(string pluginId, CancellationToken cancellationToken = default)
        {
            Uninstalls.Add(pluginId);
            UninstallCalled.TrySetResult();
            if (UninstallGate is { } gate)
            {
                await gate.Task;
            }

            if (UninstallResult.IsSuccess)
            {
                _items = _items.Select(item => item.Catalog.Id == pluginId
                        ? new PluginStoreItem(item.Catalog, null, false, false, [], null)
                        : item)
                    .ToArray();
            }

            return UninstallResult;
        }
    }

    private sealed class ChoiceDialogs : IDialogService
    {
        public ConfirmDialogResult? Choice { get; set; }
        public Action? OnShow { get; set; }
        public List<ConfirmDialogViewModel> Shown { get; } = [];

        public Task ShowDialogAsync(object viewModel)
        {
            return Task.CompletedTask;
        }

        public Task<TResult?> ShowDialogAsync<TResult>(object viewModel)
        {
            if (viewModel is not ConfirmDialogViewModel dialog)
            {
                return Task.FromResult(default(TResult?));
            }

            Shown.Add(dialog);
            OnShow?.Invoke();
            return Task.FromResult(Choice is TResult result ? result : default);
        }
    }

    private sealed class StubPluginApi : IMcpPluginApi
    {
        private readonly Dictionary<string, List<McpPluginViewInfo>> _views = new(StringComparer.Ordinal);
        private readonly HashSet<string> _allowed = new(StringComparer.Ordinal);

        public List<McpPluginMountInfo> Mounts { get; } = [];

        IReadOnlyList<McpPluginMountInfo> IMcpPluginApi.Mounts => Mounts;

        public void Allow(string pluginId, string verb)
        {
            _allowed.Add(pluginId + "\n" + verb);
        }

        public void AddView(string pluginId, McpPluginViewInfo view)
        {
            if (!_views.TryGetValue(pluginId, out List<McpPluginViewInfo>? views))
            {
                _views[pluginId] = views = [];
            }

            views.Add(view);
        }

        public McpPluginMountInfo? FindMount(string pluginId)
        {
            return Mounts.FirstOrDefault(mount => mount.PluginId == pluginId);
        }

        public bool AllowsVerb(string pluginId, string verb)
        {
            return _allowed.Contains(pluginId + "\n" + verb);
        }

        public bool IsEndpoint(string pluginId, string relativePath)
        {
            return false;
        }

        public IReadOnlyList<McpPluginViewInfo> Views(string pluginId)
        {
            return _views.TryGetValue(pluginId, out List<McpPluginViewInfo>? views) ? views : [];
        }

        public McpPluginViewInfo? FindView(string pluginId, string viewId)
        {
            return Views(pluginId).FirstOrDefault(view => view.ViewId == viewId);
        }

        public Result<McpPluginFetchContent?> TryFetchStatic(string pluginId, string relativePath)
        {
            return Result<McpPluginFetchContent?>.Success(null);
        }

        public Task<Result<McpPluginFindPage>> FindAsync(string pluginId, string relativeScope,
            string? query, int limit, string? cursor, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<McpPluginFetchContent>> FetchAsync(string pluginId, string relativePath,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<McpPluginSendOutcome>> SendAsync(string pluginId, string endpointPath,
            string action, JsonElement args, string? requestId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
