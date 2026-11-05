using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Host.Composition;
using Patchouli.Host.Settings;
using Patchouli.UI;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

[Collection("RetainedTabUI")]
public sealed class LibraryStartupLifecycleTests(AvaloniaSharedSessionFixture headless)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repeated_startup_is_shared_and_switch_or_shutdown_waits_for_initialization(bool shutdown)
    {
        await headless.Session.Dispatch<bool>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            BlockingSettingsStore store = new(settings.Path);
            await using MainWindowViewModel main = new(settingsPath: settings.Path, settingsStore: store,
                enforceRuntimeHostOwnership: true);
            store.Block = true;
            Task startup = main.RunStartupAsync(false);
            try
            {
                await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                main.RunStartupAsync(false).Should().BeSameAs(startup);
                main.IsStartupLoadingVisible.Should().BeTrue();
                Task stop = shutdown ? main.ShutdownAsync() : main.BeginLibrarySwitchAsync();
                stop.IsCompleted.Should().BeFalse();
                store.Release.TrySetResult();
                await startup.WaitAsync(TimeSpan.FromSeconds(30));
                await stop.WaitAsync(TimeSpan.FromSeconds(30));
                main.IsStartupLoadingVisible.Should().BeFalse();
                if (!shutdown)
                {
                    main.HasOpenRuntimeDatabase.Should().BeFalse();
                    await main.OpenDatabaseCommand.ExecuteAsync();
                    main.HasOpenRuntimeDatabase.Should().BeTrue();
                }
            }
            finally
            {
                store.Release.TrySetResult();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Host_open_upgrades_legacy_cache_before_returning()
    {
        using TemporaryAppSettingsFile settings = new();
        PatchouliAppSettings options = PatchouliAppSettings.Load(settings.Path);
        string path = options.Runtime.RuntimeDatabasePath;
        HostServices initial = await HostServices.CreateAsync(path, options, settings.Path);
        try
        {
            await using SqliteConnection connection = initial.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            await connection.ExecuteAsync("update fts_cache_state set cache_version = 1 where state_id = 1;");
        }
        finally
        {
            await initial.ShutdownAsync();
        }

        HostServices reopened = await HostServices.CreateAsync(path, options, settings.Path);
        try
        {
            await using SqliteConnection connection = reopened.ConnectionFactory.CreateReadConnection();
            await connection.OpenAsync();
            (await connection.ExecuteScalarAsync<int>("select cache_version from fts_cache_state;"))
                .Should().Be(2, "opening the host must complete the cache upgrade before startup finishes");
        }
        finally
        {
            await reopened.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }

        await using SqliteConnection writer = reopened.ConnectionFactory.CreateConnection();
        await writer.OpenAsync();
        await writer.ExecuteAsync("update fts_cache_state set cache_version = 1 where state_id = 1;");
    }

    [Fact]
    public async Task Rebuild_search_index_uses_blocking_dialog_with_phase_progress()
    {
        await headless.Session.Dispatch<bool>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            RecordingModalRunner modal = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path, modalOperations: modal,
                enforceRuntimeHostOwnership: true);
            await main.RunStartupAsync(false);

            await main.RebuildSearchIndexCommand.ExecuteAsync();

            modal.Options.Should().ContainSingle().Which.Title.Should().Be("重建搜索索引");
            UI.ViewModels.Dialogs.BlockingOperationDialogViewModel dialog =
                modal.Dialogs.Should().ContainSingle().Which;
            dialog.Logs.Should().Contain(log => log.Contains("正在准备 FTS 索引缓存", StringComparison.Ordinal));
            dialog.Logs.Should().Contain(log => log.Contains("正在清空旧 FTS 索引", StringComparison.Ordinal));
            dialog.Logs.Should().Contain(log => log.Contains("正在统计待重建的搜索单元", StringComparison.Ordinal));
            dialog.Logs.Should().Contain(log => log.Contains("没有需要写入的搜索单元", StringComparison.Ordinal));
            dialog.Logs.Should().Contain(log => log.Contains("正在提交 FTS 索引事务", StringComparison.Ordinal));
            dialog.Logs.Should().Contain(log => log.Contains("正在更新书库索引状态", StringComparison.Ordinal));
            dialog.Logs.Should().Contain(log => log.Contains("本地 FTS 搜索索引已重建", StringComparison.Ordinal));
            return true;
        }, CancellationToken.None);
    }

    private sealed class RecordingModalRunner : UI.Services.IModalOperationRunner
    {
        public List<UI.Services.ModalOperationOptions> Options { get; } = [];
        public List<UI.ViewModels.Dialogs.BlockingOperationDialogViewModel> Dialogs { get; } = [];

        public async Task<T> RunAsync<T>(UI.Services.ModalOperationOptions options,
            Func<UI.Services.ModalOperationContext, Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            Options.Add(options);
            UI.ViewModels.Dialogs.BlockingOperationDialogViewModel viewModel = new();
            Dialogs.Add(viewModel);
            UI.Services.ModalOperationContext context = new(viewModel, cancellationToken, false);
            T result = await Task.Run(() => operation(context), cancellationToken);
            context.ApplyPendingUpdates();
            return result;
        }
    }

    private sealed class BlockingSettingsStore(string path) : IAppSettingsStore
    {
        private readonly AppSettingsStore _inner = new(path);
        public bool Block { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PatchouliAppSettings Current => _inner.Current;
        public bool IsDirty => Block || _inner.IsDirty;

        public event EventHandler<AppSettingsCommittedEventArgs>? SettingsCommitted
        {
            add => _inner.SettingsCommitted += value;
            remove => _inner.SettingsCommitted -= value;
        }

        public void Update(Func<PatchouliAppSettings, PatchouliAppSettings> updater, string? fieldCategory = null)
        {
            _inner.Update(updater, fieldCategory);
        }

        public Task<SettingsSaveResult> UpdateSyncBindingAsync(SyncAppSettings settings,
            DeviceRootBindingAppSettings binding, CancellationToken cancellationToken = default)
        {
            return _inner.UpdateSyncBindingAsync(settings, binding, cancellationToken);
        }

        public Task<SettingsSaveResult> UpdateSyncBindingAndMetadataLookupAsync(SyncAppSettings settings,
            DeviceRootBindingAppSettings binding, MetadataLookupAppSettings metadataLookup,
            CancellationToken cancellationToken = default)
        {
            return _inner.UpdateSyncBindingAndMetadataLookupAsync(settings, binding, metadataLookup, cancellationToken);
        }

        public async Task<SettingsSaveResult> SaveImmediatelyAsync(CancellationToken cancellationToken = default)
        {
            if (Block)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                Block = false;
            }

            return await _inner.SaveImmediatelyAsync(cancellationToken);
        }

        public Task<SettingsSaveResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            return _inner.FlushAsync(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            return _inner.DisposeAsync();
        }
    }
}
