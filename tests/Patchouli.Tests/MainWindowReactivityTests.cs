using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using FluentAssertions;
using Microsoft.Reactive.Testing;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Search;
using Patchouli.Host.Composition;
using Patchouli.Infrastructure.Snapshots;
using Patchouli.UI;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Core;

namespace Patchouli.Tests;

public sealed class MainWindowReactivityTests
{
    [Fact]
    public void Direct_mutation_of_IsFirstRunVisible_updates_derived_properties_without_manual_refresh()
    {
        using TemporaryAppSettingsFile settings = new();
        ImmediateScheduler scheduler = ImmediateScheduler.Instance;
        using MainWindowViewModel vm = new(
            new FakeClipboard(),
            settingsPath: settings.Path,
            timingScheduler: scheduler,
            uiScheduler: scheduler);

        vm.IsFirstRunVisible.Should().BeFalse();
        vm.IsLibraryVisible.Should().BeTrue();
        vm.IsSearchEnabled.Should().BeTrue();

        // Mutate source state directly without calling Refresh or Raise
        vm.IsFirstRunVisible = true;

        vm.IsLibraryVisible.Should().BeFalse();
        vm.IsSearchEnabled.Should().BeFalse();

        vm.IsFirstRunVisible = false;
        vm.IsLibraryVisible.Should().BeTrue();
        vm.IsSearchEnabled.Should().BeTrue();
    }

    [Fact]
    public void RuntimeDatabasePath_mutation_updates_VersionInfo_without_manual_refresh()
    {
        using TemporaryAppSettingsFile settings = new();
        ImmediateScheduler scheduler = ImmediateScheduler.Instance;
        using MainWindowViewModel vm = new(
            new FakeClipboard(),
            settingsPath: settings.Path,
            timingScheduler: scheduler,
            uiScheduler: scheduler);

        string testDb = "C:/data/custom_library.db";
        vm.RuntimeDatabasePath = testDb;

        vm.VersionInfo.Should().Contain(testDb);
    }

    [Fact]
    public void Report_and_ReportError_update_status_without_manual_raise()
    {
        using TemporaryAppSettingsFile settings = new();
        ImmediateScheduler scheduler = ImmediateScheduler.Instance;
        using MainWindowViewModel vm = new(
            new FakeClipboard(),
            settingsPath: settings.Path,
            timingScheduler: scheduler,
            uiScheduler: scheduler);

        List<string?> changedProperties = [];
        vm.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        vm.Report("系统已就绪");
        vm.Status.Should().Be("系统已就绪");
        vm.StatusIsError.Should().BeFalse();
        changedProperties.Should().Contain(nameof(MainWindowViewModel.Status));

        changedProperties.Clear();
        vm.ReportError("网络连接失败");
        vm.Status.Should().Be("网络连接失败");
        vm.StatusIsError.Should().BeTrue();
        changedProperties.Should().Contain(nameof(MainWindowViewModel.Status));
        changedProperties.Should().Contain(nameof(MainWindowViewModel.StatusIsError));
    }

    [Fact]
    public void Snapshot_properties_update_derived_flags_and_commands_without_manual_raise()
    {
        using TemporaryAppSettingsFile settings = new();
        ImmediateScheduler scheduler = ImmediateScheduler.Instance;
        using MainWindowViewModel vm = new(
            new FakeClipboard(),
            settingsPath: settings.Path,
            timingScheduler: scheduler,
            uiScheduler: scheduler);

        SnapshotViewModel snapshot = vm.Snapshot;
        List<string?> changed = [];
        snapshot.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        snapshot.IsOverviewSectionActive.Should().BeTrue();
        snapshot.IsPublishSectionActive.Should().BeFalse();
        snapshot.IsReceiveSectionActive.Should().BeFalse();

        // Mutate active section to publish section
        snapshot.ActiveNavSection = snapshot.NavSections[1];
        snapshot.IsOverviewSectionActive.Should().BeFalse();
        snapshot.IsPublishSectionActive.Should().BeTrue();
        snapshot.IsReceiveSectionActive.Should().BeFalse();
        changed.Should().Contain(nameof(SnapshotViewModel.IsPublishSectionActive));

        // Mutate ConfirmApply
        snapshot.ConfirmApply.Should().BeFalse();
        snapshot.ApplyCommand.CanExecute(null).Should().BeFalse();

        snapshot.ConfirmApply = true;
        snapshot.ConfirmApply.Should().BeTrue();
        changed.Should().Contain(nameof(SnapshotViewModel.ConfirmApply));
        changed.Should().Contain(nameof(SnapshotViewModel.CanApply));
    }

    [Fact]
    public void Workspace_passthrough_properties_update_automatically_via_Rx_without_RaiseWorkspaceStateChanged()
    {
        using TemporaryAppSettingsFile settings = new();
        ImmediateScheduler scheduler = ImmediateScheduler.Instance;
        using MainWindowViewModel vm = new(
            new FakeClipboard(),
            settingsPath: settings.Path,
            timingScheduler: scheduler,
            uiScheduler: scheduler);

        WorkspaceTabViewModel readerTab = new(
            WorkspaceTabKind.PdfWorkspace, "reader", "Reader", "Document", true, null,
            new WorkspaceTabViewModel(WorkspaceTabKind.Library, "lib", "Lib", "Folder", false, null, null!));

        vm.Layout.Tabs.Add(readerTab);
        vm.Layout.ActiveTab = readerTab;

        vm.IsReaderTabActive.Should().BeTrue();
        vm.IsLibraryTabActive.Should().BeFalse();
        vm.ShowSelectedDocumentTab.Should().BeTrue();
    }

    [Fact]
    public void Revision_batching_coalesces_multiple_change_sets_in_20ms_window_and_applies_sequentially()
    {
        TestScheduler timingScheduler = new();
        TestScheduler uiScheduler = new();
        Subject<LibraryChangeSet> changes = new();
        List<LibraryChangeSet> appliedSets = [];
        List<Exception> errors = [];
        TimeSpan window = TimeSpan.FromMilliseconds(20);

        using IDisposable subscription = ReactiveUiFlow.SubscribeBufferedSequential(
            changes,
            window,
            timingScheduler,
            uiScheduler,
            (batch, _) =>
            {
                appliedSets.Add(LibraryShellViewModel.MergeChangeSets(batch));
                return Task.CompletedTask;
            },
            errors.Add);

        ItemId item1 = ItemId.New();
        ItemId item2 = ItemId.New();
        ItemId item3 = ItemId.New();

        // Emit 2 changes within the 20ms buffer window
        changes.OnNext(new LibraryChangeSet(1, [item1], [], [], [], [], []));
        changes.OnNext(new LibraryChangeSet(2, [item2], [], [], [], [], []));

        // Clock advances past buffer window
        timingScheduler.AdvanceBy(window.Ticks + 10);
        uiScheduler.AdvanceBy(1);

        appliedSets.Should().ContainSingle();
        appliedSets[0].NewRevision.Should().Be(2);
        appliedSets[0].ItemIds.Should().BeEquivalentTo([item1, item2]);

        // Emit 3rd change in a separate window
        changes.OnNext(new LibraryChangeSet(3, [item3], [], [], [], [], []));
        timingScheduler.AdvanceBy(window.Ticks + 10);
        uiScheduler.AdvanceBy(1);

        appliedSets.Should().HaveCount(2);
        appliedSets[1].NewRevision.Should().Be(3);
        appliedSets[1].ItemIds.Should().BeEquivalentTo([item3]);
        errors.Should().BeEmpty();
    }

    [Fact]
    public void Rapid_query_rewrite_toggle_cancels_older_in_flight_persistence()
    {
        Subject<bool> requests = new();
        List<CancellationToken> tokens = [];
        List<TaskCompletionSource<bool>> tcsList = [];
        List<bool> appliedValues = [];

        using IDisposable subscription = requests
            .Select(enabled => Observable.FromAsync(async ct =>
            {
                tokens.Add(ct);
                TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
                tcsList.Add(tcs);
                bool res = await tcs.Task;
                ct.ThrowIfCancellationRequested();
                appliedValues.Add(res);
            }))
            .Switch()
            .Subscribe();

        // First rapid toggle to true
        requests.OnNext(true);
        tokens.Should().ContainSingle();
        tokens[0].IsCancellationRequested.Should().BeFalse();

        // Second rapid toggle to false before first completes
        requests.OnNext(false);
        tokens.Should().HaveCount(2);
        tokens[0].IsCancellationRequested.Should().BeTrue();
        tokens[1].IsCancellationRequested.Should().BeFalse();

        // Complete the second request; first was cancelled so only second value is recorded
        tcsList[1].TrySetResult(false);
        appliedValues.Should().Equal(false);
    }

    [Fact]
    public async Task Query_rewrite_toggle_end_to_end_persists_latest_and_rolls_back_on_failure()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"reactivity-query-rewrite-{Guid.NewGuid():N}.sqlite");
        try
        {
            using TemporaryAppSettingsFile settings = new();
            // Real async I/O + time-based Rx operators (Buffer windows) require a real
            // scheduler; ImmediateScheduler blocks the raising thread on time-based windows.
            TaskPoolScheduler scheduler = TaskPoolScheduler.Default;
            using MainWindowViewModel vm = new(
                new FakeClipboard(),
                settingsPath: settings.Path,
                timingScheduler: scheduler,
                uiScheduler: scheduler)
            {
                RuntimeDatabasePath = dbPath
            };

            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            HostServices services = await vm.ServicesAsync();

            // Seed a known persisted value, then let the production refresh path load it
            await services.SearchProfiles.SetRewriteEnabledAsync(true);
            await vm.RefreshQueryRewriteEnabledAsync();

            // Wait for the initial value to flow through the async pipeline
            for (int i = 0; i < 200 && !vm.QueryRewriteEnabled; i++)
            {
                await Task.Delay(10);
            }

            vm.QueryRewriteEnabled.Should().BeTrue();

            // Rapid consecutive setting of QueryRewriteEnabled directly on vm:
            // false -> true -> false
            vm.QueryRewriteEnabled = false;
            vm.QueryRewriteEnabled = true;
            vm.QueryRewriteEnabled = false;

            // Wait for production pipeline to complete the latest persistence
            for (int i = 0; i < 100 && vm.Status != "已停用查询重写。"; i++)
            {
                await Task.Delay(10);
            }

            vm.Status.Should().Be("已停用查询重写。");
            vm.StatusIsError.Should().BeFalse();
            vm.QueryRewriteEnabled.Should().BeFalse();

            // Assert latest persistence won in the underlying service
            Result<SearchProfileSettings> settingsAfterRapid = await services.SearchProfiles.GetSearchSettingsAsync();
            settingsAfterRapid.IsSuccess.Should().BeTrue();
            settingsAfterRapid.Value.RewriteEnabled.Should().BeFalse();

            // The UI request stream observes setters on a TaskPool scheduler. Seeing the disabled
            // status can belong to the first `false` in the rapid sequence while a later queued
            // write is still completing. Let the persisted value remain stable across several
            // scheduler turns before installing the failure trigger below.
            int stableDisabledReads = 0;
            for (int attempt = 0; attempt < 100 && stableDisabledReads < 5; attempt++)
            {
                await Task.Delay(20);
                Result<SearchProfileSettings> settledSettings = await services.SearchProfiles.GetSearchSettingsAsync();
                if (settledSettings.IsSuccess && !settledSettings.Value.RewriteEnabled &&
                    vm.QueryRewriteEnabled == false && vm.Status == "已停用查询重写。" && !vm.StatusIsError)
                {
                    stableDisabledReads++;
                }
                else
                {
                    stableDisabledReads = 0;
                }
            }

            stableDisabledReads.Should().Be(5,
                "the rapid request stream should settle on its final disabled value before injecting a failure");

            // Simulate failure during persistence via an SQLite trigger on search_settings
            await using (SqliteConnection conn = new($"Data Source={dbPath}"))
            {
                await conn.OpenAsync();
                await using SqliteCommand cmd = conn.CreateCommand();
                cmd.CommandText =
                    "CREATE TRIGGER fail_rewrite_update BEFORE UPDATE ON search_settings BEGIN SELECT RAISE(ABORT, 'forced-rewrite-failure'); END;";
                await cmd.ExecuteNonQueryAsync();
            }

            // Attempt to toggle to true; the persistence will fail due to the trigger
            vm.QueryRewriteEnabled = true;

            // Wait for production pipeline to handle failure and roll back
            for (int i = 0; i < 100 && (!vm.StatusIsError || vm.QueryRewriteEnabled); i++)
            {
                await Task.Delay(10);
            }

            vm.StatusIsError.Should().BeTrue();
            vm.Status.Should().Contain("查询重写设置保存失败");
            vm.Status.Should().Contain("forced-rewrite-failure");
            // Assert that it rolled back to persisted value (false)
            vm.QueryRewriteEnabled.Should().BeFalse();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
            {
                try
                {
                    File.Delete(dbPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    [Fact]
    public void Small_viewmodels_update_properties_and_commands_without_manual_raise()
    {
        using TemporaryAppSettingsFile settings = new();
        ImmediateScheduler scheduler = ImmediateScheduler.Instance;
        using MainWindowViewModel vm = new(
            new FakeClipboard(),
            settingsPath: settings.Path,
            timingScheduler: scheduler,
            uiScheduler: scheduler);

        vm.Library.RenameTo = "New Name";
        vm.Library.RenameCommand.CanExecute(null).Should().BeTrue();
        vm.Library.RenameTo = "   ";
        vm.Library.RenameCommand.CanExecute(null).Should().BeFalse();

        vm.Bibliography.ItemType = "book";
        vm.Bibliography.Title = "Sample Book";
        vm.Bibliography.CreateItemCommand.CanExecute(null).Should().BeTrue();
        vm.Bibliography.Title = "";
        vm.Bibliography.CreateItemCommand.CanExecute(null).Should().BeFalse();

        vm.FileDocument.FilePath = "C:/sample.pdf";
        vm.FileDocument.RegisterCommand.CanExecute(null).Should().BeTrue();
        vm.FileDocument.FilePath = "";
        vm.FileDocument.RegisterCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void LibraryTabTitle_updates_open_tabs_title_automatically_via_Rx()
    {
        using TemporaryAppSettingsFile settings = new();
        ImmediateScheduler scheduler = ImmediateScheduler.Instance;
        using MainWindowViewModel vm = new(
            new FakeClipboard(),
            settingsPath: settings.Path,
            timingScheduler: scheduler,
            uiScheduler: scheduler);

        WorkspaceTabViewModel? libTab = vm.OpenTabs.FirstOrDefault(t => t.Kind == WorkspaceTabKind.Library);
        libTab.Should().NotBeNull();
        libTab!.Title.Should().Be("我的书库");

        vm.Shell.LibraryName = "新资料库";
        vm.LibraryTabTitle.Should().Be("新资料库");
        libTab.Title.Should().Be("新资料库");
    }

    [Fact]
    public async Task Snapshot_RefreshAsync_notifies_HasLastError_when_status_has_error()
    {
        using TemporaryAppSettingsFile settings = new();
        using MainWindowViewModel vm = new(
            new FakeClipboard(),
            dialogs: new FakeDialogService(),
            settingsPath: settings.Path);

        HostServices services = await vm.ServicesAsync();
        Result<LibraryMetadata> library = await services.Library.CreateLibraryAsync("Sync Test Library");
        library.IsSuccess.Should().BeTrue();

        SnapshotSyncLocalState localState = new(
            null,
            null,
            null,
            null,
            SnapshotSyncOperationState.Failed,
            "同步遇到致命错误",
            DateTimeOffset.UtcNow);

        PatchouliAppSettings appSettings = PatchouliAppSettings.Load(settings.Path);
        DeviceRootBindingAppSettings binding =
            appSettings.Sync.EnsureCurrentSyncRootBinding(library.Value.LibraryId) with
            {
                SnapshotState = localState
            };
        PatchouliAppSettings customSettings = appSettings with
        {
            Sync = appSettings.Sync.WithDeviceBinding(binding)
        };
        customSettings.Save(settings.Path).IsSuccess.Should().BeTrue();

        SnapshotViewModel snapshot = vm.Snapshot;
        List<string?> changed = [];
        snapshot.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await snapshot.RefreshAsync();

        snapshot.HasLastError.Should().BeTrue();
        snapshot.LastErrorText.Should().Contain("同步遇到致命错误");
        changed.Should().Contain(nameof(SnapshotViewModel.HasLastError));
        changed.Should().Contain(nameof(SnapshotViewModel.LastErrorText));
    }

    [Fact]
    public async Task Snapshot_check_incoming_and_confirm_apply_notifies_CanApply()
    {
        using TemporaryAppSettingsFile settings = new();
        using MainWindowViewModel vm = new(
            new FakeClipboard(),
            dialogs: new FakeDialogService(),
            settingsPath: settings.Path);

        SnapshotViewModel snapshot = vm.Snapshot;
        List<string?> changed = [];
        snapshot.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await snapshot.CheckCurrentCommand.ExecuteAsync(null);
        changed.Should().Contain(nameof(SnapshotViewModel.CanApply));
        changed.Should().Contain(nameof(SnapshotViewModel.HasIncomingPlan));

        changed.Clear();
        snapshot.ConfirmApply = true;
        snapshot.ConfirmApply.Should().BeTrue();
        changed.Should().Contain(nameof(SnapshotViewModel.ConfirmApply));
        changed.Should().Contain(nameof(SnapshotViewModel.CanApply));

        changed.Clear();
        snapshot.ConfirmApply = false;
        snapshot.ConfirmApply.Should().BeFalse();
        changed.Should().Contain(nameof(SnapshotViewModel.ConfirmApply));
        changed.Should().Contain(nameof(SnapshotViewModel.CanApply));
    }

    private sealed class FakeClipboard : IClipboardService
    {
        public string? Text { get; private set; }

        public Task<string?> GetTextAsync()
        {
            return Task.FromResult(Text);
        }

        public Task SetTextAsync(string text)
        {
            Text = text;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDialogService : IDialogService
    {
        public Task ShowDialogAsync(object viewModel)
        {
            return Task.CompletedTask;
        }

        public Task<TResult?> ShowDialogAsync<TResult>(object viewModel)
        {
            return Task.FromResult<TResult?>(default);
        }
    }
}
