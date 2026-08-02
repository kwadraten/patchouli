using Avalonia.Headless;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.UI;
using Patchouli.UI.ViewModels;

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
                AppServices services = await viewModel.ServicesAsync();
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
