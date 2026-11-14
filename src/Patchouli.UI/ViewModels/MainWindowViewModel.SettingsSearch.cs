using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.UI.ViewModels.Core;
using Patchouli.UI.ViewModels.Settings;

namespace Patchouli.UI.ViewModels;

public sealed class SettingsSearchResultViewModel
{
    public SettingsSearchResultViewModel(SettingsSearchEntry entry, bool isSelected)
    {
        Entry = entry;
        IsSelected = isSelected;
    }

    public SettingsSearchEntry Entry { get; }
    public string DisplayPath => Entry.DisplayPath;
    public bool IsSelected { get; set; }
}

public sealed partial class MainWindowViewModel
{
    private int _settingsSearchGeneration;

    public ObservableCollection<SettingsSearchResultViewModel> SettingsSearchResults { get; } = new();

    [ObservableProperty] public partial bool IsSettingsSearchOpen { get; set; }

    [ObservableProperty] public partial string SettingsSearchStatus { get; set; } = "";

    [ObservableProperty] public partial int SelectedSettingsSearchIndex { get; set; } = -1;

    [ExcludeFromDerivedGeneration]
    public bool IsToolbarSettingsSearchAvailable => Layout.ActiveTab?.Kind == WorkspaceTabKind.Settings;

    [ExcludeFromDerivedGeneration] public bool HasSettingsSearchResults => SettingsSearchResults.Count > 0;

    partial void OnToolbarSearchQueryChanged(string value)
    {
        RefreshSettingsSearch();
    }

    partial void OnSelectedSettingsSearchIndexChanged(int value)
    {
        for (int i = 0; i < SettingsSearchResults.Count; i++)
        {
            SettingsSearchResults[i].IsSelected = i == value;
        }
    }

    private void RefreshSettingsSearch()
    {
        if (ToolbarSearchMode.Mode != SearchMode.Settings || !IsToolbarSettingsSearchAvailable)
        {
            DismissSettingsSearch();
            return;
        }

        SettingsSearchEntry[] matches = SettingsSearchIndex.Search(Settings, ToolbarSearchQuery).ToArray();
        SettingsSearchResults.Clear();
        foreach (SettingsSearchEntry entry in matches)
        {
            SettingsSearchResults.Add(new SettingsSearchResultViewModel(entry, false));
        }

        SelectedSettingsSearchIndex = matches.Length == 0 ? -1 : 0;
        IsSettingsSearchOpen = ToolbarSearchQuery.Trim().Length > 0;
        SettingsSearchStatus = ToolbarSearchQuery.Trim().Length == 0
            ? ""
            : matches.Length == 0
                ? "没有找到匹配的设置项"
                : "";
        Raise(nameof(HasSettingsSearchResults));
    }

    public void MoveSettingsSearchSelection(int delta)
    {
        if (SettingsSearchResults.Count == 0)
        {
            return;
        }

        int next = SelectedSettingsSearchIndex < 0
            ? 0
            : (SelectedSettingsSearchIndex + delta + SettingsSearchResults.Count) % SettingsSearchResults.Count;
        SelectedSettingsSearchIndex = next;
        IsSettingsSearchOpen = true;
    }

    public void DismissSettingsSearch()
    {
        IsSettingsSearchOpen = false;
        SelectedSettingsSearchIndex = -1;
        SettingsSearchResults.Clear();
        Raise(nameof(HasSettingsSearchResults));
    }

    public async Task AcceptSettingsSearchResultAsync(SettingsSearchResultViewModel? result = null)
    {
        result ??= SelectedSettingsSearchIndex >= 0 && SelectedSettingsSearchIndex < SettingsSearchResults.Count
            ? SettingsSearchResults[SelectedSettingsSearchIndex]
            : SettingsSearchResults.FirstOrDefault();
        if (result is null)
        {
            Report(SettingsSearchStatus.Length > 0 ? SettingsSearchStatus : "没有找到匹配的设置项");
            return;
        }

        int generation = ++_settingsSearchGeneration;
        DismissSettingsSearch();
        IsSettingsSearchOpen = false;
        await Settings.NavigateToSearchEntryAsync(result.Entry);
        if (generation == _settingsSearchGeneration)
        {
            ToolbarSearchQuery = "";
        }
    }

    /// <summary>Runs the settings-scoped toolbar search. Query rewrite is deliberately not
    /// applied; a leading patchouli:// link is handled before this method is reached.</summary>
    private async Task RunSettingsSearchAsync()
    {
        RefreshSettingsSearch();
        if (SettingsSearchResults.Count == 0)
        {
            Report(SettingsSearchStatus.Length > 0 ? SettingsSearchStatus : "没有找到匹配的设置项");
            return;
        }

        await AcceptSettingsSearchResultAsync();
    }
}
