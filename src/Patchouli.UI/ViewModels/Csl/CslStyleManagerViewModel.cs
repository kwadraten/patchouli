using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Csl;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.UI.Diagnostics;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Settings;

namespace Patchouli.UI.ViewModels.Csl;

public sealed partial class CslStyleManagerViewModel : SettingsSectionViewModelBase
{
    private readonly MainWindowViewModel _main;
    private readonly bool _isConstructing;
    private string? _defaultStyleId;
    private bool _loadingCatalogSources;
    private HostServices? _installedServices;
    private HostServices? _catalogServices;
    private int _searchVersion;
    private CslCatalogSourceViewModel _selectedCatalogSource = null!;

    public CslStyleManagerViewModel(MainWindowViewModel main)
    {
        _isConstructing = true;
        _main = main;
        RefreshCommand = new AsyncCommand(RefreshAsync, () => SelectedCatalogSource is not null);
        SearchCommand = new AsyncCommand(SearchAsync, () => SelectedCatalogSource is not null);
        _isConstructing = false;
    }

    [ObservableProperty] public partial string SearchQuery { get; set; } = "";

    [ObservableProperty] public partial string StatusText { get; private set; } = "就绪";

    partial void OnStatusTextChanged(string value)
    {
        if (_isConstructing)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(value))
        {
            if (value.Contains("失败", StringComparison.Ordinal) || value.Contains("异常", StringComparison.Ordinal))
            {
                _main.ReportError(value);
            }
            else
            {
                _main.Report(value);
            }
        }
    }

    public ObservableCollection<CslStyleViewModel> InstalledStyles { get; } = new();
    public ObservableCollection<CslCatalogStyleViewModel> RemoteStyles { get; } = new();
    public ObservableCollection<CslCatalogSourceViewModel> CatalogSources { get; } = new();

    public CslCatalogSourceViewModel SelectedCatalogSource
    {
        get => _selectedCatalogSource;
        set
        {
            if (value is null || !SetProperty(ref _selectedCatalogSource, value))
            {
                return;
            }

            RefreshCommand.NotifyCanExecuteChanged();
            SearchCommand.NotifyCanExecuteChanged();
            if (!_loadingCatalogSources)
            {
                ChangeCatalogSourceAsync(value)
                    .Observe(nameof(CslStyleManagerViewModel), nameof(ChangeCatalogSourceAsync));
            }
        }
    }

    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand SearchCommand { get; }

    public override bool SupportsEditing => false;
    public override bool IsDirty => false;
    public override bool CanSave => false;

    public override async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        HostServices services = await _main.ServicesAsync();
        if (!ReferenceEquals(services, _installedServices))
        {
            await LoadInstalledStylesAsync();
        }
    }

    public async Task InitializeCatalogAsync()
    {
        await LoadAsync();
        HostServices services = await _main.ServicesAsync();
        if (!ReferenceEquals(services, _catalogServices))
        {
            _searchVersion++;
            RemoteStyles.Clear();
            await LoadCatalogSourcesAsync();
            _catalogServices = services;
        }
    }

    public override Task SaveAsync()
    {
        return Task.CompletedTask;
    }

    public override Task DiscardAsync()
    {
        return Task.CompletedTask;
    }

    public Task RefreshIfLoadedAsync()
    {
        return _installedServices is null ? Task.CompletedTask : LoadInstalledStylesAsync();
    }

    public Task RefreshInstalledStylesAsync()
    {
        return LoadInstalledStylesAsync();
    }

    private async Task LoadInstalledStylesAsync()
    {
        StatusText = "正在加载已安装样式...";
        HostServices services = await _main.ServicesAsync();

        Result<CslSettings> settingsResult = await services.CslStore.GetSettingsAsync();
        if (settingsResult.IsSuccess)
        {
            _defaultStyleId = settingsResult.Value.DefaultStyleId;
        }
        else
        {
            StatusText = settingsResult.ErrorMessage ?? "加载默认样式失败。";
            return;
        }

        Result<IReadOnlyList<CslStyle>> installedResult = await services.CslStore.ListInstalledStylesAsync();
        if (installedResult.IsSuccess)
        {
            InstalledStyles.Clear();
            foreach (CslStyle style in installedResult.Value.OrderBy(s => s.DisplayName))
            {
                InstalledStyles.Add(new CslStyleViewModel(style, this, _defaultStyleId == style.StyleId));
            }

            _installedServices = services;
            HashSet<string> installedIds = InstalledStyles.Select(style => style.StyleId).ToHashSet();
            foreach (CslCatalogStyleViewModel remote in RemoteStyles)
            {
                remote.IsInstalled = installedIds.Contains(remote.StyleId);
            }

            StatusText = $"已加载 {InstalledStyles.Count} 个本地样式。";
        }
        else
        {
            StatusText = installedResult.ErrorMessage ?? "加载本地样式失败。";
        }
    }

    private async Task LoadCatalogSourcesAsync()
    {
        HostServices services = await _main.ServicesAsync();
        _loadingCatalogSources = true;
        try
        {
            CatalogSources.Clear();
            foreach (CslCatalogSource source in services.CslCatalog.Sources)
            {
                CatalogSources.Add(new CslCatalogSourceViewModel(source));
            }

            SelectedCatalogSource =
                CatalogSources.FirstOrDefault(source => source.SourceId == services.CslCatalog.CurrentSource.SourceId)
                ?? CatalogSources.First();
        }
        finally
        {
            _loadingCatalogSources = false;
        }
    }

    private async Task RefreshAsync()
    {
        int searchVersion = ++_searchVersion;
        StatusText = $"正在刷新远程索引：{SelectedCatalogSource.DisplayName}...";
        HostServices services = await _main.ServicesAsync();
        Result<IReadOnlyList<CslCatalogStyle>> refreshResult = await services.CslCatalog.RefreshAsync();
        if (searchVersion != _searchVersion)
        {
            return;
        }

        if (refreshResult.IsFailure)
        {
            StatusText = refreshResult.ErrorMessage ?? "刷新远程索引失败。";
            return;
        }

        StatusText = "索引已刷新。";
        await SearchAsync();
    }

    private async Task SearchAsync()
    {
        int searchVersion = ++_searchVersion;
        StatusText = $"正在搜索远程样式：{SelectedCatalogSource.DisplayName}...";
        HostServices services = await _main.ServicesAsync();
        Result<IReadOnlyList<CslCatalogStyle>> searchResult =
            await services.CslCatalog.SearchAsync(string.IsNullOrWhiteSpace(SearchQuery) ? null : SearchQuery);

        if (searchVersion != _searchVersion)
        {
            return;
        }

        if (searchResult.IsFailure)
        {
            StatusText = searchResult.ErrorMessage ?? "搜索失败。";
            return;
        }

        RemoteStyles.Clear();
        HashSet<string> installedIds = InstalledStyles.Select(s => s.StyleId).ToHashSet();
        foreach (CslCatalogStyle catalogStyle in searchResult.Value.OrderBy(s => s.DisplayName).Take(100))
        {
            RemoteStyles.Add(new CslCatalogStyleViewModel(catalogStyle, this,
                installedIds.Contains(catalogStyle.StyleId)));
        }

        StatusText = $"找到 {RemoteStyles.Count} 个远程样式。";
    }

    private async Task ChangeCatalogSourceAsync(CslCatalogSourceViewModel source)
    {
        _searchVersion++;
        HostServices services = await _main.ServicesAsync();
        Result result = services.CslCatalog.SetSource(source.SourceId);
        if (result.IsFailure)
        {
            StatusText = result.ErrorMessage ?? "切换样式源失败。";
            return;
        }

        RemoteStyles.Clear();
        StatusText = $"已切换样式源：{source.DisplayName}。";
        await SearchAsync();
    }

    internal async Task InstallStyleAsync(CslCatalogStyle catalogStyle)
    {
        StatusText = $"正在下载并安装：{catalogStyle.DisplayName}...";
        if (string.IsNullOrWhiteSpace(catalogStyle.SourceUrl))
        {
            StatusText = "安装失败：样式源没有提供下载地址。";
            return;
        }

        try
        {
            using HttpClient client = new();
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"{Patchouli.Core.BuildInfo.AppName}/{Patchouli.Core.BuildInfo.Version}");
            string xml = await client.GetStringAsync(catalogStyle.SourceUrl);
            HostServices services = await _main.ServicesAsync();
            Result<CslStyle> result = await services.CslStore.InstallStyleAsync(catalogStyle, xml);

            if (result.IsSuccess)
            {
                StatusText = $"安装成功：{catalogStyle.DisplayName}";
                await LoadInstalledStylesAsync();

                // Update remote view
                CslCatalogStyleViewModel? remote = RemoteStyles.FirstOrDefault(r => r.StyleId == catalogStyle.StyleId);
                if (remote != null)
                {
                    remote.IsInstalled = true;
                }
            }
            else
            {
                StatusText = result.ErrorMessage ?? "安装失败。";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"安装异常：{ex.Message}";
        }
    }

    internal async Task SetDefaultStyleAsync(string styleId)
    {
        HostServices services = await _main.ServicesAsync();
        Result<CslSettings> result = await services.CslStore.SaveDefaultStyleAsync(styleId);
        if (result.IsSuccess)
        {
            _defaultStyleId = styleId;
            foreach (CslStyleViewModel style in InstalledStyles)
            {
                style.IsDefault = style.StyleId == styleId;
            }

            StatusText = "默认样式已更新。";
            await _main.RefreshCslPreviewsAsync();
        }
        else
        {
            StatusText = result.ErrorMessage ?? "更新默认样式失败。";
        }
    }

    internal async Task RemoveStyleAsync(string styleId)
    {
        HostServices services = await _main.ServicesAsync();
        Result result = await services.CslStore.RemoveStyleAsync(styleId);
        if (result.IsSuccess)
        {
            StatusText = "已移除样式。";
            await LoadInstalledStylesAsync();
            CslCatalogStyleViewModel? remote = RemoteStyles.FirstOrDefault(r => r.StyleId == styleId);
            if (remote != null)
            {
                remote.IsInstalled = false;
            }
        }
        else
        {
            StatusText = result.ErrorMessage ?? "移除失败。";
        }
    }
}

public sealed partial class CslCatalogSourceViewModel : ViewModelBase
{
    public string SourceId { get; }
    public string DisplayName { get; }
    public string Description { get; }

    public CslCatalogSourceViewModel(CslCatalogSource source)
    {
        SourceId = source.SourceId;
        DisplayName = source.DisplayName;
        Description = source.Description;
    }

    public override string ToString()
    {
        return DisplayName;
    }
}

public sealed partial class CslStyleViewModel : ViewModelBase
{
    private readonly CslStyleManagerViewModel _parent;
    public string StyleId { get; }
    public string Title { get; }
    public string FormattedUpdated { get; }

    [ObservableProperty] public partial bool IsDefault { get; set; }

    public bool IsNotDefault => !IsDefault;

    public AsyncCommand SetDefaultCommand { get; }
    public AsyncCommand RemoveCommand { get; }

    public CslStyleViewModel(CslStyle style, CslStyleManagerViewModel parent, bool isDefault)
    {
        _parent = parent;
        StyleId = style.StyleId;
        Title = style.DisplayName;
        FormattedUpdated = style.UpdatedAt.ToLocalTime().ToString("g");
        IsDefault = isDefault;

        SetDefaultCommand = new AsyncCommand(() => _parent.SetDefaultStyleAsync(StyleId));
        RemoveCommand = new AsyncCommand(() => _parent.RemoveStyleAsync(StyleId));
    }
}

public sealed partial class CslCatalogStyleViewModel : ViewModelBase
{
    private readonly CslStyleManagerViewModel _parent;
    private readonly CslCatalogStyle _catalogStyle;

    public string StyleId { get; }
    public string Title { get; }

    [ObservableProperty] public partial bool IsInstalled { get; set; }

    public bool IsNotInstalled => !IsInstalled;

    public AsyncCommand InstallCommand { get; }

    public CslCatalogStyleViewModel(CslCatalogStyle catalogStyle, CslStyleManagerViewModel parent, bool isInstalled)
    {
        _catalogStyle = catalogStyle;
        _parent = parent;
        StyleId = catalogStyle.StyleId;
        Title = catalogStyle.DisplayName;
        IsInstalled = isInstalled;
        InstallCommand = new AsyncCommand(() => _parent.InstallStyleAsync(_catalogStyle));
    }
}
