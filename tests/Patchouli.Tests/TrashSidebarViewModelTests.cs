namespace Patchouli.Tests;

using Avalonia.Headless;
using FluentAssertions;
using Core.Bibliography;
using Core.Ids;
using Core.Library;
using Core.Results;
using UI;
using UI.ViewModels;

[Collection("Avalonia")]
public sealed class TrashSidebarViewModelTests : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    public void Dispose()
    {
        _settings.Dispose();
    }

    [Fact]
    public async Task Switching_scope_changes_item_source_between_active_and_trash()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(async () =>
        {
            string databasePath = _settings.CreateDatabasePath("ui-trash-scope");
            MainWindowViewModel viewModel = new(settingsPath: _settings.Path) { RuntimeDatabasePath = databasePath };
            try
            {
                await viewModel.OpenDatabaseCommand.ExecuteAsync();
                await viewModel.Library.CreateCommand.ExecuteAsync();
                AppServices services = await viewModel.ServicesAsync();
                Result<ItemMetadata> active = await services.Items.CreateItemAsync("book", "Active Item");
                active.IsSuccess.Should().BeTrue(active.ErrorMessage);
                Result<ItemMetadata> trashed = await services.Items.CreateItemAsync("book", "Trashed Item");
                trashed.IsSuccess.Should().BeTrue(trashed.ErrorMessage);
                Result deleteResult = await services.Items.DeleteItemAsync(trashed.Value.ItemId);
                deleteResult.IsSuccess.Should().BeTrue(deleteResult.ErrorMessage);

                await viewModel.Shell.RefreshItemsAsync();
                viewModel.Shell.Items.Should().ContainSingle(item => item.Title == "Active Item");

                viewModel.Shell.Sidebar.SelectedSection = viewModel.Shell.Sidebar.Sections[1];
                await WaitUntilAsync(() => viewModel.Shell.Items.Any(item => item.Title == "Trashed Item"));
                viewModel.Shell.Items.Should().ContainSingle(item => item.Title == "Trashed Item");

                viewModel.Shell.Sidebar.SelectedSection = viewModel.Shell.Sidebar.Sections[0];
                await WaitUntilAsync(() => viewModel.Shell.Items.Any(item => item.Title == "Active Item"));
                viewModel.Shell.Items.Should().ContainSingle(item => item.Title == "Active Item");
            }
            finally
            {
                await viewModel.BeginLibrarySwitchAsync();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public void Trash_scope_hides_active_only_context_menu_items()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        session.Dispatch(() =>
        {
            MainWindowViewModel viewModel = new(settingsPath: _settings.Path);
            LibrarySidebarViewModel sidebar = viewModel.Shell.Sidebar;

            sidebar.IsActiveSelected.Should().BeTrue();
            sidebar.CanDelete.Should().BeTrue();
            sidebar.CanRestore.Should().BeFalse();
            sidebar.CanPurge.Should().BeFalse();
            viewModel.Shell.CanModifyLibraryItems.Should().BeTrue();

            sidebar.SelectedSection = sidebar.Sections[1];

            sidebar.IsTrashSelected.Should().BeTrue();
            sidebar.CanDelete.Should().BeFalse();
            sidebar.CanRestore.Should().BeTrue();
            sidebar.CanPurge.Should().BeTrue();
            viewModel.Shell.CanModifyLibraryItems.Should().BeFalse();

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Delete_selected_items_command_calls_item_service()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(async () =>
        {
            string databasePath = _settings.CreateDatabasePath("ui-delete-command");
            MainWindowViewModel viewModel = new(settingsPath: _settings.Path) { RuntimeDatabasePath = databasePath };
            try
            {
                await viewModel.OpenDatabaseCommand.ExecuteAsync();
                await viewModel.Library.CreateCommand.ExecuteAsync();
                AppServices services = await viewModel.ServicesAsync();
                Result<ItemMetadata> created = await services.Items.CreateItemAsync("book", "Delete Me");
                created.IsSuccess.Should().BeTrue(created.ErrorMessage);

                await viewModel.Shell.RefreshItemsAsync();
                LibraryItemViewModel item = viewModel.Shell.Items.Should().ContainSingle().Subject;
                viewModel.Shell.SetSelectedItems(new[] { item });

                await viewModel.Shell.DeleteSelectedItemsCommand.ExecuteAsync();

                Result<IReadOnlyList<LibraryItemRow>> activeRows = await services.LibraryItems.ListRowsAsync();
                activeRows.Value.Should().BeEmpty();
                Result<IReadOnlyList<LibraryItemRow>> trashRows = await services.LibraryItems.ListTrashedRowsAsync();
                trashRows.Value.Should().ContainSingle(row => row.Title == "Delete Me");
            }
            finally
            {
                await viewModel.BeginLibrarySwitchAsync();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Restore_selected_items_command_calls_item_service()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(async () =>
        {
            string databasePath = _settings.CreateDatabasePath("ui-restore-command");
            MainWindowViewModel viewModel = new(settingsPath: _settings.Path) { RuntimeDatabasePath = databasePath };
            try
            {
                await viewModel.OpenDatabaseCommand.ExecuteAsync();
                await viewModel.Library.CreateCommand.ExecuteAsync();
                AppServices services = await viewModel.ServicesAsync();
                Result<ItemMetadata> created = await services.Items.CreateItemAsync("book", "Restore Me");
                created.IsSuccess.Should().BeTrue(created.ErrorMessage);
                Result deleteResult = await services.Items.DeleteItemAsync(created.Value.ItemId);
                deleteResult.IsSuccess.Should().BeTrue(deleteResult.ErrorMessage);

                viewModel.Shell.Sidebar.SelectedSection = viewModel.Shell.Sidebar.Sections[1];
                await viewModel.Shell.RefreshItemsAsync();
                LibraryItemViewModel item = viewModel.Shell.Items.Should().ContainSingle().Subject;
                viewModel.Shell.SetSelectedItems(new[] { item });

                await viewModel.Shell.RestoreSelectedItemsCommand.ExecuteAsync();

                Result<IReadOnlyList<LibraryItemRow>> trashRows = await services.LibraryItems.ListTrashedRowsAsync();
                trashRows.Value.Should().BeEmpty();
                Result<IReadOnlyList<LibraryItemRow>> activeRows = await services.LibraryItems.ListRowsAsync();
                activeRows.Value.Should().ContainSingle(row => row.Title == "Restore Me");
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

        condition().Should().BeTrue("the operation should complete on the UI dispatcher");
    }
}
