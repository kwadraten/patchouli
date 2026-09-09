using Avalonia.Headless;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.UI;
using Patchouli.UI.ViewModels;
using Patchouli.Host.Composition;

namespace Patchouli.Tests;

/// <summary>
/// Reactive tag UI: committed tag writes must update the sidebar tag list/counts and the active
/// tag filter without a manual full refresh, and removing the inspector's item must clear it.
/// </summary>
[Collection("Avalonia")]
public sealed class LibraryTagReactivityTests : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    public void Dispose()
    {
        _settings.Dispose();
    }

    [Fact]
    public async Task Committed_tag_add_updates_sidebar_tags_without_full_refresh()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(async () =>
        {
            string databasePath = _settings.CreateDatabasePath("ui-tag-sidebar-reactive");
            MainWindowViewModel viewModel = new(settingsPath: _settings.Path) { RuntimeDatabasePath = databasePath };
            try
            {
                await viewModel.OpenDatabaseCommand.ExecuteAsync();
                await viewModel.Library.CreateCommand.ExecuteAsync();
                HostServices services = await viewModel.ServicesAsync();
                Result<ItemMetadata> created = await services.Items.CreateItemAsync("book", "Tagged book");
                created.IsSuccess.Should().BeTrue(created.ErrorMessage);

                await viewModel.Shell.RefreshItemsAsync();
                viewModel.Shell.Sidebar.Tags.Should().NotContain(tag => tag.Name == "reactive-tag");

                Result tagResult =
                    await services.Tags.AddTagsToItemsAsync([created.Value.ItemId], ["reactive-tag"]);
                tagResult.IsSuccess.Should().BeTrue(tagResult.ErrorMessage);

                await WaitUntilAsync(() =>
                    viewModel.Shell.Sidebar.Tags.Any(tag => tag.Name == "reactive-tag" && tag.Count == 1));
            }
            finally
            {
                await viewModel.BeginLibrarySwitchAsync();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Committed_tag_change_reapplies_the_active_sidebar_filter()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(async () =>
        {
            string databasePath = _settings.CreateDatabasePath("ui-tag-filter-reactive");
            MainWindowViewModel viewModel = new(settingsPath: _settings.Path) { RuntimeDatabasePath = databasePath };
            try
            {
                await viewModel.OpenDatabaseCommand.ExecuteAsync();
                await viewModel.Library.CreateCommand.ExecuteAsync();
                HostServices services = await viewModel.ServicesAsync();
                Result<ItemMetadata> first = await services.Items.CreateItemAsync("book", "Filter First");
                Result<ItemMetadata> second = await services.Items.CreateItemAsync("book", "Filter Second");
                Result<ItemMetadata> third = await services.Items.CreateItemAsync("book", "Filter Third");
                first.IsSuccess.Should().BeTrue(first.ErrorMessage);
                second.IsSuccess.Should().BeTrue(second.ErrorMessage);
                third.IsSuccess.Should().BeTrue(third.ErrorMessage);
                Result tagResult = await services.Tags.AddTagsToItemsAsync(
                    [first.Value.ItemId, second.Value.ItemId], ["filter-tag"]);
                tagResult.IsSuccess.Should().BeTrue(tagResult.ErrorMessage);

                await viewModel.Shell.RefreshItemsAsync();
                viewModel.Shell.Sidebar.ToggleTagSelection(
                    viewModel.Shell.Sidebar.Tags.Single(tag => tag.Name == "filter-tag"));
                // The shell debounces tag selection into a refresh; wait for the filtered grid.
                await WaitUntilAsync(() =>
                    viewModel.Shell.Items.Count == 2 &&
                    viewModel.Shell.Items.All(item => item.Title is "Filter First" or "Filter Second"));
                LibraryItemViewModel surviving = viewModel.Shell.Items.Single(item => item.Title == "Filter Second");
                viewModel.Shell.SelectedItem = surviving;

                // Losing the filtered tag removes the item from the grid; the surviving item's
                // selection must be preserved.
                Result removeResult =
                    await services.Tags.RemoveTagFromItemsAsync([first.Value.ItemId], "filter-tag");
                removeResult.IsSuccess.Should().BeTrue(removeResult.ErrorMessage);

                await WaitUntilAsync(() =>
                    viewModel.Shell.Items.Count == 1 && viewModel.Shell.Items[0].Title == "Filter Second");
                viewModel.Shell.SelectedItem.Should().BeSameAs(surviving);

                // Gaining the filtered tag adds the item to the grid.
                Result addResult =
                    await services.Tags.AddTagsToItemsAsync([third.Value.ItemId], ["filter-tag"]);
                addResult.IsSuccess.Should().BeTrue(addResult.ErrorMessage);

                await WaitUntilAsync(() =>
                    viewModel.Shell.Items.Count == 2 &&
                    viewModel.Shell.Items.Any(item => item.Title == "Filter Third"));
                viewModel.Shell.Items.Should().NotContain(item => item.Title == "Filter First");
                viewModel.Shell.SelectedItem.Should().BeSameAs(surviving);
            }
            finally
            {
                await viewModel.BeginLibrarySwitchAsync();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Deleting_the_selected_item_clears_the_inspector()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(async () =>
        {
            string databasePath = _settings.CreateDatabasePath("ui-inspector-cleared-on-delete");
            MainWindowViewModel viewModel = new(settingsPath: _settings.Path) { RuntimeDatabasePath = databasePath };
            try
            {
                await viewModel.OpenDatabaseCommand.ExecuteAsync();
                await viewModel.Library.CreateCommand.ExecuteAsync();
                HostServices services = await viewModel.ServicesAsync();
                Result<ItemMetadata> created = await services.Items.CreateItemAsync("book", "Doomed book");
                created.IsSuccess.Should().BeTrue(created.ErrorMessage);

                await viewModel.Shell.RefreshItemsAsync();
                LibraryItemViewModel row = viewModel.Shell.Items.Should().ContainSingle().Subject;
                viewModel.Shell.SelectedItem = row;
                await WaitUntilAsync(() => !viewModel.Shell.Inspector.IsEmpty);
                viewModel.Shell.Inspector.Title.Should().Be("Doomed book");

                Result deleteResult = await services.Items.DeleteItemAsync(created.Value.ItemId);
                deleteResult.IsSuccess.Should().BeTrue(deleteResult.ErrorMessage);

                await WaitUntilAsync(() =>
                    viewModel.Shell.SelectedItem is null && viewModel.Shell.Inspector.IsEmpty);
                viewModel.Shell.Items.Should().BeEmpty();
            }
            finally
            {
                await viewModel.BeginLibrarySwitchAsync();
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

        condition().Should().BeTrue("the committed change should be applied on the UI dispatcher");
    }
}
