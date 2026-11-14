using System.ComponentModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Host.Settings;
using Patchouli.UI.ViewModels.Editor;

namespace Patchouli.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    // The tab kind whose default was last applied; a repeated ActiveTab raise for the same kind
    // (rejected navigation confirm, same-kind switch) must not stomp a manual choice.
    private WorkspaceTabKind? _toolbarSearchContextKind;
    private int _toolbarSearchContextSuppressions;
    private Dictionary<string, string>? _toolbarSearchConfiguredModes;

    [ObservableProperty] public partial bool IsToolbarSearchPageAware { get; set; } = true;

    [ExcludeFromDerivedGeneration]
    public bool IsToolbarDocumentSearchAvailable =>
        Layout.ActiveTab?.Kind is WorkspaceTabKind.PdfWorkspace or WorkspaceTabKind.ItemEditor;

    partial void OnIsToolbarSearchPageAwareChanged(bool value)
    {
        if (value)
        {
            _toolbarSearchContextKind = null;
            ApplyToolbarSearchContextForActiveTab();
        }
    }

    /// <summary>
    /// Re-applies the per-kind default whenever the active workspace tab changes, and re-applies
    /// it immediately when committed settings change the configured defaults themselves.
    /// Explicit scope choices suspend page awareness until it is enabled again.
    /// </summary>
    private void WireToolbarSearchContextDefaults()
    {
        Register(Observable
            .FromEventPattern<PropertyChangedEventHandler, PropertyChangedEventArgs>(
                h => Layout.PropertyChanged += h,
                h => Layout.PropertyChanged -= h)
            .Where(e => e.EventArgs.PropertyName is null or nameof(WorkspaceLayoutViewModel.ActiveTab))
            .Subscribe(_ => ApplyToolbarSearchContextForActiveTab()));

        Register(Observable
            .FromEventPattern<AppSettingsCommittedEventArgs>(
                h => _settingsStore.SettingsCommitted += h,
                h => _settingsStore.SettingsCommitted -= h)
            .Where(e => e.EventArgs.Categories.Contains("Ui", StringComparer.OrdinalIgnoreCase))
            .ObserveOn(_uiScheduler)
            .Subscribe(_ => RefreshToolbarSearchContextForSettingsChange()));
    }

    private void ApplyToolbarSearchContextForActiveTab()
    {
        Raise(nameof(IsToolbarDocumentSearchAvailable));
        Raise(nameof(IsToolbarSettingsSearchAvailable));
        WorkspaceTabKind kind = Layout.ActiveTab?.Kind ?? WorkspaceTabKind.Library;
        if (kind != WorkspaceTabKind.Settings && ToolbarSearchMode.Mode == SearchMode.Settings)
        {
            DismissSettingsSearch();
        }

        if (_toolbarSearchContextKind == kind)
        {
            return;
        }

        _toolbarSearchContextKind = kind;
        _toolbarSearchConfiguredModes = _settings.Ui.SearchPageModes;
        if (_toolbarSearchContextSuppressions > 0)
        {
            return;
        }

        if (!IsToolbarSearchPageAware)
        {
            if (!IsToolbarDocumentSearchAvailable && ToolbarSearchMode.Mode == SearchMode.DocumentFullText)
            {
                ToolbarSearchMode = SearchPageAwareness.ModeOptions[1];
            }

            if (!IsToolbarSettingsSearchAvailable && ToolbarSearchMode.Mode == SearchMode.Settings)
            {
                ToolbarSearchMode = SearchPageAwareness.ModeOptions[1];
            }

            return;
        }

        ToolbarSearchMode = SearchPageAwareness.GetModeOption(_settings.Ui, kind);
    }

    /// <summary>
    /// Called after every in-memory settings refresh (<see cref="UpdateAppOptionsDeferred"/>,
    /// <see cref="UpdateAppOptions"/>, <see cref="UpdateAppOptionsAsync"/>, persisted reloads) and
    /// for committed Ui changes delivered through <see cref="IAppSettingsStore.SettingsCommitted"/>.
    /// Only an actual <see cref="UiPreferences.SearchPageModes"/> change re-derives the mode, so
    /// unrelated preference writes keep a manual toolbar choice intact.
    /// </summary>
    private void RefreshToolbarSearchContextForSettingsChange()
    {
        if (ModeMapsEqual(_toolbarSearchConfiguredModes, _settings.Ui.SearchPageModes))
        {
            return;
        }

        _toolbarSearchConfiguredModes = _settings.Ui.SearchPageModes;
        if (_toolbarSearchContextSuppressions > 0 || !IsToolbarSearchPageAware)
        {
            return;
        }

        WorkspaceTabKind kind = Layout.ActiveTab?.Kind ?? WorkspaceTabKind.Library;
        _toolbarSearchContextKind = kind;
        ToolbarSearchMode = SearchPageAwareness.GetModeOption(_settings.Ui, kind);
    }

    /// <summary>
    /// Holds back the per-kind default while a toolbar action opens its own target tab — the mode
    /// the user picked for this activation must stay in force through the programmatic switch.
    /// </summary>
    private IDisposable EnterToolbarSearchContextSuppression()
    {
        _toolbarSearchContextSuppressions++;
        return Disposable.Create(() => _toolbarSearchContextSuppressions--);
    }

    private static bool ModeMapsEqual(Dictionary<string, string>? first, Dictionary<string, string>? second)
    {
        if (ReferenceEquals(first, second))
        {
            return true;
        }

        if (first is null || second is null || first.Count != second.Count)
        {
            return false;
        }

        foreach ((string key, string value) in first)
        {
            if (!second.TryGetValue(key, out string? other) ||
                !string.Equals(value, other, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Document-scoped search (全书全文检索): resolves the context item, opens its PDF workspace,
    /// switches the sidebar to 全书搜索 and runs the query there. The global 全文搜索结果 tab is
    /// deliberately not opened for document searches.
    /// </summary>
    private async Task RunToolbarDocumentSearchAsync()
    {
        string query = ToolbarSearchQuery.Trim();
        if (query.Length == 0)
        {
            Report("请输入搜索词。");
            return;
        }

        LibraryItemViewModel? item = await ResolveDocumentSearchItemAsync();
        if (item is null)
        {
            Report("没有可搜索的文档。");
            return;
        }

        if (string.IsNullOrWhiteSpace(item.DocumentInstanceId))
        {
            Report("该题录暂无文档。");
            return;
        }

        PdfWorkspaceViewModel? workspace = await OpenDocumentSearchWorkspaceAsync(item);
        if (workspace is null)
        {
            return;
        }

        // ShowSearchTabCommand routes through SwitchSidebarTabAsync, so a pending page edit
        // resolves its save/discard/cancel prompt first. A cancelled guard leaves the sidebar on
        // its current surface — verify the switch before executing the query.
        await workspace.ShowSearchTabCommand.ExecuteAsync();
        if (!workspace.IsSearchTabActive)
        {
            return;
        }

        workspace.BookSearchQuery = query;
        await workspace.RunBookSearchCommand.ExecuteAsync();
    }

    private async Task<PdfWorkspaceViewModel?> OpenDocumentSearchWorkspaceAsync(LibraryItemViewModel item)
    {
        if (ActiveTab?.Content is PdfWorkspaceViewModel active && ReferenceEquals(active.Item, item))
        {
            return active;
        }

        await ShowReadingAsync(item);
        if (ActiveTab?.Content is not PdfWorkspaceViewModel workspace)
        {
            Report("无法打开 PDF 工作台执行全书搜索。");
            return null;
        }

        return workspace;
    }

    /// <summary>
    /// The item whose document a document-scoped search targets: the loaded item of an active PDF
    /// workspace, the item an item editor is editing, or the library selection for every other
    /// context.
    /// </summary>
    private async Task<LibraryItemViewModel?> ResolveDocumentSearchItemAsync()
    {
        switch (ActiveTab?.Content)
        {
            case PdfWorkspaceViewModel workspace:
                return workspace.Item;
            case ItemEditorViewModel editor:
                return await ResolveDocumentSearchItemForEditorAsync(editor);
            default:
                return Shell.SelectedItem;
        }
    }

    private async Task<LibraryItemViewModel?> ResolveDocumentSearchItemForEditorAsync(ItemEditorViewModel editor)
    {
        if (!editor.HasItem)
        {
            return null;
        }

        LibraryItemViewModel? match = FindLibraryItemByIdText(editor.ItemIdText);
        if (match is null && HasOpenRuntimeDatabase)
        {
            LinkedDocumentInstanceItemViewModel? document = editor.LinkedFiles
                .Where(file => !file.IsPendingRegistration)
                .OrderByDescending(file => file.IsPrimary)
                .FirstOrDefault();
            if (document is not null)
            {
                return await Shell.ResolveDocumentItemAsync(document.DocumentInstanceId.ToString());
            }
        }

        return match;
    }

    private LibraryItemViewModel? FindLibraryItemByIdText(string itemIdText)
    {
        return Shell.Items.FirstOrDefault(candidate =>
            string.Equals(candidate.ItemId, itemIdText, StringComparison.OrdinalIgnoreCase));
    }
}
