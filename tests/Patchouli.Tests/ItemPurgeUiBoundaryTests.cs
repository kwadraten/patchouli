using Avalonia.Controls;
using Avalonia.Threading;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Core.Import;
using Patchouli.Core.Diagnostics;
using Patchouli.Host.Composition;
using Patchouli.Core.Documents;
using Patchouli.Core.Layout;
using Patchouli.Infrastructure.Search;
using Dapper;
using Microsoft.Data.Sqlite;
using Page = Patchouli.Core.Layout.Page;
using Patchouli.Performance;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Dialogs;
using Patchouli.UI.Views;

namespace Patchouli.Tests;

[Collection("RetainedTabUI")]
[Trait("Category", "ui")]
public sealed class ItemPurgeUiBoundaryTests(AvaloniaSharedSessionFixture headless)
{
    [Fact]
    public async Task Report_and_delete_are_background_original_selection_is_frozen_and_modal_controls_stay_live()
    {
        await headless.Session.Dispatch<bool>(async () =>
        {
            await using Fixture fixture = await Fixture.CreateAsync();
            long before = UiThreadExecutionGuard.GetCount(UiThreadExecutionGuard.Sqlite);
            string originalTitle = fixture.Original.Title;
            string originalItemId = fixture.Original.ItemId;
            Task purging = fixture.Start();
            await fixture.Purge.ReportStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            fixture.Content.IsEnabled.Should().BeTrue("dependency inspection is non-modal");
            fixture.Dialogs.Confirmation.Should().BeNull();
            UiHeartbeatMeasurement reportHeartbeat = await UiHeartbeatProbe.MeasureAsync(() => Task.Delay(450));
            reportHeartbeat.Samples.Should().BeGreaterThanOrEqualTo(2);
            reportHeartbeat.MaximumGapMs.Should().BeLessThan(500);

            fixture.Main.Shell.SetSelectedItems([fixture.Other]);
            fixture.Original.Title = "A later title must not change the original confirmation";
            fixture.Purge.ReportRelease.TrySetResult();
            await fixture.Purge.DeleteStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Dispatcher.UIThread.InvokeAsync(() => { });
            fixture.Dialogs.Confirmation!.Titles.Should().Equal(originalTitle);
            fixture.Purge.DeleteIds.Should().Equal(ItemId.Parse(fixture.Original.ItemId));
            fixture.Content.IsEnabled.Should().BeFalse();
            BlockingOperationDialog dialog = fixture.Owner.OwnedWindows.OfType<BlockingOperationDialog>().Single();
            dialog.Close();
            dialog.IsVisible.Should().BeTrue("a running destructive operation cannot be dismissed");
            fixture.Dialogs.Blocking!.CanCancel.Should().BeTrue();
            fixture.Dialogs.Blocking.StatusMessage.Should().Be("等待删除替身");
            UiHeartbeatMeasurement deleteHeartbeat = await UiHeartbeatProbe.MeasureAsync(() => Task.Delay(450));
            deleteHeartbeat.Samples.Should().BeGreaterThanOrEqualTo(2);
            deleteHeartbeat.MaximumGapMs.Should().BeLessThan(500);

            fixture.Purge.DeleteRelease.TrySetResult();
            await WaitUntilAsync(() => !fixture.Dialogs.Blocking.IsRunning);
            fixture.Dialogs.Blocking.OperationState.Should().Be("已成功");
            fixture.Content.IsEnabled.Should().BeFalse("the terminal result still requires acknowledgment");
            await fixture.Dialogs.Blocking.ConfirmCommand.ExecuteAsync();
            await purging.WaitAsync(TimeSpan.FromSeconds(10));
            fixture.Content.IsEnabled.Should().BeTrue();
            fixture.Main.Status.Should().Contain("已永久删除 1");
            ItemLifecycleInfo original = (await Task.Run(() =>
                fixture.Services.Items.GetItemLifecycleAsync(ItemId.Parse(fixture.Original.ItemId)))).Value;
            ItemLifecycleInfo other = (await Task.Run(() =>
                fixture.Services.Items.GetItemLifecycleAsync(ItemId.Parse(fixture.Other.ItemId)))).Value;
            original.State.Should().Be(ItemLifecycleState.Purged);
            other.State.Should().Be(ItemLifecycleState.Trash);
            File.Exists(fixture.OriginalPath).Should().BeTrue("purge never deletes the user-owned PDF");
            long uiEntries = UiThreadExecutionGuard.GetCount(UiThreadExecutionGuard.Sqlite) - before;
            uiEntries.Should().Be(0);
            fixture.Main.Shell.Items.Should().NotContain(item => item.ItemId == originalItemId);
            await WriteHeartbeatEvidenceAsync("blocked-substitutes", new
            {
                ReportHeartbeat = reportHeartbeat, DeleteHeartbeat = deleteHeartbeat, UiSqliteEntries = uiEntries
            });
            return true;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Library_retirement_drains_a_cancellation_ignoring_report_and_never_shows_its_stale_confirmation(
        bool shutdown)
    {
        await headless.Session.Dispatch<bool>(async () =>
        {
            await using Fixture fixture = await Fixture.CreateAsync();
            fixture.Purge.IgnoreReportCancellation = true;
            Task purging = fixture.Start();
            await fixture.Purge.ReportStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task switching = shutdown ? fixture.Main.ShutdownAsync() : fixture.Main.BeginLibrarySwitchAsync();
            await Task.Delay(100);
            switching.IsCompleted.Should()
                .BeFalse("cancellation does not mean synchronous SQLite inspection has finished");
            fixture.Main.Report("new context marker");
            fixture.Purge.ReportRelease.TrySetResult();
            await switching.WaitAsync(TimeSpan.FromSeconds(10));
            await purging.WaitAsync(TimeSpan.FromSeconds(10));
            fixture.Dialogs.Confirmation.Should().BeNull();
            fixture.Purge.DeleteStarted.Task.IsCompleted.Should().BeFalse();
            fixture.Main.Status.Should().Be("new context marker");
            fixture.Main.HasOpenRuntimeDatabase.Should().BeFalse();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_confirmation_from_a_retired_library_can_never_authorize_deletion()
    {
        await headless.Session.Dispatch<bool>(async () =>
        {
            await using Fixture fixture = await Fixture.CreateAsync();
            fixture.Dialogs.DelayConfirmation = true;
            fixture.Purge.ReportRelease.TrySetResult();
            Task purging = fixture.Start();
            await fixture.Dialogs.ConfirmationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await fixture.Main.BeginLibrarySwitchAsync().WaitAsync(TimeSpan.FromSeconds(10));
            fixture.Main.Report("new context marker");

            fixture.Dialogs.ConfirmationRelease.TrySetResult(true);
            await purging.WaitAsync(TimeSpan.FromSeconds(10));

            fixture.Purge.DeleteStarted.Task.IsCompleted.Should().BeFalse();
            fixture.Main.Status.Should().Be("new context marker");
            return true;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Library_retirement_waits_for_an_accepted_delete_even_if_SQLite_ignores_cancellation(bool shutdown)
    {
        await headless.Session.Dispatch<bool>(async () =>
        {
            await using Fixture fixture = await Fixture.CreateAsync();
            fixture.Purge.IgnoreDeleteCancellation = true;
            fixture.Purge.ReportRelease.TrySetResult();
            Task purging = fixture.Start();
            await fixture.Purge.DeleteStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task retirement = shutdown ? fixture.Main.ShutdownAsync() : fixture.Main.BeginLibrarySwitchAsync();
            await Task.Delay(100);
            retirement.IsCompleted.Should()
                .BeFalse("the Host must outlive actual destructive work, not just its token");
            fixture.Main.Report("new context marker");

            fixture.Purge.DeleteRelease.TrySetResult();
            await retirement.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() => fixture.Dialogs.Blocking?.IsRunning == false);
            await fixture.Dialogs.Blocking!.ConfirmCommand.ExecuteAsync();
            await purging.WaitAsync(TimeSpan.FromSeconds(10));

            fixture.Main.Status.Should().Be("new context marker");
            fixture.Main.HasOpenRuntimeDatabase.Should().BeFalse();
            File.Exists(fixture.OriginalPath).Should().BeTrue();
            return true;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancellation_and_service_failure_never_report_success(bool cancel)
    {
        await headless.Session.Dispatch<bool>(async () =>
        {
            await using Fixture fixture = await Fixture.CreateAsync();
            fixture.Purge.FailDelete = !cancel;
            fixture.Purge.ReportRelease.TrySetResult();
            Task purging = fixture.Start();
            await fixture.Purge.DeleteStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (cancel)
            {
                await fixture.Dialogs.Blocking!.CancelCommand.ExecuteAsync();
            }
            else
            {
                fixture.Purge.DeleteRelease.TrySetResult();
            }

            await WaitUntilAsync(() => fixture.Dialogs.Blocking?.IsRunning == false);
            fixture.Dialogs.Blocking!.OperationState.Should().Be(cancel ? "已取消" : "失败");
            await fixture.Dialogs.Blocking.ConfirmCommand.ExecuteAsync();
            await purging.WaitAsync(TimeSpan.FromSeconds(10));
            fixture.Main.Status.Should().Contain(cancel ? "已取消" : "永久删除失败");
            ItemLifecycleInfo item = (await Task.Run(() =>
                fixture.Services.Items.GetItemLifecycleAsync(ItemId.Parse(fixture.Original.ItemId)))).Value;
            item.State.Should().Be(ItemLifecycleState.Trash);
            return true;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "performance")]
    public async Task Real_page_history_and_search_payload_purge_keeps_the_dispatcher_live(bool recoverFts)
    {
        await headless.Session.Dispatch<bool>(async () =>
        {
            await using Fixture fixture = await Fixture.CreateAsync();
            HostServices services = fixture.Services;
            DocumentInstanceId documentId = DocumentInstanceId.Parse(fixture.Original.DocumentInstanceId!);
            (int boxCount, int searchUnits) = await Task.Run(async () =>
            {
                IReadOnlyList<Page> pages = (await services.Pages.ListPagesAsync(documentId)).Value;
                foreach (Page page in pages)
                {
                    for (int history = 0; history < 4; history++)
                    {
                        DocumentBoxSeed[] boxes = Enumerable.Range(0, 400).Select(ordinal => new DocumentBoxSeed(
                            null, null, ordinal, DocumentBoxType.Text, null,
                            new NormalizedBBox(.04 + ordinal % 8 * .115, .03 + ordinal / 8 * .018, .1, .016),
                            new TextBoxPayload($"History {history}, paragraph {ordinal}."))).ToArray();
                        DocumentTreeRevision working = (await services.DocumentTrees.BeginWorkingRevisionAsync(
                            documentId,
                            page.PageId, boxes, DocumentTreeRevisionSource.Import)).Value;
                        (await services.DocumentTrees.CommitWorkingRevisionAsync(working.TreeRevisionId)).IsSuccess
                            .Should().BeTrue();
                    }
                }

                (await new SearchUnitBuilder(services.ConnectionFactory, services.Clock)
                    .RebuildForDocumentInstanceAsync(documentId)).IsSuccess.Should().BeTrue();
                (await services.SearchIndex.RebuildFtsForDocumentInstanceAsync(documentId)).IsSuccess.Should().BeTrue();
                if (recoverFts)
                {
                    await using SqliteConnection connection = services.ConnectionFactory.CreateConnection();
                    await connection.OpenAsync();
                    await connection.ExecuteAsync("delete from fts_cache_state;");
                }

                await using SqliteConnection read = services.ConnectionFactory.CreateReadConnection();
                await read.OpenAsync();
                return (await read.ExecuteScalarAsync<int>("select count(*) from document_boxes;"),
                    await read.ExecuteScalarAsync<int>("select count(*) from search_units;"));
            });
            boxCount.Should().BeGreaterThan(4000);
            searchUnits.Should().BeGreaterThan(1000);
            fixture.Purge.ReportRelease.TrySetResult();
            Task purging = fixture.Start();
            await fixture.Purge.DeleteStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            long before = UiThreadExecutionGuard.GetCount(UiThreadExecutionGuard.Sqlite);
            UiHeartbeatMeasurement heartbeat = await UiHeartbeatProbe.MeasureAsync(async () =>
            {
                fixture.Purge.DeleteRelease.TrySetResult();
                await WaitUntilAsync(() => fixture.Dialogs.Blocking?.IsRunning == false);
                fixture.Dialogs.Blocking!.OperationState.Should().Be("已成功");
                fixture.Content.IsEnabled.Should().BeFalse();
                await fixture.Dialogs.Blocking.ConfirmCommand.ExecuteAsync();
                await purging.WaitAsync(TimeSpan.FromSeconds(10));
                await Task.Delay(350);
            });
            long violations = UiThreadExecutionGuard.GetCount(UiThreadExecutionGuard.Sqlite) - before;
            double budget = new RegressionTolerance(EnforceUiBudgets: true).UiHeartbeatBudgetMs;
            await WriteHeartbeatEvidenceAsync(recoverFts ? "real-fts-recovery" : "real-fts-ready", new
            {
                Heartbeat = heartbeat, BudgetMs = budget, UiSqliteEntries = violations,
                DocumentBoxCount = boxCount, SearchUnitCount = searchUnits
            });
            heartbeat.Samples.Should().BeGreaterThanOrEqualTo(2);
            heartbeat.MaximumGapMs.Should().BeLessThanOrEqualTo(budget);
            violations.Should().Be(0);
            File.Exists(fixture.OriginalPath).Should().BeTrue();
            return true;
        }, CancellationToken.None);
    }

    private static async Task WriteHeartbeatEvidenceAsync(string scenario, object measurement)
    {
        string root = Path.Combine(Testing.RepositoryTestEnvironment.TemporaryRoot, "test-evidence", "item-purge");
        Directory.CreateDirectory(root);
        string json = System.Text.Json.JsonSerializer.Serialize(measurement);
        ReportPrivacy.AssertSafe(json);
        await File.WriteAllTextAsync(Path.Combine(root, scenario + "-" + Guid.NewGuid().ToString("N") + ".json"), json);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TemporaryAppSettingsFile _settings;
        private Task? _purging;
        public MainWindowViewModel Main { get; }
        public HostServices Services { get; }
        public Window Owner { get; }
        public Border Content { get; }
        public PurgeDialogs Dialogs { get; }
        public BlockingPurgeService Purge { get; }
        public LibraryItemViewModel Original { get; }
        public LibraryItemViewModel Other { get; }
        public string OriginalPath => Path.Combine(Path.GetDirectoryName(_settings.Path)!, "original.pdf");

        private Fixture(TemporaryAppSettingsFile settings, MainWindowViewModel main, HostServices services,
            Window owner, Border content, PurgeDialogs dialogs, BlockingPurgeService purge,
            LibraryItemViewModel original, LibraryItemViewModel other)
        {
            _settings = settings;
            Main = main;
            Services = services;
            Owner = owner;
            Content = content;
            Dialogs = dialogs;
            Purge = purge;
            Original = original;
            Other = other;
        }

        public static async Task<Fixture> CreateAsync()
        {
            TemporaryAppSettingsFile settings = new();
            Border content = new();
            Window owner = new() { Content = content, Width = 800, Height = 600 };
            PurgeDialogs dialogs = new(owner);
            MainWindowViewModel main = new(dialogs: dialogs, settingsPath: settings.Path)
            {
                RuntimeDatabasePath = settings.CreateDatabasePath("purge-ui-boundary")
            };
            owner.Show();
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            string originalPath = Path.Combine(Path.GetDirectoryName(settings.Path)!, "original.pdf");
            (ItemId originalId, ItemId otherId) = await Task.Run(async () =>
            {
                File.Copy(TestFixtures.RealThreePagePdf, originalPath);
                PdfImportResult imported =
                    await services.PdfImport.ImportPdfAsync(new PdfImportRequest(originalPath, "Original target", null,
                        3));
                imported.Success.Should().BeTrue(imported.ErrorMessage);
                ItemId first = ItemId.Parse(imported.CreatedItemId!);
                ItemId second = (await services.Items.CreateItemAsync("book", "Later selection")).Value.ItemId;
                (await services.Items.DeleteItemsAsync([first, second])).IsSuccess.Should().BeTrue();
                return (first, second);
            });
            main.Shell.Sidebar.SelectedSection = main.Shell.Sidebar.Sections[1];
            await main.Shell.RefreshItemsAsync();
            await WaitUntilAsync(() => main.Shell.Items.Count == 2);
            LibraryItemViewModel original = main.Shell.Items.Single(item => item.ItemId == originalId.ToString());
            LibraryItemViewModel other = main.Shell.Items.Single(item => item.ItemId == otherId.ToString());
            main.Shell.SetSelectedItems([original]);
            BlockingPurgeService purge = new(services.PurgeItems);
            main.Shell.ItemPurgeDecorator = _ => purge;
            return new Fixture(settings, main, services, owner, content, dialogs, purge, original, other);
        }

        public Task Start()
        {
            return _purging = Main.Shell.PurgeSelectedItemsCommand.ExecuteAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Purge.ReportRelease.TrySetResult();
            Purge.DeleteRelease.TrySetResult();
            Dialogs.ConfirmationRelease.TrySetResult(false);
            await Main.Shell.CancelAndDrainItemPurgeAsync();
            if (Dialogs.Blocking is { } blocking)
            {
                blocking.MarkCancelled();
                blocking.RequestClose?.Invoke(null);
            }

            if (_purging is not null)
            {
                await _purging;
            }

            await Main.ShutdownAsync();
            Owner.Close();
            _settings.Dispose();
        }
    }

    private sealed class BlockingPurgeService(IItemPurgeService inner) : IItemPurgeService
    {
        public TaskCompletionSource ReportStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReportRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DeleteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DeleteRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<ItemId> DeleteIds { get; private set; } = [];
        public bool IgnoreReportCancellation { get; set; }
        public bool IgnoreDeleteCancellation { get; set; }
        public bool FailDelete { get; set; }

        public Task<Result<ItemPurgeDependencyReport>> BuildPurgeReportAsync(ItemId itemId,
            CancellationToken cancellationToken = default, IProgress<ItemPurgeProgress>? progress = null)
        {
            return inner.BuildPurgeReportAsync(itemId, cancellationToken, progress);
        }

        public Task<Result<IReadOnlyList<ItemPurgeDependencyReport>>> BuildPurgeReportsAsync(
            IReadOnlyList<ItemId> itemIds,
            CancellationToken cancellationToken = default, IProgress<ItemPurgeProgress>? progress = null)
        {
            Dispatcher.UIThread.CheckAccess().Should().BeFalse();
            ReportStarted.TrySetResult();
            ReportRelease.Task.Wait(IgnoreReportCancellation ? CancellationToken.None : cancellationToken);
            return inner.BuildPurgeReportsAsync(itemIds, cancellationToken, progress);
        }

        public Task<Result> PurgeItemsAsync(IReadOnlyList<ItemId> itemIds,
            CancellationToken cancellationToken = default, IProgress<ItemPurgeProgress>? progress = null)
        {
            Dispatcher.UIThread.CheckAccess().Should().BeFalse();
            DeleteIds = itemIds.ToArray();
            progress?.Report(new ItemPurgeProgress("test_wait", "等待删除替身"));
            DeleteStarted.TrySetResult();
            CancellationToken token = IgnoreDeleteCancellation ? CancellationToken.None : cancellationToken;
            DeleteRelease.Task.Wait(token);
            return FailDelete
                ? Task.FromResult(Result.Failure(AppErrorCodes.DatabaseError, "injected delete failure"))
                : inner.PurgeItemsAsync(itemIds, token, progress);
        }
    }

    private sealed class PurgeDialogs : IDialogService
    {
        private readonly OwnedDialogs _inner;
        public PurgeConfirmDialogViewModel? Confirmation { get; private set; }
        public BlockingOperationDialogViewModel? Blocking { get; private set; }
        public bool DelayConfirmation { get; set; }

        public TaskCompletionSource ConfirmationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool?> ConfirmationRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PurgeDialogs(Window owner)
        {
            _inner = new OwnedDialogs(owner);
            _inner.Register<BlockingOperationDialogViewModel, BlockingOperationDialog>();
        }

        public Task ShowDialogAsync(object viewModel)
        {
            Blocking = (BlockingOperationDialogViewModel)viewModel;
            return _inner.ShowDialogAsync(viewModel);
        }

        public async Task<TResult?> ShowDialogAsync<TResult>(object viewModel)
        {
            Confirmation = (PurgeConfirmDialogViewModel)viewModel;
            ConfirmationStarted.TrySetResult();
            bool? result = DelayConfirmation ? await ConfirmationRelease.Task : true;
            return (TResult?)(object?)result;
        }
    }

    private sealed class OwnedDialogs(Window owner) : DialogService
    {
        protected override Window? GetMainWindow()
        {
            return owner;
        }
    }
}
