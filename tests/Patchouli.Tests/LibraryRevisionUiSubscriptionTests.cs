using Avalonia.Headless;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.UI;
using Patchouli.UI.ViewModels;
using Patchouli.Host.Composition;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public sealed class LibraryRevisionUiSubscriptionTests : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    public void Dispose()
    {
        _settings.Dispose();
    }

    [Fact]
    public async Task Committed_item_change_updates_the_existing_shell_row_incrementally()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(async () =>
        {
            string databasePath = _settings.CreateDatabasePath("ui-library-revision");
            MainWindowViewModel viewModel = new(settingsPath: _settings.Path) { RuntimeDatabasePath = databasePath };
            try
            {
                await viewModel.OpenDatabaseCommand.ExecuteAsync();
                await viewModel.Library.CreateCommand.ExecuteAsync();
                HostServices services = await viewModel.ServicesAsync();
                LibraryChangeSet? observedChange = null;
                services.LibraryRevisions.ChangeCommitted += (_, eventArgs) => observedChange = eventArgs.ChangeSet;
                Result<ItemMetadata> created = await services.Items.CreateItemAsync("book", "Before revision");
                created.IsSuccess.Should().BeTrue(created.ErrorMessage);

                await viewModel.Shell.RefreshItemsAsync();
                LibraryItemViewModel originalRow = viewModel.Shell.Items.Should().ContainSingle().Subject;
                Result<ItemMetadata> updated = await services.Items.UpdateItemAsync(created.Value.ItemId,
                    new UpdateItemRequest("book", "After revision", ExpectedUpdatedAt: created.Value.UpdatedAt));
                updated.IsSuccess.Should().BeTrue(updated.ErrorMessage);
                observedChange.Should().NotBeNull("the item service must publish after its write transaction commits");
                observedChange!.ItemIds.Should().Contain(created.Value.ItemId);

                await WaitUntilAsync(() => viewModel.Shell.Items.Single().Title == "After revision");
                viewModel.Shell.Items.Should().ContainSingle().Which.Should().BeSameAs(originalRow);
            }
            finally
            {
                await viewModel.BeginLibrarySwitchAsync();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Imported_item_does_not_leak_into_the_selected_empty_collection()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(async () =>
        {
            string databasePath = _settings.CreateDatabasePath("ui-empty-collection-import");
            MainWindowViewModel viewModel = new(settingsPath: _settings.Path) { RuntimeDatabasePath = databasePath };
            try
            {
                await viewModel.OpenDatabaseCommand.ExecuteAsync();
                await viewModel.Library.CreateCommand.ExecuteAsync();
                HostServices services = await viewModel.ServicesAsync();
                Result<Collection> collection = await services.Collections.CreateCollectionAsync("Empty");
                collection.IsSuccess.Should().BeTrue(collection.ErrorMessage);
                await viewModel.Shell.RefreshItemsAsync();
                viewModel.Shell.Sidebar.SelectedNavigationItem =
                    viewModel.Shell.Sidebar.Collections.Single(item =>
                        item.CollectionId == collection.Value.CollectionId);
                viewModel.Shell.Items.Should().BeEmpty();

                Result<ItemMetadata> imported = await services.Items.CreateItemAsync("book", "Imported");
                imported.IsSuccess.Should().BeTrue(imported.ErrorMessage);
                await viewModel.Shell.ApplyChangeSetAsync([imported.Value.ItemId]);

                viewModel.Shell.Items.Should().BeEmpty(
                    "an imported item has no membership in the selected empty collection");
            }
            finally
            {
                await viewModel.BeginLibrarySwitchAsync();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Imported_item_does_not_leak_into_the_selected_tag_filter()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(async () =>
        {
            string databasePath = _settings.CreateDatabasePath("ui-tag-filter-import");
            MainWindowViewModel viewModel = new(settingsPath: _settings.Path) { RuntimeDatabasePath = databasePath };
            try
            {
                await viewModel.OpenDatabaseCommand.ExecuteAsync();
                await viewModel.Library.CreateCommand.ExecuteAsync();
                HostServices services = await viewModel.ServicesAsync();
                Result<ItemMetadata> tagged = await services.Items.CreateItemAsync("book", "Tagged",
                    tagsJson: "[\"Alpha\"]");
                tagged.IsSuccess.Should().BeTrue(tagged.ErrorMessage);
                await viewModel.Shell.RefreshItemsAsync();
                viewModel.Shell.Sidebar.ToggleTagSelection(
                    viewModel.Shell.Sidebar.Tags.Single(item => item.Name == "Alpha"));
                await viewModel.Shell.RefreshItemsAsync();

                Result<ItemMetadata> imported = await services.Items.CreateItemAsync("book", "Imported");
                imported.IsSuccess.Should().BeTrue(imported.ErrorMessage);
                await viewModel.Shell.ApplyChangeSetAsync([imported.Value.ItemId]);

                viewModel.Shell.Items.Should().ContainSingle().Which.ItemId.Should()
                    .Be(tagged.Value.ItemId.ToString());
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
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        condition().Should().BeTrue("the revision notification should be applied on the UI dispatcher");
    }
}
