using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Composition;
using Patchouli.UI.Diagnostics;
using Patchouli.UI.ViewModels.Settings;
using Patchouli.UI.Views;

namespace Patchouli.UI.ViewModels;

/// <summary>The category a card on the 插件与扩展管理 page belongs to.</summary>
public enum ExtensionManagerRowCategory
{
    Plugin,
    Extension
}

/// <summary>The 全部 / 已安装 / 未安装 install-state filter.</summary>
public enum ExtensionManagerInstallFilter
{
    All,
    Installed,
    NotInstalled
}

/// <summary>The 全部 / 插件 / 扩展 category filter.</summary>
public enum ExtensionManagerCategoryFilter
{
    All,
    Plugin,
    Extension
}

/// <summary>
/// The standalone 插件与扩展管理 page: community plugins from the signed store index and the FSX
/// extension packages shown as one searchable card list (Zed extensions-tab style), filterable by
/// install state (全部 / 已安装 / 未安装) and category (插件 / 扩展).
/// </summary>
public sealed partial class ExtensionManagerViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private long _loadGeneration;
    private CancellationTokenSource? _loadCancellation;

    public ExtensionManagerViewModel(MainWindowViewModel main)
    {
        _main = main;
        PluginStore = new PluginStoreViewModel(main);
        Register(PluginStore);
        RefreshCommand = new AsyncCommand(RefreshAllAsync);
        PluginStore.PropertyChanged += OnPluginStorePropertyChanged;
        PluginStore.Plugins.CollectionChanged += (_, _) => RebuildVisibleRows();
        InstallFilters =
        [
            Option("全部", () => SelectInstallFilter(ExtensionManagerInstallFilter.All), true),
            Option("已安装", () => SelectInstallFilter(ExtensionManagerInstallFilter.Installed)),
            Option("未安装", () => SelectInstallFilter(ExtensionManagerInstallFilter.NotInstalled))
        ];
        CategoryFilters =
        [
            Option("全部", () => SelectCategoryFilter(ExtensionManagerCategoryFilter.All), true),
            Option("插件", () => SelectCategoryFilter(ExtensionManagerCategoryFilter.Plugin)),
            Option("扩展", () => SelectCategoryFilter(ExtensionManagerCategoryFilter.Extension))
        ];
    }

    private static ExtensionFilterOptionViewModel Option(string label, Action select,
        bool selected = false)
    {
        return new ExtensionFilterOptionViewModel(label, select) { IsSelected = selected };
    }

    /// <summary>The community-plugin store inventory and its install / enable / uninstall operations.</summary>
    public PluginStoreViewModel PluginStore { get; }

    /// <summary>The FSX extension packages discovered from generations, working sources and bundles.</summary>
    public ObservableCollection<ExtensionRowViewModel> Extensions { get; } = [];

    /// <summary>The filtered, sorted union of plugin and extension cards the page renders.</summary>
    [ExcludeFromDerivedGeneration]
    public ObservableCollection<ExtensionManagerRowViewModel> VisibleRows { get; } = [];

    public IReadOnlyList<ExtensionFilterOptionViewModel> InstallFilters { get; }
    public IReadOnlyList<ExtensionFilterOptionViewModel> CategoryFilters { get; }
    public AsyncCommand RefreshCommand { get; }

    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial bool ExtensionsBusy { get; private set; }

    /// <summary>Load-side status for the extension inventory, combined with the store status.</summary>
    [ObservableProperty]
    public partial string ExtensionStatus { get; private set; } = "";

    /// <summary>The active 全部 / 已安装 / 未安装 filter, mirrored by <see cref="InstallFilters"/>.</summary>
    [ObservableProperty]
    public partial ExtensionManagerInstallFilter InstallFilter { get; private set; } =
        ExtensionManagerInstallFilter.All;

    /// <summary>The active 全部 / 插件 / 扩展 filter, mirrored by <see cref="CategoryFilters"/>.</summary>
    [ObservableProperty]
    public partial ExtensionManagerCategoryFilter CategoryFilter { get; private set; } =
        ExtensionManagerCategoryFilter.All;

    [ExcludeFromDerivedGeneration] public bool IsBusy => PluginStore.IsBusy || ExtensionsBusy;

    [ExcludeFromDerivedGeneration]
    public string Status =>
        string.Join(" · ", new[] { PluginStore.Status, ExtensionStatus }
            .Where(text => !string.IsNullOrWhiteSpace(text)));

    [ExcludeFromDerivedGeneration] public string CatalogStatus => PluginStore.CatalogStatus;
    [ExcludeFromDerivedGeneration] public bool HasNoRows => VisibleRows.Count == 0;

    /// <summary>Test seam: replaces the modal <see cref="ExtensionConfigurationDialog"/> host.</summary>
    internal Func<ExtensionSettingsViewModel, Task>? ExtensionEditorShower { get; set; }

    /// <summary>Test seam: supplies the dialog owner when no real window exists.</summary>
    internal Func<Window?>? OwnerWindowResolver { get; set; }

    partial void OnSearchTextChanged(string value)
    {
        RebuildVisibleRows();
    }

    partial void OnExtensionsBusyChanged(bool value)
    {
        Raise(nameof(IsBusy));
    }

    partial void OnExtensionStatusChanged(string value)
    {
        Raise(nameof(Status));
    }

    partial void OnInstallFilterChanged(ExtensionManagerInstallFilter value)
    {
        RebuildVisibleRows();
    }

    partial void OnCategoryFilterChanged(ExtensionManagerCategoryFilter value)
    {
        RebuildVisibleRows();
    }

    private void OnPluginStorePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(PluginStoreViewModel.IsBusy))
        {
            Raise(nameof(IsBusy));
        }
        else if (args.PropertyName is nameof(PluginStoreViewModel.Status) or
                 nameof(PluginStoreViewModel.CatalogStatus))
        {
            Raise(nameof(Status));
            Raise(nameof(CatalogStatus));
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await Task.WhenAll(PluginStore.LoadAsync(cancellationToken), LoadExtensionsAsync(cancellationToken));
    }

    private async Task RefreshAllAsync()
    {
        await Task.WhenAll(PluginStore.RefreshCommand.ExecuteAsync(), LoadExtensionsAsync());
    }

    private void SelectInstallFilter(ExtensionManagerInstallFilter filter)
    {
        InstallFilter = filter;
        UpdateOptionSelection(InstallFilters, (int)filter);
    }

    private void SelectCategoryFilter(ExtensionManagerCategoryFilter filter)
    {
        CategoryFilter = filter;
        UpdateOptionSelection(CategoryFilters, (int)filter);
    }

    private static void UpdateOptionSelection(IReadOnlyList<ExtensionFilterOptionViewModel> options,
        int selected)
    {
        for (int i = 0; i < options.Count; i++)
        {
            options[i].IsSelected = i == selected;
        }
    }

    internal bool Matches(ExtensionManagerRowViewModel row)
    {
        if (CategoryFilter == ExtensionManagerCategoryFilter.Plugin &&
            row.Category != ExtensionManagerRowCategory.Plugin)
        {
            return false;
        }

        if (CategoryFilter == ExtensionManagerCategoryFilter.Extension &&
            row.Category != ExtensionManagerRowCategory.Extension)
        {
            return false;
        }

        if (InstallFilter == ExtensionManagerInstallFilter.Installed && !row.IsInstalled)
        {
            return false;
        }

        if (InstallFilter == ExtensionManagerInstallFilter.NotInstalled && row.IsInstalled)
        {
            return false;
        }

        return row.MatchesSearch(SearchText.Trim());
    }

    internal void RebuildVisibleRows()
    {
        List<ExtensionManagerRowViewModel> rows = PluginStore.Plugins
            .Cast<ExtensionManagerRowViewModel>()
            .Concat(Extensions)
            .Where(Matches)
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        VisibleRows.Clear();
        foreach (ExtensionManagerRowViewModel row in rows)
        {
            VisibleRows.Add(row);
        }

        Raise(nameof(HasNoRows));
    }

    private async Task LoadExtensionsAsync(CancellationToken cancellationToken = default)
    {
        long generation = ++_loadGeneration;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken token = _loadCancellation.Token;
        ExtensionsBusy = true;
        try
        {
            HostServices services = await _main.ServicesAsync();
            int libraryGeneration = _main.LibraryGeneration;
            List<ExtensionRowViewModel> rows =
                await Task.Run(() => BuildExtensionRowsAsync(services, token), token);
            if (generation != Volatile.Read(ref _loadGeneration) ||
                !_main.IsCurrentLibraryContext(services, libraryGeneration))
            {
                return;
            }

            Extensions.Clear();
            foreach (ExtensionRowViewModel row in rows)
            {
                Extensions.Add(row);
            }

            ExtensionStatus = $"已加载 {Extensions.Count} 个扩展。";
            RebuildVisibleRows();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A newer load or the page refresh superseded this one.
        }
        catch (Exception exception)
        {
            if (generation == Volatile.Read(ref _loadGeneration))
            {
                UnexpectedExceptions.Sink.Report(exception, "extension-manager",
                    nameof(LoadExtensionsAsync));
                ExtensionStatus = $"扩展加载失败：{exception.Message}";
            }
        }
        finally
        {
            if (generation == Volatile.Read(ref _loadGeneration))
            {
                ExtensionsBusy = false;
            }
        }
    }

    /// <summary>
    /// Enumerates every FSX package worth showing — active generations first, then unactivated
    /// working sources, then bundled packages never activated — with display metadata read from
    /// the same working-source projection the extension editor uses (never a check or a run).
    /// </summary>
    private async Task<List<ExtensionRowViewModel>> BuildExtensionRowsAsync(HostServices services,
        CancellationToken cancellationToken)
    {
        IExtensionManager manager = services.TranslationExtensions.Manager;
        IReadOnlyList<ExtensionPackageSnapshot> bundled = services.TranslationExtensions.BundledPackageSnapshots;
        List<ExtensionRowViewModel> rows = [];
        HashSet<string> listed = new(StringComparer.Ordinal);
        foreach (ExtensionGenerationInfo generation in manager.List().Where(info => info.IsActive))
        {
            ExtensionPackageSnapshot? source = await ReadRowSourceAsync(manager, bundled, generation.Id,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(source is null
                ? ExtensionRowViewModel.Unavailable(this, generation.Id, "已激活")
                : CreateExtensionRow(services, source, generation.Fingerprint, true));
            listed.Add(generation.Id);
        }

        foreach (string id in services.TranslationExtensions.ListWorkingPackages()
                     .Where(id => !listed.Contains(id)))
        {
            ExtensionPackageSnapshot? source = await ReadRowSourceAsync(manager, bundled, id,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (source is not null)
            {
                rows.Add(CreateExtensionRow(services, source, source.Fingerprint, false));
            }

            listed.Add(id);
        }

        foreach (ExtensionPackageSnapshot package in bundled.Where(package => !listed.Contains(package.Id)))
        {
            rows.Add(CreateExtensionRow(services, package, package.Fingerprint, false));
        }

        return rows;
    }

    private static async Task<ExtensionPackageSnapshot?> ReadRowSourceAsync(IExtensionManager manager,
        IReadOnlyList<ExtensionPackageSnapshot> bundled, string id, CancellationToken cancellationToken)
    {
        try
        {
            return await ExtensionSettingsViewModel.ReadDisplaySourceAsync(manager, bundled, id,
                cancellationToken);
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException or FileNotFoundException)
        {
            // A package directory deleted mid-enumeration still deserves a visible, configurable row.
            return null;
        }
    }

    private ExtensionRowViewModel CreateExtensionRow(HostServices services,
        ExtensionPackageSnapshot package, string fingerprint, bool active)
    {
        bool builtIn = services.TranslationExtensions.IsBuiltIn(package.Id);
        try
        {
            ExtensionEntryMetadata metadata = ExtensionEntryMetadata.Parse(package);
            string status = active
                ? package.Fingerprint == fingerprint ? "已激活" : "已激活 · 工作源码不同"
                : "未激活";
            return new ExtensionRowViewModel(this, metadata.Id, metadata.Name,
                $"版本 {metadata.Version}", metadata.Kind, metadata.Summary, status, builtIn);
        }
        catch (ExtensionMetadataException error)
        {
            return ExtensionRowViewModel.Invalid(this, package.Id, error.Message,
                active ? "已激活" : "未激活", builtIn);
        }
    }

    internal async Task ConfigureExtensionAsync(ExtensionRowViewModel row)
    {
        using ExtensionSettingsViewModel editor = _main.Settings.CreateExtensionEditor();
        await editor.OpenAsync(row.ExtensionId);
        if (ExtensionEditorShower is { } shower)
        {
            await shower(editor);
            await LoadExtensionsAsync();
            return;
        }

        Window? owner = OwnerWindowResolver?.Invoke() ?? ResolveOwnerWindow();
        if (owner is null)
        {
            ExtensionStatus = "找不到可用的窗口来打开扩展配置。";
            return;
        }

        await new ExtensionConfigurationDialog(editor).ShowDialog(owner);
        await LoadExtensionsAsync();
    }

    private static Window? ResolveOwnerWindow()
    {
        return Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;
    }

    internal static string KindLabel(ExtensionKind kind)
    {
        return kind switch
        {
            ExtensionKind.Translation => "翻译",
            ExtensionKind.Metadata => "元数据",
            ExtensionKind.Ocr => "OCR",
            ExtensionKind.Retrieval => "检索",
            ExtensionKind.PdfTextLayer => "文本层",
            ExtensionKind.LayoutRecognition => "版面识别",
            _ => "扩展"
        };
    }
}

/// <summary>
/// Base type for one card on the 插件与扩展管理 page. Concrete rows are
/// <see cref="PluginStoreRowViewModel"/> (community plugins) and <see cref="ExtensionRowViewModel"/>
/// (FSX packages); a single data template on this type renders both.
/// </summary>
public abstract class ExtensionManagerRowViewModel : ViewModelBase
{
    public abstract ExtensionManagerRowCategory Category { get; }
    public abstract bool IsInstalled { get; }
    public abstract string Name { get; }
    public abstract string IdText { get; }
    public virtual string Description => "";
    public virtual string VersionText => "";
    public virtual string MetaText => "";
    public virtual string StatusText => "";
    public virtual IReadOnlyList<string> Tags => [];
    public virtual bool IsBusy => false;
    public virtual bool ShowInstall => false;
    public virtual string InstallText => "";
    public virtual string InstallIcon => InstallText == "更新" ? "RefreshCw" : "Download";
    public virtual AsyncCommand? InstallCommand => null;
    public virtual bool ShowToggle => false;
    public virtual string ToggleText => "";
    public virtual AsyncCommand? ToggleCommand => null;
    public virtual bool ShowUninstall => false;
    public virtual AsyncCommand? UninstallCommand => null;
    public virtual bool ShowConfigure => false;
    public virtual AsyncCommand? ConfigureCommand => null;

    internal bool MatchesSearch(string search)
    {
        return search.Length == 0 ||
               Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               IdText.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               Description.Contains(search, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>One FSX extension package card on the 插件与扩展管理 page.</summary>
public sealed class ExtensionRowViewModel : ExtensionManagerRowViewModel
{
    internal ExtensionRowViewModel(ExtensionManagerViewModel owner, string id, string name,
        string versionText, ExtensionKind? kind, string summary, string statusText, bool builtIn)
    {
        ExtensionId = id;
        Name = name;
        VersionText = versionText;
        Kind = kind;
        Description = summary;
        StatusText = statusText;
        List<string> tags = ["扩展"];
        if (kind is { } kindValue)
        {
            tags.Add(ExtensionManagerViewModel.KindLabel(kindValue));
        }

        tags.Add(builtIn ? "内置" : "自定义");
        Tags = tags;
        ConfigureCommand = new AsyncCommand(() => owner.ConfigureExtensionAsync(this));
    }

    /// <summary>The package id the configuration editor opens.</summary>
    public string ExtensionId { get; }

    /// <summary>The declared package kind; null when the metadata block failed to parse.</summary>
    public ExtensionKind? Kind { get; }

    public override ExtensionManagerRowCategory Category => ExtensionManagerRowCategory.Extension;
    public override bool IsInstalled => true;
    public override string Name { get; }
    public override string IdText => ExtensionId;
    public override string Description { get; }
    public override string VersionText { get; }
    public override string StatusText { get; }
    public override IReadOnlyList<string> Tags { get; }
    public override bool ShowConfigure => true;
    public override AsyncCommand ConfigureCommand { get; }

    /// <summary>A package whose source could not be read still shows so it can be repaired.</summary>
    internal static ExtensionRowViewModel Unavailable(ExtensionManagerViewModel owner, string id,
        string statusText)
    {
        return new ExtensionRowViewModel(owner, id, id + "（源码不可读）", "", null, "", statusText,
            false);
    }

    /// <summary>A package whose /// metadata block failed to parse shows the parse error.</summary>
    internal static ExtensionRowViewModel Invalid(ExtensionManagerViewModel owner, string id,
        string message, string statusText, bool builtIn)
    {
        return new ExtensionRowViewModel(owner, id, id + "（元数据无效）", "", null, message,
            statusText, builtIn);
    }
}

/// <summary>One toggle chip in the 已安装/未安装/全部 or 插件/扩展 filter rows.</summary>
public sealed partial class ExtensionFilterOptionViewModel : ViewModelBase
{
    internal ExtensionFilterOptionViewModel(string label, Action select)
    {
        Label = label;
        SelectCommand = new RelayCommand(_ => select());
    }

    public string Label { get; }
    [ObservableProperty] public partial bool IsSelected { get; set; }
    public RelayCommand SelectCommand { get; }
}
