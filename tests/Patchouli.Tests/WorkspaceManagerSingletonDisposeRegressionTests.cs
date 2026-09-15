using System.Reflection;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Ocr;
using Patchouli.UI;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Settings;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public sealed class WorkspaceManagerSingletonDisposeRegressionTests : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _settings.Dispose();
    }

    private sealed class FakeClipboard : IClipboardService
    {
        public Task<string?> GetTextAsync()
        {
            return Task.FromResult<string?>(null);
        }

        public Task SetTextAsync(string text)
        {
            return Task.CompletedTask;
        }

        public Task ClearAsync()
        {
            return Task.CompletedTask;
        }
    }

    private MainWindowViewModel CreateMainWindow(string? dbPath = null)
    {
        MainWindowViewModel vm = new(new FakeClipboard(), settingsPath: _settings.Path);
        if (!string.IsNullOrWhiteSpace(dbPath))
        {
            vm.RuntimeDatabasePath = dbPath;
        }

        return vm;
    }

    private static bool IsViewModelDisposed(ViewModelBase viewModel)
    {
        FieldInfo field =
            typeof(ViewModelBase).GetField("_isDisposed", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (int)field.GetValue(viewModel)! == 1;
    }

    [Fact]
    public async Task Closing_ocr_queue_tab_does_not_dispose_singleton_and_maintains_reactivity_on_reopen()
    {
        MainWindowViewModel? main = null;
        try
        {
            string path = _settings.CreateDatabasePath("ocr-queue-singleton-dispose");
            main = CreateMainWindow(path);
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();

            OcrQueueViewModel queue = main.OcrQueue;

            // 1. 打开 OCR 队列 tab
            await main.OpenOcrQueueCommand.ExecuteAsync();
            main.Layout.Tabs.Should().Contain(t => t.Kind == WorkspaceTabKind.OcrQueue);
            main.Layout.ActiveTab?.Kind.Should().Be(WorkspaceTabKind.OcrQueue);
            IsViewModelDisposed(queue).Should().BeFalse();

            // 2. 关闭（CloseKind / Close）
            int closedCount = main.Workspace.CloseKind(WorkspaceTabKind.OcrQueue);
            closedCount.Should().Be(1);
            main.Layout.Tabs.Should().NotContain(t => t.Kind == WorkspaceTabKind.OcrQueue);

            // 3. 断言单例 OcrQueue 实例未被 Dispose
            IsViewModelDisposed(queue).Should().BeFalse();

            // 4. 重新打开
            await main.OpenOcrQueueCommand.ExecuteAsync();
            main.Layout.Tabs.Should().Contain(t => t.Kind == WorkspaceTabKind.OcrQueue);
            main.OcrQueue.Should().BeSameAs(queue);
            IsViewModelDisposed(queue).Should().BeFalse();

            // 5. 触发刷新（入队任务或调用 RefreshAsync 的路径）
            HostServices services = await main.ServicesAsync();
            IOcrQueueScheduler scheduler = (await services.GetOcrQueueAsync()).Value;
            Result<OcrQueueTask> enqueued = await scheduler.EnqueueMockPagesAsync(
                DocumentInstanceId.New(), OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument);
            enqueued.IsSuccess.Should().BeTrue();

            await main.OcrQueue.RefreshAsync();

            // 6. 断言 HasActiveTasks 变为 true（即派生属性订阅仍然存活）
            main.OcrQueue.ActiveTaskCount.Should().Be(1);
            main.OcrQueue.HasActiveTasks.Should().BeTrue();
            main.OcrQueue.NoActiveTasks.Should().BeFalse();
            main.OcrQueue.ActiveTabHeader.Should().Be("进行中 (1)");
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
    public async Task Closing_settings_and_snapshot_tabs_does_not_dispose_singletons()
    {
        MainWindowViewModel? main = null;
        try
        {
            string path = _settings.CreateDatabasePath("settings-snapshot-singleton-dispose");
            main = CreateMainWindow(path);
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();

            // Settings 单例
            SettingsViewModel settings = main.Settings;
            await main.OpenSettingsAsync("library");
            main.Layout.Tabs.Should().Contain(t => t.Kind == WorkspaceTabKind.Settings);
            IsViewModelDisposed(settings).Should().BeFalse();

            main.Workspace.CloseKind(WorkspaceTabKind.Settings).Should().Be(1);
            main.Layout.Tabs.Should().NotContain(t => t.Kind == WorkspaceTabKind.Settings);
            IsViewModelDisposed(settings).Should().BeFalse();

            // Snapshot/同步中心 单例
            SnapshotViewModel snapshot = main.Snapshot;
            await main.OpenSyncCenterAsync();
            main.Layout.Tabs.Should().Contain(t => t.Kind == WorkspaceTabKind.SyncCenter);
            IsViewModelDisposed(snapshot).Should().BeFalse();

            main.Workspace.CloseKind(WorkspaceTabKind.SyncCenter).Should().Be(1);
            main.Layout.Tabs.Should().NotContain(t => t.Kind == WorkspaceTabKind.SyncCenter);
            IsViewModelDisposed(snapshot).Should().BeFalse();
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
    public async Task Closing_pdf_workspace_tab_disposes_its_view_model()
    {
        MainWindowViewModel? main = null;
        try
        {
            string path = _settings.CreateDatabasePath("pdf-workspace-dispose");
            main = CreateMainWindow(path);
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();

            LibraryItemViewModel item = new(
                ItemId.New().ToString(), "Sample Book", "book", "", "", "", null, null, null, "sample.pdf", "", 0, 0,
                "",
                _ => Task.CompletedTask, _ => Task.CompletedTask);

            string tabId = $"PdfWorkspace_{item.ItemId}";
            WorkspaceTabViewModel tab = main.Workspace.OpenOrActivate(
                WorkspaceTabKind.PdfWorkspace,
                tabId,
                "PDF 工作台",
                "FolderOpen",
                true,
                () => new PdfWorkspaceViewModel(main, item));

            PdfWorkspaceViewModel pdfVm = (PdfWorkspaceViewModel)tab.Content;
            IsViewModelDisposed(pdfVm).Should().BeFalse();

            // Close tab using Workspace.Close
            bool closed = main.Workspace.Close(tabId);
            closed.Should().BeTrue();
            main.Layout.Tabs.Should().NotContain(t => t.TabId == tabId);

            // 断言 PdfWorkspaceViewModel 被 Dispose
            IsViewModelDisposed(pdfVm).Should().BeTrue();
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
