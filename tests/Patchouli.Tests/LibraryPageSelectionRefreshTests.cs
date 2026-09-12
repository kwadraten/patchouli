using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.UI;
using Patchouli.UI.ViewModels;
using Patchouli.UI.Views;

namespace Patchouli.Tests;

/// <summary>
/// Covers the refresh path that replaces <see cref="LibraryShellViewModel.Items"/> while the
/// bound DataGrid still exposes the previous collection. Restoring the selection too early made
/// the DataGrid selection model reject the new view-model instances.
/// </summary>
[Collection("Avalonia")]
public sealed class LibraryPageSelectionRefreshTests : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    public void Dispose()
    {
        _settings.Dispose();
    }

    [Fact]
    public async Task Refreshing_replaced_items_restores_selection_by_item_id_without_throwing()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(async () =>
        {
            string databasePath = _settings.CreateDatabasePath("ui-selection-refresh");
            MainWindowViewModel viewModel = new(settingsPath: _settings.Path)
            {
                RuntimeDatabasePath = databasePath
            };
            Window window = new() { Width = 1000, Height = 700 };
            try
            {
                await viewModel.OpenDatabaseCommand.ExecuteAsync();
                await viewModel.Library.CreateCommand.ExecuteAsync();
                HostServices services = await viewModel.ServicesAsync();
                Result<ItemMetadata> first = await services.Items.CreateItemAsync("book", "First");
                first.IsSuccess.Should().BeTrue(first.ErrorMessage);
                Result<ItemMetadata> second = await services.Items.CreateItemAsync("book", "Second");
                second.IsSuccess.Should().BeTrue(second.ErrorMessage);

                window.Content = new LibraryPage { DataContext = viewModel.Shell };
                window.Show();
                window.Measure(new Size(1000, 700));
                window.Arrange(new Rect(0, 0, 1000, 700));
                DataGrid grid = ((LibraryPage)window.Content!).FindControl<DataGrid>("LibraryGrid")!;

                await viewModel.Shell.RefreshItemsAsync();
                LibraryItemViewModel selected =
                    viewModel.Shell.Items.Single(item => item.ItemId == first.Value.ItemId.ToString());
                viewModel.Shell.SetSelectedItems([selected]);
                grid.SelectedItems.OfType<LibraryItemViewModel>().Should().ContainSingle()
                    .Which.Should().BeSameAs(selected);

                // This refresh swaps in a new ObservableCollection and new row view models. The
                // DataGrid must rebind before the selection is restored; otherwise
                // DataGridSelection.SelectedItemsView.Add throws "Item not found in selection
                // model source".
                await viewModel.Shell.RefreshItemsAsync();

                viewModel.Shell.SelectedItems.Should().ContainSingle()
                    .Which.ItemId.Should().Be(first.Value.ItemId.ToString());
                grid.SelectedItems.OfType<LibraryItemViewModel>().Should().ContainSingle()
                    .Which.ItemId.Should().Be(first.Value.ItemId.ToString());
            }
            finally
            {
                window.Close();
                await viewModel.BeginLibrarySwitchAsync();
            }

            return true;
        }, CancellationToken.None);
    }
}
