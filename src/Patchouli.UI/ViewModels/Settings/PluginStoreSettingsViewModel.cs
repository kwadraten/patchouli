using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Host.Plugins;
using Patchouli.UI.ViewModels.Core;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.ViewModels.Settings;

public sealed partial class PluginStoreSettingsViewModel : SettingsSectionViewModelBase
{
    private readonly MainWindowViewModel _main;

    public PluginStoreSettingsViewModel(MainWindowViewModel main)
    {
        _main = main;
        RefreshCommand = new AsyncCommand(cancellationToken => RefreshAsync(true, cancellationToken));
    }

    public ObservableCollection<PluginStoreRowViewModel> Plugins { get; } = [];
    public AsyncCommand RefreshCommand { get; }
    [ObservableProperty] public partial bool IsBusy { get; private set; }
    [ObservableProperty] public partial string CatalogStatus { get; private set; } = "";
    [ObservableProperty] public partial bool IsEmpty { get; private set; }

    partial void OnIsBusyChanged(bool value)
    {
        foreach (PluginStoreRowViewModel plugin in Plugins)
        {
            plugin.NotifyBusyChanged();
        }
    }

    public override bool SupportsEditing => false;
    public override bool IsDirty => false;
    public override bool CanSave => false;

    public override Task LoadAsync(CancellationToken cancellationToken = default)
    {
        return RefreshAsync(false, cancellationToken);
    }

    public override Task SaveAsync()
    {
        return Task.CompletedTask;
    }

    public override Task DiscardAsync()
    {
        return Task.CompletedTask;
    }

    private async Task RefreshAsync(bool force, CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Status = force ? "正在刷新签名索引…" : "正在读取插件商店…";
        try
        {
            HostServices services = await _main.ServicesAsync();
            Result<PluginStoreSnapshot> result = await services.PluginStore.RefreshAsync(force, cancellationToken);
            if (result.IsFailure)
            {
                LastError = result.ErrorMessage;
                SaveState = SettingsSaveState.Failed;
                Status = $"插件商店不可用：{result.ErrorMessage}";
                return;
            }

            Plugins.Clear();
            foreach (PluginStoreItem item in result.Value.Items)
            {
                Plugins.Add(new PluginStoreRowViewModel(this, item));
            }

            IsEmpty = Plugins.Count == 0;
            LastError = null;
            SaveState = SettingsSaveState.Clean;
            string cacheStatus = result.Value.FromCache ? "已验证缓存" : "在线索引";
            CatalogStatus = $"{cacheStatus} · 索引时间 {result.Value.GeneratedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
            Status = result.Value.Warning ?? $"已加载 {Plugins.Count} 个插件。";
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            SaveState = SettingsSaveState.Failed;
            Status = $"插件商店加载失败：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal async Task InstallAsync(PluginStoreRowViewModel row, CancellationToken cancellationToken)
    {
        PluginCatalogEntry plugin = row.Item.Catalog;
        bool update = row.Item.InstalledVersion is not null;
        ConfirmDialogResult? choice = await _main.Dialogs.ShowDialogAsync<ConfirmDialogResult>(
            new ConfirmDialogViewModel(update ? "更新插件？" : "安装插件？",
                $"{plugin.Name} {plugin.Version}\n" +
                $"来源：{plugin.Repository}\n固定源码修订：{plugin.SourceRevision}\n" +
                $"允许通过宿主调用：{string.Join("、", plugin.RequestedVerbs)}\n" +
                "书库权限：无。\n\n" +
                "插件以独立进程运行，但仍能访问当前用户可访问的文件和网络。索引签名与产物哈希用于验证来源和完整性，不是安全沙箱。",
                update ? "更新" : "安装"));
        if (choice != ConfirmDialogResult.Confirm)
        {
            return;
        }

        await RunOperationAsync(row, async services =>
            {
                Result<PluginDefinition> result = row.Item.InstalledVersions.Contains(plugin.Version,
                    StringComparer.Ordinal)
                    ? await services.PluginStore.ActivateAsync(plugin.Id, plugin.Version, cancellationToken)
                    : await services.PluginStore.InstallAsync(plugin.Id, cancellationToken);
                return result.IsSuccess
                    ? Result.Success()
                    : Result.Failure(result.ErrorCode!, result.ErrorMessage!);
            }, update ? "插件更新失败" : "插件安装失败", $"已安装 {plugin.Name} {plugin.Version}。");
    }

    internal Task ToggleAsync(PluginStoreRowViewModel row, CancellationToken cancellationToken)
    {
        bool enabled = !row.Item.IsEnabled;
        return RunOperationAsync(row,
            services => services.PluginStore.SetEnabledAsync(row.Item.Catalog.Id, enabled, cancellationToken),
            "无法更改插件状态", enabled ? "插件已启用。" : "插件已停用。");
    }

    internal async Task RollbackAsync(PluginStoreRowViewModel row, CancellationToken cancellationToken)
    {
        string? version = row.Item.RollbackVersion;
        if (version is null)
        {
            return;
        }

        ConfirmDialogResult? choice = await _main.Dialogs.ShowDialogAsync<ConfirmDialogResult>(
            new ConfirmDialogViewModel("回滚插件？",
                $"将 {row.Item.Catalog.Name} 切换到本机保留的 {version}。已提交的题录和插件自有数据不会回滚。",
                "回滚"));
        if (choice != ConfirmDialogResult.Confirm)
        {
            return;
        }

        await RunOperationAsync(row, async services =>
            {
                Result<PluginDefinition> result = await services.PluginStore.ActivateAsync(row.Item.Catalog.Id,
                    version, cancellationToken);
                return result.IsSuccess
                    ? Result.Success()
                    : Result.Failure(result.ErrorCode!, result.ErrorMessage!);
            }, "插件回滚失败", $"已回滚到 {version}。");
    }

    private async Task RunOperationAsync(PluginStoreRowViewModel row,
        Func<HostServices, Task<Result>> operation, string failurePrefix, string successStatus)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Status = $"正在处理 {row.Item.Catalog.Name}…";
        try
        {
            HostServices services = await _main.ServicesAsync();
            Result result = await operation(services);
            if (result.IsFailure)
            {
                LastError = result.ErrorMessage;
                SaveState = SettingsSaveState.Failed;
                Status = $"{failurePrefix}：{result.ErrorMessage}";
                return;
            }

            LastError = null;
            SaveState = SettingsSaveState.Clean;
            await RefreshAfterOperationAsync(services, successStatus);
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            SaveState = SettingsSaveState.Failed;
            Status = $"{failurePrefix}：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshAfterOperationAsync(HostServices services, string successStatus)
    {
        Result<PluginStoreSnapshot> refreshed = await services.PluginStore.RefreshAsync(false);
        if (refreshed.IsFailure)
        {
            Status = $"{successStatus} 但列表刷新失败：{refreshed.ErrorMessage}";
            return;
        }

        Plugins.Clear();
        foreach (PluginStoreItem item in refreshed.Value.Items)
        {
            Plugins.Add(new PluginStoreRowViewModel(this, item));
        }

        IsEmpty = Plugins.Count == 0;
        Status = successStatus;
    }
}

public sealed class PluginStoreRowViewModel : ViewModelBase
{
    private readonly PluginStoreSettingsViewModel _owner;

    internal PluginStoreRowViewModel(PluginStoreSettingsViewModel owner, PluginStoreItem item)
    {
        _owner = owner;
        Item = item;
        InstallCommand = new AsyncCommand(cancellationToken => owner.InstallAsync(this, cancellationToken));
        ToggleCommand = new AsyncCommand(cancellationToken => owner.ToggleAsync(this, cancellationToken));
        RollbackCommand = new AsyncCommand(cancellationToken => owner.RollbackAsync(this, cancellationToken));
    }

    internal PluginStoreItem Item { get; }
    public bool IsBusy => _owner.IsBusy;

    internal void NotifyBusyChanged()
    {
        Raise(nameof(IsBusy));
    }

    public string Name => Item.Catalog.Name;
    public string Description => Item.Catalog.Description;
    public string Id => Item.Catalog.Id;
    public string VersionText => $"版本 {Item.Catalog.Version} · {Item.Catalog.License}";
    public string SourceText => $"来源：{Item.Catalog.Repository}";
    public string PermissionsText => $"宿主调用：{string.Join("、", Item.Catalog.RequestedVerbs)} · 书库权限：无";

    public bool ShowInstall => Item.UnavailableReason is null &&
                               (Item.InstalledVersion is null || Item.UpdateAvailable);

    public string InstallText => Item.UpdateAvailable ? "更新" : "安装";
    public bool ShowToggle => Item.InstalledVersion is not null;
    public string ToggleText => Item.IsEnabled ? "停用" : "启用";
    public bool ShowRollback => Item.RollbackVersion is not null;
    public string RollbackText => $"回滚到 {Item.RollbackVersion}";

    public string StatusText => Item.UnavailableReason ??
                                (Item.InstalledVersion is null
                                    ? "尚未安装"
                                    : $"{Item.InstalledVersion} · {(Item.IsEnabled ? "已启用" : "已停用")}" +
                                      (Item.UpdateAvailable ? " · 有可用更新" : ""));

    public AsyncCommand InstallCommand { get; }
    public AsyncCommand ToggleCommand { get; }
    public AsyncCommand RollbackCommand { get; }
}
