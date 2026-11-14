using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Patchouli.UI.ViewModels.Settings;

/// <summary>「搜索框页面感知」panel inside Settings &gt; 搜索: one row per workspace tab kind with
/// the search mode the toolbar search box applies when that kind is activated. Selections are
/// device-local UI preferences written through the settings store's debounced writer on change, so
/// each edit auto-saves without touching this section's dirty/save lifecycle or unrelated
/// settings.</summary>
public sealed partial class SearchPageAwarenessSettingsViewModel : SettingsSectionViewModelBase
{
    private readonly MainWindowViewModel _main;

    [ExcludeFromDerivedGeneration] public MainWindowViewModel SearchControls => _main;
    private bool _isSyncing;

    public SearchPageAwarenessSettingsViewModel(MainWindowViewModel main)
    {
        _main = main;
        Rows = SearchPageAwareness.Kinds
            .Select(kind => new SearchPageModeRowViewModel(this, kind))
            .ToArray();
        RefreshFromPersisted();
    }

    [ExcludeFromDerivedGeneration]
    public IReadOnlyList<SearchModeOption> ModeOptions => SearchPageAwareness.ModeOptions;

    public IReadOnlyList<SearchPageModeRowViewModel> Rows { get; }

    public override bool SupportsEditing => true;
    public override bool IsDirty => false;
    public override bool CanSave => false;

    public override Task LoadAsync(CancellationToken cancellationToken = default)
    {
        RefreshFromPersisted();
        return Task.CompletedTask;
    }

    public override Task SaveAsync()
    {
        return Task.CompletedTask;
    }

    public override Task DiscardAsync()
    {
        RefreshFromPersisted();
        return Task.CompletedTask;
    }

    /// <summary>Whether new full-text search hits start with their snippets expanded.</summary>
    [ObservableProperty]
    public partial bool ExpandFullTextSearchResults { get; set; } = true;

    partial void OnExpandFullTextSearchResultsChanged(bool value)
    {
        if (_isSyncing || _main.AppOptions.Ui.ExpandFullTextSearchResults == value)
        {
            return;
        }

        _main.UpdateAppOptionsDeferred(current => current with
        {
            Ui = current.Ui with { ExpandFullTextSearchResults = value }
        }, "Ui");
    }

    /// <summary>Re-reads the persisted preferences into the row selections without writing back.</summary>
    internal void RefreshFromPersisted()
    {
        _isSyncing = true;
        try
        {
            UiPreferences preferences = _main.AppOptions.Ui;
            ExpandFullTextSearchResults = preferences.ExpandFullTextSearchResults;
            foreach (SearchPageModeRowViewModel row in Rows)
            {
                row.SelectedMode = SearchPageAwareness.GetModeOption(preferences, row.Kind);
            }
        }
        finally
        {
            _isSyncing = false;
        }
    }

    internal void OnRowModeChanged(SearchPageModeRowViewModel row, SearchModeOption? option)
    {
        if (_isSyncing || option is null || !row.ModeOptions.Contains(option))
        {
            return;
        }

        WorkspaceTabKind kind = row.Kind;
        string persisted = SearchPageAwareness.ToPersistedValue(option.Mode);
        bool isDefault = option.Mode == SearchPageAwareness.DefaultMode(kind);
        string key = kind.ToString();
        Dictionary<string, string>? existing = _main.AppOptions.Ui.SearchPageModes;
        if (isDefault && (existing is null || !existing.ContainsKey(key)))
        {
            return;
        }

        if (!isDefault && existing is not null &&
            existing.TryGetValue(key, out string? currentValue) &&
            string.Equals(currentValue, persisted, StringComparison.Ordinal))
        {
            return;
        }

        _main.UpdateAppOptionsDeferred(current =>
        {
            Dictionary<string, string> modes = current.Ui.SearchPageModes is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(current.Ui.SearchPageModes, StringComparer.Ordinal);
            if (isDefault)
            {
                modes.Remove(key);
            }
            else
            {
                modes[key] = persisted;
            }

            return current with
            {
                Ui = current.Ui with { SearchPageModes = modes.Count == 0 ? null : modes }
            };
        }, "Ui");
    }
}

/// <summary>One workspace tab kind row in the 搜索框页面感知 table.</summary>
public sealed partial class SearchPageModeRowViewModel : ViewModelBase
{
    private readonly SearchPageAwarenessSettingsViewModel _owner;

    internal SearchPageModeRowViewModel(SearchPageAwarenessSettingsViewModel owner, WorkspaceTabKind kind)
    {
        _owner = owner;
        Kind = kind;
    }

    public WorkspaceTabKind Kind { get; }

    [ExcludeFromDerivedGeneration] public string KindLabel => SearchPageAwareness.GetKindLabel(Kind);

    /// <summary>Label of the mode applied while this kind has no persisted override.</summary>
    [ExcludeFromDerivedGeneration]
    public string DefaultModeLabel => SearchPageAwareness.ModeOptions
        .First(option => option.Mode == SearchPageAwareness.DefaultMode(Kind)).Label;

    [ExcludeFromDerivedGeneration]
    public IReadOnlyList<SearchModeOption> ModeOptions =>
        SearchPageAwareness.ModeOptionsFor(Kind);

    [ObservableProperty] public partial SearchModeOption? SelectedMode { get; set; }

    partial void OnSelectedModeChanged(SearchModeOption? value)
    {
        _owner.OnRowModeChanged(this, value);
    }
}
