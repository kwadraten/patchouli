using Avalonia.Headless;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public sealed class LibraryPaginationUiTests : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _settings.Dispose();
    }

    [Fact]
    public async Task Library_shell_loads_keyset_pages_of_one_hundred_without_duplicate_requests()
    {
        MainWindowViewModel? main = null;
        try
        {
            string databasePath = _settings.CreateDatabasePath("library-pagination");
            main = new MainWindowViewModel(settingsPath: _settings.Path)
            {
                RuntimeDatabasePath = databasePath
            };
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            main.Shell.ObserveLibraryRevisions(null);

            HostServices services = await main.ServicesAsync();
            for (int index = 0; index < 205; index++)
            {
                await services.Items.CreateItemAsync("book", $"Paged Item {index:D3}");
            }

            await main.Shell.RefreshItemsAsync();

            main.Shell.Items.Should().HaveCount(LibraryShellViewModel.LibraryPageSize);
            main.Shell.LoadedItemCount.Should().Be(LibraryShellViewModel.LibraryPageSize);
            main.Shell.HasMoreItems.Should().BeTrue();
            string[] selectedIds = main.Shell.Items.Take(2).Select(item => item.ItemId).ToArray();
            main.Shell.SetSelectedItems(main.Shell.Items.Take(2));

            Task firstPrefetch = main.Shell.LoadNextPageAsync();
            Task duplicatePrefetch = main.Shell.LoadNextPageAsync();
            await Task.WhenAll(firstPrefetch, duplicatePrefetch);

            main.Shell.Items.Should().HaveCount(200);
            main.Shell.Items.Select(item => item.ItemId).Should().OnlyHaveUniqueItems();
            main.Shell.SelectedItems.Select(item => item.ItemId).Should().Equal(selectedIds);
            main.Shell.HasMoreItems.Should().BeTrue();

            await main.Shell.LoadNextPageAsync();

            main.Shell.Items.Should().HaveCount(205);
            main.Shell.Items.Select(item => item.ItemId).Should().OnlyHaveUniqueItems();
            main.Shell.HasMoreItems.Should().BeFalse();
        }
        finally
        {
            if (main is not null)
            {
                await main.ShutdownAsync();
            }
        }
    }

    [Fact]
    public async Task Refresh_in_flight_keeps_page_loads_from_publishing_stale_generations()
    {
        MainWindowViewModel? main = null;
        try
        {
            string databasePath = _settings.CreateDatabasePath("library-refresh-inflight");
            main = new MainWindowViewModel(settingsPath: _settings.Path)
            {
                RuntimeDatabasePath = databasePath
            };
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            main.Shell.ObserveLibraryRevisions(null);

            HostServices services = await main.ServicesAsync();
            for (int index = 0; index < 205; index++)
            {
                await services.Items.CreateItemAsync("book", $"Inflight Item {index:D3}");
            }

            await main.Shell.RefreshItemsAsync();
            main.Shell.Items.Should().HaveCount(LibraryShellViewModel.LibraryPageSize);
            int settledGeneration = main.Shell.GetPagingStatus().PagingGeneration;

            // A refresh in flight must fence off page loads, and a superseded refresh must not
            // publish: only the newest generation may land on Items.
            Task first = main.Shell.RefreshItemsAsync();
            Task fencedPageLoad = main.Shell.LoadNextPageAsync();
            Task second = main.Shell.RefreshItemsAsync();
            await Task.WhenAll(first, fencedPageLoad, second);

            LibraryPagingStatus status = main.Shell.GetPagingStatus();
            status.PagingGeneration.Should().Be(settledGeneration + 2);
            status.IsRefreshingItems.Should().BeFalse();
            status.HasPageQuery.Should().BeTrue();
            main.Shell.Items.Should().HaveCount(LibraryShellViewModel.LibraryPageSize);
            main.Shell.Items.Select(item => item.ItemId).Should().OnlyHaveUniqueItems();
            main.Shell.HasMoreItems.Should().BeTrue();
        }
        finally
        {
            if (main is not null)
            {
                await main.ShutdownAsync();
            }
        }
    }

    [Fact]
    public async Task Superseded_page_load_publishes_nothing_and_returns_quietly()
    {
        MainWindowViewModel? main = null;
        try
        {
            string databasePath = _settings.CreateDatabasePath("library-superseded-page");
            main = new MainWindowViewModel(settingsPath: _settings.Path)
            {
                RuntimeDatabasePath = databasePath
            };
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            main.Shell.ObserveLibraryRevisions(null);

            HostServices services = await main.ServicesAsync();
            for (int index = 0; index < 205; index++)
            {
                await services.Items.CreateItemAsync("book", $"Superseded Item {index:D3}");
            }

            await main.Shell.RefreshItemsAsync();
            main.Shell.Items.Should().HaveCount(LibraryShellViewModel.LibraryPageSize);

            // A page load racing a newer refresh is an internal generation switch, not a caller
            // failure: it must complete quietly and never publish the stale fetch.
            Task pageLoad = main.Shell.LoadNextPageAsync();
            Task refresh = main.Shell.RefreshItemsAsync();
            await Task.WhenAll(pageLoad, refresh);

            main.Shell.Items.Should().HaveCount(LibraryShellViewModel.LibraryPageSize);
            main.Shell.Items.Select(item => item.ItemId).Should().OnlyHaveUniqueItems();
            main.Shell.HasMoreItems.Should().BeTrue();
        }
        finally
        {
            if (main is not null)
            {
                await main.ShutdownAsync();
            }
        }
    }

    [Fact]
    public async Task Caller_cancellation_surfaces_without_publishing_or_poisoning_the_session()
    {
        MainWindowViewModel? main = null;
        try
        {
            string databasePath = _settings.CreateDatabasePath("library-caller-cancel");
            main = new MainWindowViewModel(settingsPath: _settings.Path)
            {
                RuntimeDatabasePath = databasePath
            };
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            main.Shell.ObserveLibraryRevisions(null);

            HostServices services = await main.ServicesAsync();
            for (int index = 0; index < 205; index++)
            {
                await services.Items.CreateItemAsync("book", $"Cancel Item {index:D3}");
            }

            await main.Shell.RefreshItemsAsync();
            main.Shell.Items.Should().HaveCount(LibraryShellViewModel.LibraryPageSize);

            using CancellationTokenSource cancelled = new();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                main.Shell.LoadNextPageAsync(cancelled.Token));
            main.Shell.Items.Should().HaveCount(LibraryShellViewModel.LibraryPageSize,
                "caller cancellation must not publish a page");
            main.Shell.GetPagingStatus().IsLoadingMoreItems.Should().BeFalse(
                "a cancelled page load must release the in-flight guard");

            // Caller cancellation is scoped to that call: the paging session survives and the
            // next parameterless call still loads the next page.
            await main.Shell.LoadNextPageAsync();
            main.Shell.Items.Should().HaveCount(200,
                "caller cancellation must not cancel the shared paging session");
        }
        finally
        {
            if (main is not null)
            {
                await main.ShutdownAsync();
            }
        }
    }

    [Fact]
    public async Task Detaching_the_library_resets_paging_state_and_blocks_late_publication()
    {
        MainWindowViewModel? main = null;
        try
        {
            string databasePath = _settings.CreateDatabasePath("library-detach-paging");
            main = new MainWindowViewModel(settingsPath: _settings.Path)
            {
                RuntimeDatabasePath = databasePath
            };
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            main.Shell.ObserveLibraryRevisions(null);

            HostServices services = await main.ServicesAsync();
            for (int index = 0; index < 205; index++)
            {
                await services.Items.CreateItemAsync("book", $"Detached Item {index:D3}");
            }

            await main.Shell.RefreshItemsAsync();
            main.Shell.Items.Should().HaveCount(LibraryShellViewModel.LibraryPageSize);
            main.Shell.HasMoreItems.Should().BeTrue();

            main.Shell.ObserveLibraryRevisions(null);
            LibraryPagingStatus detached = main.Shell.GetPagingStatus();
            detached.HasMoreItems.Should().BeFalse();
            detached.HasPageQuery.Should().BeFalse();
            detached.HasNextPageCursor.Should().BeFalse();
            detached.IsRefreshingItems.Should().BeFalse();

            // A detached shell must never page more rows out of the old library.
            await main.Shell.LoadNextPageAsync();
            main.Shell.Items.Should().HaveCount(LibraryShellViewModel.LibraryPageSize);
        }
        finally
        {
            if (main is not null)
            {
                await main.ShutdownAsync();
            }
        }
    }

    [Fact]
    public async Task Filter_change_during_refresh_publishes_only_the_latest_selection()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch(async () =>
        {
            string databasePath = _settings.CreateDatabasePath("library-filter-paging");
            MainWindowViewModel main = new(settingsPath: _settings.Path)
            {
                RuntimeDatabasePath = databasePath
            };
            try
            {
                await main.OpenDatabaseCommand.ExecuteAsync();
                await main.Library.CreateCommand.ExecuteAsync();
                HostServices services = await main.ServicesAsync();
                main.Shell.ObserveLibraryRevisions(null);

                Result<ItemMetadata> tagged = await services.Items.CreateItemAsync("book", "Tagged");
                tagged.IsSuccess.Should().BeTrue(tagged.ErrorMessage);
                Result tagResult =
                    await services.Tags.AddTagsToItemsAsync([tagged.Value.ItemId], ["paging-tag"]);
                tagResult.IsSuccess.Should().BeTrue(tagResult.ErrorMessage);
                await services.Items.CreateItemAsync("book", "Untagged A");
                await services.Items.CreateItemAsync("book", "Untagged B");

                await main.Shell.RefreshItemsAsync();
                main.Shell.Items.Should().HaveCount(3);
                int settledGeneration = main.Shell.GetPagingStatus().PagingGeneration;

                // The sidebar bumps the paging generation synchronously; a refresh already in
                // flight must not publish its unfiltered rows over the newer selection.
                Task inFlight = main.Shell.RefreshItemsAsync();
                main.Shell.Sidebar.ToggleTagSelection(
                    main.Shell.Sidebar.Tags.Single(tag => tag.Name == "paging-tag"));
                main.Shell.IsRefreshingItems.Should().BeTrue();
                main.Shell.GetPagingStatus().PagingGeneration.Should().Be(settledGeneration + 2);
                await inFlight;

                Task filtered = main.Shell.CurrentRefreshCompletion;
                filtered.IsCompleted.Should().BeFalse(
                    "the filtered refresh is the new current generation");
                await WaitUntilAsync(() =>
                    !main.Shell.IsRefreshingItems && main.Shell.GetPagingStatus().HasPageQuery);

                main.Shell.Items.Should().ContainSingle().Which.Title.Should().Be("Tagged");
            }
            finally
            {
                await main.ShutdownAsync();
            }

            return true;
        }, CancellationToken.None);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 150; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        condition().Should().BeTrue("the condition should be reached on the UI dispatcher");
    }
}
