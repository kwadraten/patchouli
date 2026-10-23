using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Host.Composition;
using Patchouli.UI.ViewModels;
using Patchouli.UI.Views;

namespace Patchouli.Tests;

[Collection("RetainedTabUI")]
public sealed class LibraryLoadingFeedbackTests(AvaloniaSharedSessionFixture headless)
{
    [Fact]
    public async Task Refresh_keeps_old_rows_disables_only_the_grid_and_centers_the_loading_ring()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile file = new();
            await using MainWindowViewModel main = new(settingsPath: file.Path)
                { RuntimeDatabasePath = file.CreateDatabasePath("loading-overlay") };
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            main.Shell.ObserveLibraryRevisions(null);
            await services.Items.CreateItemAsync("book", "Old visible row");
            await main.Shell.RefreshItemsAsync();
            ObservableCollection<LibraryItemViewModel> oldItems = main.Shell.Items;
            LibraryPage page = new() { DataContext = main.Shell };
            Window window = new() { Width = 1280, Height = 800, Content = page };
            window.Show();
            try
            {
                window.UpdateLayout();
                DataGrid grid = page.FindControl<DataGrid>("LibraryGrid")!;
                Border overlay = page.FindControl<Border>("LibraryLoadingOverlay")!;
                bool sawLoading = false;
                main.Shell.PropertyChanged += (sender, args) =>
                {
                    LibraryShellViewModel shell = (LibraryShellViewModel)sender!;
                    if (args.PropertyName != nameof(LibraryShellViewModel.IsRefreshingItems) ||
                        !shell.IsRefreshingItems)
                    {
                        return;
                    }

                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    shell.Items.Should().BeSameAs(oldItems);
                    grid.IsEnabled.Should().BeFalse();
                    grid.Opacity.Should().BeLessThan(1);
                    page.IsEnabled.Should().BeTrue();
                    overlay.IsVisible.Should().BeTrue();
                    Point ringCenter =
                        overlay.TranslatePoint(new Point(overlay.Bounds.Width / 2, overlay.Bounds.Height / 2), window)!
                            .Value;
                    Point gridCenter =
                        grid.TranslatePoint(new Point(grid.Bounds.Width / 2, grid.Bounds.Height / 2), window)!.Value;
                    ringCenter.Should().Be(gridCenter);
                    using RenderTargetBitmap screenshot = new(new PixelSize(1280, 800));
                    screenshot.Render(page);
                    string path = TestPaths.FromRepositoryRoot(".tmp", "library-loading", "overlay.png");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    screenshot.Save(path);
                    sawLoading = true;
                };
                await main.Shell.RefreshItemsAsync();
                sawLoading.Should().BeTrue();
                main.Shell.IsRefreshingItems.Should().BeFalse();
                grid.IsEnabled.Should().BeTrue();
                overlay.IsVisible.Should().BeFalse();
            }
            finally
            {
                await main.ShutdownAsync();
                page.DataContext = null;
                window.Content = null;
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }

            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Rapid_collection_and_tag_changes_share_loading_and_only_publish_the_latest_filter()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile file = new();
            await using MainWindowViewModel main = new(settingsPath: file.Path)
                { RuntimeDatabasePath = file.CreateDatabasePath("rapid-filters") };
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            main.Shell.ObserveLibraryRevisions(null);
            ItemMetadata first = (await services.Items.CreateItemAsync("book", "First")).Value;
            ItemMetadata last = (await services.Items.CreateItemAsync("book", "Last")).Value;
            Collection firstCollection = (await services.Collections.CreateCollectionAsync("First collection")).Value;
            Collection lastCollection = (await services.Collections.CreateCollectionAsync("Last collection")).Value;
            await services.Collections.AddItemsAsync(firstCollection.CollectionId, [first.ItemId]);
            await services.Collections.AddItemsAsync(lastCollection.CollectionId, [last.ItemId]);
            await services.Tags.AddTagsToItemsAsync([last.ItemId], ["last-tag"]);
            await main.Shell.RefreshItemsAsync();
            List<bool> loading = [];
            List<string[]> publications = [];
            main.Shell.PropertyChanged += (sender, args) =>
            {
                LibraryShellViewModel shell = (LibraryShellViewModel)sender!;
                if (args.PropertyName == nameof(LibraryShellViewModel.IsRefreshingItems))
                {
                    loading.Add(shell.IsRefreshingItems);
                }

                if (args.PropertyName == nameof(LibraryShellViewModel.Items))
                {
                    publications.Add(shell.Items.Select(item => item.Title).ToArray());
                }
            };
            Task older = main.Shell.RefreshItemsAsync();
            main.Shell.Sidebar.SelectedCollection = main.Shell.Sidebar.Collections.Single(collection =>
                collection.CollectionId == firstCollection.CollectionId);
            main.Shell.Sidebar.ToggleTagSelection(main.Shell.Sidebar.Tags.Single(tag => tag.Name == "last-tag"));
            main.Shell.Sidebar.SelectedCollection = main.Shell.Sidebar.Collections.Single(collection =>
                collection.CollectionId == lastCollection.CollectionId);
            main.Shell.IsRefreshingItems.Should().BeTrue();
            await older;
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            while (main.Shell.IsRefreshingItems)
            {
                await Task.Delay(10, timeout.Token);
            }

            main.Shell.Items.Should().ContainSingle().Which.Title.Should().Be("Last");
            publications.Should().NotBeEmpty().And.OnlyContain(titles => titles.SequenceEqual(new[] { "Last" }));
            loading.Should().Equal(true, false);
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Failed_refresh_releases_the_loading_overlay()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile file = new();
            await using MainWindowViewModel main = new(settingsPath: file.Path)
                { RuntimeDatabasePath = file.CreateDatabasePath("failed-loading") };
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            main.Shell.ObserveLibraryRevisions(null);
            await using SqliteConnection connection = services.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            await connection.ExecuteAsync("alter table collections rename to unavailable_collections;");
            await Assert.ThrowsAsync<InvalidOperationException>(() => main.Shell.RefreshItemsAsync());
            main.Shell.IsRefreshingItems.Should().BeFalse();
            return 0;
        }, CancellationToken.None);
    }
}
