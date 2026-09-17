using FluentAssertions;
using Microsoft.Data.Sqlite;
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
}
