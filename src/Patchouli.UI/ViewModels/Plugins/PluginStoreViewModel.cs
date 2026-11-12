using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Host.Plugins;
using Patchouli.UI.ViewModels.Dialogs;
using Patchouli.UI.ViewModels.Settings;

namespace Patchouli.UI.ViewModels;

/// <summary>
/// The community-plugin half of the 插件与扩展管理 page: loads the signed store index and owns the
/// install / enable / uninstall operations. It keeps the settings-section base for its Status /
/// LastError / SaveState plumbing even though it no longer lives inside the settings page.
/// </summary>
public sealed partial class PluginStoreViewModel : SettingsSectionViewModelBase
{
    private readonly MainWindowViewModel _main;

    public PluginStoreViewModel(MainWindowViewModel main)
    {
        _main = main;
        RefreshCommand = new AsyncCommand(cancellationToken => RefreshAsync(true, cancellationToken));
    }

    public ObservableCollection<PluginStoreRowViewModel> Plugins { get; } = [];
    public ObservableCollection<ExtensionStoreRowViewModel> Extensions { get; } = [];
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

        foreach (ExtensionStoreRowViewModel extension in Extensions)
        {
            extension.NotifyBusyChanged();
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
        Status = force ? "正在刷新签名索引…" : "正在读取插件目录…";
        try
        {
            HostServices services = await _main.ServicesAsync();
            Result<PluginStoreSnapshot> result = await services.PluginStore.RefreshAsync(force, cancellationToken);
            if (result.IsFailure)
            {
                LastError = result.ErrorMessage;
                SaveState = SettingsSaveState.Failed;
                Status = $"插件目录不可用：{result.ErrorMessage}";
                return;
            }

            Plugins.Clear();
            foreach (PluginStoreItem item in result.Value.Items)
            {
                Plugins.Add(new PluginStoreRowViewModel(this, item));
            }

            Extensions.Clear();
            foreach (ExtensionStoreItem item in result.Value.Extensions)
            {
                Extensions.Add(new ExtensionStoreRowViewModel(this, item));
            }

            IsEmpty = Plugins.Count == 0 && Extensions.Count == 0;
            LastError = null;
            SaveState = SettingsSaveState.Clean;
            string cacheStatus = result.Value.FromCache ? "已验证缓存" : "在线索引";
            CatalogStatus = $"{cacheStatus} · 索引时间 {result.Value.GeneratedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
            Status = result.Value.Warning ?? $"已加载 {Plugins.Count} 个插件、{Extensions.Count} 个扩展。";
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            SaveState = SettingsSaveState.Failed;
            Status = $"插件目录加载失败：{exception.Message}";
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
                $"插件可调用宿主动词：{(plugin.RequestedHostVerbs.Count == 0 ? "无" : string.Join("、", plugin.RequestedHostVerbs))}\n" +
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

    internal async Task InstallExtensionAsync(ExtensionStoreRowViewModel row,
        CancellationToken cancellationToken)
    {
        ExtensionCatalogEntry extension = row.Item.Catalog;
        bool update = row.Item.InstalledVersion is not null;
        ConfirmDialogResult? choice = await _main.Dialogs.ShowDialogAsync<ConfirmDialogResult>(
            new ConfirmDialogViewModel(update ? "更新扩展？" : "安装扩展？",
                $"{extension.Name} {extension.Version}\n" +
                $"类型：{ExtensionManagerViewModel.KindLabel(extension.Kind)}\n" +
                $"来源：{extension.Repository}\n固定源码修订：{extension.SourceRevision}\n" +
                $"宿主 API：{extension.HostApi}\n\n" +
                "扩展是编译后在宿主进程内运行的可信代码，能访问当前用户可访问的文件和网络。索引签名与产物哈希用于验证来源和完整性，不是安全沙箱。",
                update ? "更新" : "安装"));
        if (choice != ConfirmDialogResult.Confirm)
        {
            return;
        }

        await RunOperationAsync(row, services =>
                services.PluginStore.InstallExtensionAsync(extension.Id, cancellationToken),
            update ? "扩展更新失败" : "扩展安装失败",
            $"已安装 {extension.Name} {extension.Version}。");
    }

    internal Task ToggleAsync(PluginStoreRowViewModel row, CancellationToken cancellationToken)
    {
        bool enabled = !row.Item.IsEnabled;
        return RunOperationAsync(row,
            services => services.PluginStore.SetEnabledAsync(row.Item.Catalog.Id, enabled, cancellationToken),
            "无法更改插件状态", enabled ? "插件已启用。" : "插件已停用。");
    }

    internal async Task UninstallAsync(PluginStoreRowViewModel row, CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            return;
        }

        PluginCatalogEntry plugin = row.Item.Catalog;
        int generation = _main.LibraryGeneration;
        string versions = string.Join("、", row.Item.InstalledVersions);
        ConfirmDialogResult? choice = await _main.Dialogs.ShowDialogAsync<ConfirmDialogResult>(
            new ConfirmDialogViewModel("卸载插件？",
                $"将卸载 {plugin.Name}，删除已安装版本" +
                (versions.Length > 0 ? $"（{versions}）" : "") +
                "与可回滚版本包及其包内数据。书库中的文献、题录、插件在书库中保存的数据以及版本目录之外的插件数据不会删除。",
                "卸载"));
        if (choice != ConfirmDialogResult.Confirm || IsBusy ||
            _main.LibraryGeneration != generation)
        {
            return;
        }

        IsBusy = true;
        Status = $"正在卸载 {plugin.Name}…";
        try
        {
            HostServices services = await _main.ServicesAsync();
            if (!_main.IsCurrentLibraryContext(services, generation))
            {
                return;
            }

            Result result = await Task.Run(
                () => services.PluginStore.UninstallAsync(plugin.Id, cancellationToken),
                cancellationToken);
            bool mountGone = await Task.Run(() => services.Plugins.FindMount(plugin.Id) is null);
            if (!_main.IsCurrentLibraryContext(services, generation))
            {
                return;
            }

            if (result.IsSuccess || mountGone)
            {
                await _main.ClosePluginViewsAsync(plugin.Id);
            }

            if (result.IsFailure)
            {
                LastError = result.ErrorMessage;
                SaveState = SettingsSaveState.Failed;
                Status = $"插件卸载失败：{result.ErrorMessage}";
                await RefreshAfterOperationAsync(services, Status);
                await _main.RefreshExtensionMenuAsync();
                return;
            }

            LastError = null;
            SaveState = SettingsSaveState.Clean;
            await RefreshAfterOperationAsync(services, $"已卸载 {plugin.Name}。");
            await _main.RefreshExtensionMenuAsync();
        }
        catch (Exception exception)
        {
            if (_main.LibraryGeneration == generation)
            {
                LastError = exception.Message;
                SaveState = SettingsSaveState.Failed;
                Status = $"插件卸载失败：{exception.Message}";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunOperationAsync(ExtensionManagerRowViewModel row,
        Func<HostServices, Task<Result>> operation, string failurePrefix, string successStatus)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Status = $"正在处理 {row.Name}…";
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

        Extensions.Clear();
        foreach (ExtensionStoreItem item in refreshed.Value.Extensions)
        {
            Extensions.Add(new ExtensionStoreRowViewModel(this, item));
        }

        IsEmpty = Plugins.Count == 0 && Extensions.Count == 0;
        Status = successStatus;
    }
}

/// <summary>One community-plugin card on the 插件与扩展管理 page.</summary>
public sealed class PluginStoreRowViewModel : ExtensionManagerRowViewModel
{
    private readonly PluginStoreViewModel _owner;

    internal PluginStoreRowViewModel(PluginStoreViewModel owner, PluginStoreItem item)
    {
        _owner = owner;
        Item = item;
        InstallCommand = new AsyncCommand(cancellationToken => owner.InstallAsync(this, cancellationToken));
        ToggleCommand = new AsyncCommand(cancellationToken => owner.ToggleAsync(this, cancellationToken));
        UninstallCommand = new AsyncCommand(cancellationToken => owner.UninstallAsync(this, cancellationToken));
    }

    internal PluginStoreItem Item { get; }
    public override bool IsBusy => _owner.IsBusy;

    internal void NotifyBusyChanged()
    {
        Raise(nameof(IsBusy));
    }

    public override ExtensionManagerRowCategory Category => ExtensionManagerRowCategory.Plugin;
    public override bool IsInstalled => Item.InstalledVersion is not null;
    public override string Name => Item.Catalog.Name;
    public override string Description => Item.Catalog.Description;
    public override string IdText => Item.Catalog.Id;
    public override string VersionText => $"版本 {Item.Catalog.Version} · {Item.Catalog.License}";

    public override string MetaText =>
        $"来源：{Item.Catalog.Repository} · 宿主调用：{string.Join("、", Item.Catalog.RequestedVerbs)} · 宿主动词：{(Item.Catalog.RequestedHostVerbs.Count == 0 ? "无" : string.Join("、", Item.Catalog.RequestedHostVerbs))} · 书库权限：无";

    public override IReadOnlyList<string> Tags =>
        Item is { InstalledVersion: not null, IsManaged: false }
            ? ["插件", "本地"]
            : ["插件", "社区"];

    public string Id => Item.Catalog.Id;
    public string SourceText => $"来源：{Item.Catalog.Repository}";
    public string PermissionsText => $"宿主调用：{string.Join("、", Item.Catalog.RequestedVerbs)} · 宿主动词：{(Item.Catalog.RequestedHostVerbs.Count == 0 ? "无" : string.Join("、", Item.Catalog.RequestedHostVerbs))} · 书库权限：无";

    public override bool ShowInstall => Item.UnavailableReason is null &&
                                        (Item.InstalledVersion is null || Item.UpdateAvailable);

    public override string InstallText => Item.UpdateAvailable ? "更新" : "安装";
    public override bool ShowToggle => Item.InstalledVersion is not null;
    public override string ToggleText => Item.IsEnabled ? "停用" : "启用";
    public override bool ShowUninstall => Item.IsManaged && Item.InstalledVersion is not null;

    public override string StatusText => Item.UnavailableReason ??
                                         (Item.InstalledVersion is null
                                             ? "尚未安装"
                                             : $"{Item.InstalledVersion} · {(Item.IsEnabled ? "已启用" : "已停用")}" +
                                               (Item.UpdateAvailable ? " · 有可用更新" : ""));

    public override AsyncCommand InstallCommand { get; }
    public override AsyncCommand ToggleCommand { get; }
    public override AsyncCommand UninstallCommand { get; }
}

/// <summary>One community-extension card on the 插件与扩展管理 page.</summary>
public sealed class ExtensionStoreRowViewModel : ExtensionManagerRowViewModel
{
    private readonly PluginStoreViewModel _owner;

    internal ExtensionStoreRowViewModel(PluginStoreViewModel owner, ExtensionStoreItem item)
    {
        _owner = owner;
        Item = item;
        InstallCommand = new AsyncCommand(cancellationToken =>
            owner.InstallExtensionAsync(this, cancellationToken));
    }

    internal ExtensionStoreItem Item { get; }
    public override bool IsBusy => _owner.IsBusy;

    internal void NotifyBusyChanged()
    {
        Raise(nameof(IsBusy));
    }

    public override ExtensionManagerRowCategory Category => ExtensionManagerRowCategory.Extension;
    public override bool IsInstalled => Item.InstalledVersion is not null;
    public override string Name => Item.Catalog.Name;
    public override string Description => Item.Catalog.Description;
    public override string IdText => Item.Catalog.Id;
    public override string VersionText => $"版本 {Item.Catalog.Version}";

    public override string MetaText =>
        $"来源：{Item.Catalog.Repository} · 类型：{ExtensionManagerViewModel.KindLabel(Item.Catalog.Kind)} · 宿主 API：{Item.Catalog.HostApi}";

    public override IReadOnlyList<string> Tags =>
        ["扩展", "社区", ExtensionManagerViewModel.KindLabel(Item.Catalog.Kind)];

    public override bool ShowInstall => Item.InstalledVersion is null || Item.UpdateAvailable;
    public override string InstallText => Item.UpdateAvailable ? "更新" : "安装";
    public override bool ShowToggle => false;
    public override string ToggleText => "";
    public override bool ShowUninstall => false;

    public override string StatusText => Item.InstalledVersion is null
        ? "尚未安装"
        : $"{Item.InstalledVersion} · 已安装" + (Item.UpdateAvailable ? " · 有可用更新" : "");

    public override AsyncCommand InstallCommand { get; }
    public override AsyncCommand ToggleCommand { get; } = new(() => Task.CompletedTask);
    public override AsyncCommand UninstallCommand { get; } = new(() => Task.CompletedTask);
}
