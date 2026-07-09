using System.Collections.ObjectModel;
using Patchouli.Core.Csl;
using Patchouli.Core.Results;
using Patchouli.UI.ViewModels;

namespace Patchouli.UI.ViewModels.Csl;

public sealed class CslStyleManagerViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private string _searchQuery = "";
    private string _statusText = "就绪";
    private string? _defaultStyleId;
    private bool _loadingCatalogSources;
    private CslCatalogSourceViewModel? _selectedCatalogSource;

    public CslStyleManagerViewModel(MainWindowViewModel main)
    {
        _main = main;
        RefreshCommand = new AsyncCommand(RefreshAsync);
        SearchCommand = new AsyncCommand(SearchAsync);
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set { _searchQuery = value; Raise(); }
    }

    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; Raise(); }
    }

    public ObservableCollection<CslStyleViewModel> InstalledStyles { get; } = new();
    public ObservableCollection<CslCatalogStyleViewModel> RemoteStyles { get; } = new();
    public ObservableCollection<CslCatalogSourceViewModel> CatalogSources { get; } = new();

    public CslCatalogSourceViewModel? SelectedCatalogSource
    {
        get => _selectedCatalogSource;
        set
        {
            if (ReferenceEquals(_selectedCatalogSource, value))
            {
                return;
            }

            _selectedCatalogSource = value;
            Raise();
            if (!_loadingCatalogSources && value is not null)
            {
                _ = ChangeCatalogSourceAsync(value);
            }
        }
    }

    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand SearchCommand { get; }

    public async Task InitializeAsync()
    {
        await LoadInstalledStylesAsync();
        await LoadCatalogSourcesAsync();
    }

    private async Task LoadInstalledStylesAsync()
    {
        StatusText = "正在加载已安装样式...";
        var services = await _main.ServicesAsync();
        
        var settingsResult = await services.CslStore.GetSettingsAsync();
        if (settingsResult.IsSuccess)
        {
            _defaultStyleId = settingsResult.Value.DefaultStyleId;
        }

        var installedResult = await services.CslStore.ListInstalledStylesAsync();
        if (installedResult.IsSuccess)
        {
            InstalledStyles.Clear();
            foreach (var style in installedResult.Value.OrderBy(s => s.DisplayName))
            {
                InstalledStyles.Add(new CslStyleViewModel(style, this, _defaultStyleId == style.StyleId));
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
        var services = await _main.ServicesAsync();
        _loadingCatalogSources = true;
        try
        {
            CatalogSources.Clear();
            foreach (var source in services.CslCatalog.Sources)
            {
                CatalogSources.Add(new CslCatalogSourceViewModel(source));
            }

            _selectedCatalogSource = CatalogSources.FirstOrDefault(
                source => source.SourceId == services.CslCatalog.CurrentSource.SourceId);
            Raise(nameof(SelectedCatalogSource));
        }
        finally
        {
            _loadingCatalogSources = false;
        }
    }

    private async Task RefreshAsync()
    {
        StatusText = $"正在刷新远程索引：{SelectedCatalogSource?.DisplayName ?? "默认源"}...";
        var services = await _main.ServicesAsync();
        var refreshResult = await services.CslCatalog.RefreshAsync();
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
        StatusText = $"正在搜索远程样式：{SelectedCatalogSource?.DisplayName ?? "默认源"}...";
        var services = await _main.ServicesAsync();
        var searchResult = await services.CslCatalog.SearchAsync(string.IsNullOrWhiteSpace(SearchQuery) ? null : SearchQuery);
        
        if (searchResult.IsFailure)
        {
            StatusText = searchResult.ErrorMessage ?? "搜索失败。";
            return;
        }

        RemoteStyles.Clear();
        var installedIds = InstalledStyles.Select(s => s.StyleId).ToHashSet();
        foreach (var catalogStyle in searchResult.Value.OrderBy(s => s.DisplayName).Take(100))
        {
            RemoteStyles.Add(new CslCatalogStyleViewModel(catalogStyle, this, installedIds.Contains(catalogStyle.StyleId)));
        }
        StatusText = $"找到 {RemoteStyles.Count} 个远程样式。";
    }

    private async Task ChangeCatalogSourceAsync(CslCatalogSourceViewModel source)
    {
        var services = await _main.ServicesAsync();
        var result = services.CslCatalog.SetSource(source.SourceId);
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
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Patchouli/1.0");
            var xml = await client.GetStringAsync(catalogStyle.SourceUrl);
            var services = await _main.ServicesAsync();
            var result = await services.CslStore.InstallStyleAsync(catalogStyle, xml);
            
            if (result.IsSuccess)
            {
                StatusText = $"安装成功：{catalogStyle.DisplayName}";
                await LoadInstalledStylesAsync();
                
                // Update remote view
                var remote = RemoteStyles.FirstOrDefault(r => r.StyleId == catalogStyle.StyleId);
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
        var services = await _main.ServicesAsync();
        var result = await services.CslStore.SaveSettingsAsync(styleId, null);
        if (result.IsSuccess)
        {
            _defaultStyleId = styleId;
            foreach (var style in InstalledStyles)
            {
                style.IsDefault = style.StyleId == styleId;
            }
            StatusText = "默认样式已更新。";
        }
        else
        {
            StatusText = result.ErrorMessage ?? "更新默认样式失败。";
        }
    }

    internal async Task RemoveStyleAsync(string styleId)
    {
        var services = await _main.ServicesAsync();
        var result = await services.CslStore.RemoveStyleAsync(styleId);
        if (result.IsSuccess)
        {
            StatusText = "已移除样式。";
            await LoadInstalledStylesAsync();
            var remote = RemoteStyles.FirstOrDefault(r => r.StyleId == styleId);
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

public class CslCatalogSourceViewModel : ViewModelBase
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

    public override string ToString() => DisplayName;
}

public class CslStyleViewModel : ViewModelBase
{
    private readonly CslStyleManagerViewModel _parent;
    public string StyleId { get; }
    public string Title { get; }
    public string FormattedUpdated { get; }
    
    private bool _isDefault;
    public bool IsDefault
    {
        get => _isDefault;
        set { _isDefault = value; Raise(); Raise(nameof(IsNotDefault)); }
    }
    public bool IsNotDefault => !_isDefault;

    public AsyncCommand SetDefaultCommand { get; }
    public AsyncCommand RemoveCommand { get; }

    public CslStyleViewModel(CslStyle style, CslStyleManagerViewModel parent, bool isDefault)
    {
        _parent = parent;
        StyleId = style.StyleId;
        Title = style.DisplayName;
        FormattedUpdated = style.UpdatedAt.ToLocalTime().ToString("g");
        _isDefault = isDefault;

        SetDefaultCommand = new AsyncCommand(() => _parent.SetDefaultStyleAsync(StyleId));
        RemoveCommand = new AsyncCommand(() => _parent.RemoveStyleAsync(StyleId));
    }
}

public class CslCatalogStyleViewModel : ViewModelBase
{
    private readonly CslStyleManagerViewModel _parent;
    private readonly CslCatalogStyle _catalogStyle;

    public string StyleId => _catalogStyle.StyleId;
    public string Title => _catalogStyle.DisplayName;

    private bool _isInstalled;
    public bool IsInstalled
    {
        get => _isInstalled;
        set { _isInstalled = value; Raise(); Raise(nameof(IsNotInstalled)); }
    }
    public bool IsNotInstalled => !_isInstalled;

    public AsyncCommand InstallCommand { get; }

    public CslCatalogStyleViewModel(CslCatalogStyle catalogStyle, CslStyleManagerViewModel parent, bool isInstalled)
    {
        _catalogStyle = catalogStyle;
        _parent = parent;
        _isInstalled = isInstalled;
        InstallCommand = new AsyncCommand(() => _parent.InstallStyleAsync(_catalogStyle));
    }
}


