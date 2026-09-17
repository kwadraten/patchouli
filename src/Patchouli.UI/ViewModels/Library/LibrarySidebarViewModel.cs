using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Host.Caching;

namespace Patchouli.UI.ViewModels;

public enum LibrarySidebarScope
{
    Active,
    Trash
}

public sealed class LibrarySidebarSectionViewModel : ViewModelBase
{
    public LibrarySidebarSectionViewModel(string label, string icon, LibrarySidebarScope scope)
    {
        Label = label;
        Icon = icon;
        Scope = scope;
    }

    public string Label { get; }
    public string Icon { get; }
    public LibrarySidebarScope Scope { get; }
}

/// <summary>
/// Sidebar state for the library page: scope switcher and tag list. Tag mutations are
/// delegated to the owning shell through the command callbacks supplied to each
/// <see cref="TagListItemViewModel"/>.
/// </summary>
public sealed partial class LibrarySidebarViewModel : ViewModelBase
{
    private LibrarySidebarSectionViewModel _selectedSection;
    private readonly List<TagListItemViewModel> _selectedTags = new();
    private bool _silentCollectionSelection;

    public LibrarySidebarViewModel()
    {
        Sections =
        [
            new LibrarySidebarSectionViewModel("我的书库", "Database", LibrarySidebarScope.Active),
            new LibrarySidebarSectionViewModel("回收站", "Trash2", LibrarySidebarScope.Trash)
        ];
        _selectedSection = Sections[0];
        RebuildNavigationItems();

        SelectActiveCommand = new AsyncCommand(() =>
        {
            SelectedSection = Sections[0];
            return Task.CompletedTask;
        });

        SelectTrashCommand = new AsyncCommand(() =>
        {
            SelectedSection = Sections[1];
            return Task.CompletedTask;
        });

        CreateCollectionCommand = new AsyncCommand(() =>
        {
            CreateCollectionRequested?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        });
    }

    public ObservableCollection<LibrarySidebarSectionViewModel> Sections { get; }

    /// <summary>Built-in library rows and user collections in their single visual section.</summary>
    public ObservableCollection<object> NavigationItems { get; } = new();

    public LibrarySidebarSectionViewModel SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (value is null)
            {
                return;
            }

            if (ReferenceEquals(_selectedSection, value))
            {
                if (SelectedCollection is not null)
                {
                    ClearCollectionSelection();
                }

                SyncSelectedNavigationItem();
                return;
            }

            if (SelectedCollection is not null)
            {
                foreach (CollectionListItemViewModel item in Collections)
                {
                    item.IsSelected = false;
                }

                SetSelectedCollectionSilently(null);
            }

            _selectedSection = value;
            Raise();
            SyncSelectedNavigationItem();
            if (!IsActiveSelected)
            {
                ClearCollectionSelection();
            }

            ScopeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public LibrarySidebarScope SelectedScope => SelectedSection.Scope;

    /// <summary>The one selected row across built-in scopes and user collections.</summary>
    [ObservableProperty]
    public partial object? SelectedNavigationItem { get; set; }

    partial void OnSelectedNavigationItemChanged(object? value)
    {
        switch (value)
        {
            case LibrarySidebarSectionViewModel section:
                SelectedSection = section;
                break;
            case CollectionListItemViewModel collection:
                SelectedCollection = collection;
                break;
        }
    }

    public bool IsTrashSelected => SelectedSection.Scope == LibrarySidebarScope.Trash;

    public bool IsActiveSelected => SelectedSection.Scope == LibrarySidebarScope.Active;

    public bool CanRestore => IsTrashSelected;

    public bool CanDelete => IsActiveSelected;

    public bool CanPurge => IsTrashSelected;

    public bool IsTagAreaVisible => IsActiveSelected;

    public bool IsCollectionAreaVisible => IsActiveSelected;

    [ExcludeFromDerivedGeneration] public bool HasCollections => Collections.Count > 0;

    [ExcludeFromDerivedGeneration] public bool NoCollections => Collections.Count == 0;

    public AsyncCommand SelectActiveCommand { get; }

    public AsyncCommand SelectTrashCommand { get; }

    public AsyncCommand CreateCollectionCommand { get; }

    [ObservableProperty] public partial ObservableCollection<TagListItemViewModel> Tags { get; private set; } = new();

    /// <summary>
    /// The collection catalog, exposed as one stable instance so bindings survive a reload. A new
    /// projection is applied by clearing and re-adding rather than replacing the collection.
    /// </summary>
    public ObservableCollection<CollectionListItemViewModel> Collections { get; } = new();

    /// <summary>The single selected collection filter, or null for no collection filter.</summary>
    [ObservableProperty]
    public partial CollectionListItemViewModel? SelectedCollection { get; set; }

    partial void OnSelectedCollectionChanging(CollectionListItemViewModel? value)
    {
        if (_silentCollectionSelection)
        {
            return;
        }

        if (value is not null && !IsActiveSelected)
        {
            _selectedSection = Sections[0];
            Raise(nameof(SelectedSection));
        }
    }

    partial void OnSelectedCollectionChanged(CollectionListItemViewModel? value)
    {
        if (_silentCollectionSelection)
        {
            return;
        }

        if (value is not null)
        {
            foreach (CollectionListItemViewModel item in Collections)
            {
                item.IsSelected = ReferenceEquals(item, value);
            }
        }

        SyncSelectedNavigationItem();
        CollectionSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool HasSelectedCollection => SelectedCollection is not null;

    public string SelectedCollectionName => SelectedCollection?.Name ?? string.Empty;

    /// <summary>
    /// The currently selected tag filters. AND semantics: an item must carry every selected tag,
    /// or (when "无标签" is selected) carry no tags at all. "无标签" is mutually exclusive with
    /// ordinary tag selection.
    /// </summary>
    [ExcludeFromDerivedGeneration]
    public IReadOnlyList<TagListItemViewModel> SelectedTags => _selectedTags;

    public event EventHandler? ScopeChanged;

    public event EventHandler? TagSelectionChanged;

    public event EventHandler? CollectionSelectionChanged;

    public event EventHandler? CreateCollectionRequested;

    public event EventHandler<CollectionListItemViewModel>? RenameCollectionRequested;

    public event EventHandler<CollectionListItemViewModel>? DissolveCollectionRequested;

    public event EventHandler<CollectionListItemViewModel>? AddToSelectionRequested;

    public event EventHandler<TagListItemViewModel>? PinToggled;

    public event EventHandler<TagListItemViewModel>? RemoveRequested;

    public event EventHandler<TagListItemViewModel>? RenameRequested;

    public event EventHandler<TagListItemViewModel>? MergeIntoRequested;

    /// <summary>
    /// Rebuilds the sidebar entries from the library item cache's tag-count snapshot, preserving
    /// the current selection when the same tags are still present. The caller ensures the cache
    /// is loaded via <see cref="LibraryItemCache.EnsureLoadedAsync"/> before reading.
    /// </summary>
    public async Task LoadTagsAsync(
        LibraryItemCache cache,
        IReadOnlyList<string> pinnedTags,
        CancellationToken cancellationToken = default)
    {
        await cache.EnsureLoadedAsync(cancellationToken);
        TagCountsSnapshot counts = cache.GetTagCounts();
        LoadTags(counts.Tags, counts.UntaggedCount, pinnedTags);
    }

    public void LoadTags(
        IReadOnlyList<TagInfo> tags,
        int untaggedCount,
        IReadOnlyList<string> pinnedTags)
    {
        HashSet<string> pinnedSet = new(pinnedTags, StringComparer.Ordinal);
        HashSet<string> previouslySelected = _selectedTags
            .Where(item => !item.IsNoTagEntry)
            .Select(item => item.Name)
            .ToHashSet(StringComparer.Ordinal);
        bool noTagWasSelected = _selectedTags.Any(item => item.IsNoTagEntry);

        List<TagListItemViewModel> nextTags = new();
        foreach (TagInfo tag in tags)
        {
            nextTags.Add(CreateTagItem(tag.Name, tag.Count, pinnedSet.Contains(tag.Name)));
        }

        TagListItemViewModel noTagItem = CreateNoTagItem(untaggedCount);
        noTagItem.IsSelected = noTagWasSelected;

        // Preserve selected state on ordinary tags.
        foreach (TagListItemViewModel item in nextTags)
        {
            if (previouslySelected.Contains(item.Name))
            {
                item.IsSelected = true;
            }
        }

        // The selection list must reference the new instances: the old ones are discarded with
        // the previous Tags collection, and reference equality would otherwise make every
        // subsequent toggle re-add instead of remove.
        _selectedTags.Clear();
        foreach (TagListItemViewModel item in nextTags)
        {
            if (item.IsSelected)
            {
                _selectedTags.Add(item);
            }
        }

        if (noTagItem.IsSelected)
        {
            _selectedTags.Add(noTagItem);
        }

        List<TagListItemViewModel> sorted = SortTags(nextTags, pinnedTags);
        foreach (TagListItemViewModel item in sorted)
        {
            WireItemEvents(item);
        }

        Tags = new ObservableCollection<TagListItemViewModel>(sorted);
        Tags.Add(noTagItem);
    }

    /// <summary>
    /// Rebuilds the sidebar collection list from a fresh catalog snapshot, preserving the
    /// current selection when that collection still exists. Collections are ordered by name.
    /// </summary>
    public void LoadCollections(IReadOnlyList<Collection> collections)
    {
        CollectionId? previousSelection = SelectedCollection?.CollectionId;
        List<CollectionListItemViewModel> next = collections
            .OrderBy(collection => collection.Name, StringComparer.Ordinal)
            .Select(collection =>
            {
                CollectionListItemViewModel item =
                    new(collection.CollectionId, collection.Name, collection.ItemCount);
                WireCollectionEvents(item);
                return item;
            })
            .ToList();

        Collections.Clear();
        foreach (CollectionListItemViewModel item in next)
        {
            Collections.Add(item);
        }

        CollectionListItemViewModel? restored = previousSelection is { } previous
            ? next.FirstOrDefault(item => item.CollectionId == previous)
            : null;
        SetSelectedCollectionSilently(restored);
        if (restored is not null)
        {
            restored.IsSelected = true;
        }

        RebuildNavigationItems();

        Raise(nameof(Collections));
        Raise(nameof(HasCollections));
        Raise(nameof(NoCollections));
    }

    /// <summary>Selects one collection, or clears the filter when the same collection is clicked again.</summary>
    public void ToggleCollectionSelection(CollectionListItemViewModel item)
    {
        if (ReferenceEquals(SelectedCollection, item))
        {
            ClearCollectionSelection();
            return;
        }

        SelectedCollection = item;
    }

    public void ClearCollectionSelection()
    {
        if (SelectedCollection is null)
        {
            return;
        }

        foreach (CollectionListItemViewModel item in Collections)
        {
            item.IsSelected = false;
        }

        SelectedCollection = null;
    }

    private void WireCollectionEvents(CollectionListItemViewModel item)
    {
        item.RequestRename = collection =>
        {
            RenameCollectionRequested?.Invoke(this, collection);
            return Task.CompletedTask;
        };
        item.RequestDissolve = collection =>
        {
            DissolveCollectionRequested?.Invoke(this, collection);
            return Task.CompletedTask;
        };
        item.RequestAddToSelection = collection =>
        {
            AddToSelectionRequested?.Invoke(this, collection);
            return Task.CompletedTask;
        };
    }

    private void RebuildNavigationItems()
    {
        NavigationItems.Clear();
        NavigationItems.Add(Sections[0]);
        foreach (CollectionListItemViewModel collection in Collections)
        {
            NavigationItems.Add(collection);
        }

        NavigationItems.Add(Sections[1]);
        Raise(nameof(NavigationItems));
        SyncSelectedNavigationItem(true);
    }

    private void SyncSelectedNavigationItem(bool forceNotification = false)
    {
        object target = SelectedCollection is not null ? SelectedCollection : _selectedSection;
        if (!forceNotification && Equals(SelectedNavigationItem, target))
        {
            return;
        }

        if (forceNotification && Equals(SelectedNavigationItem, target))
        {
            SelectedNavigationItem = null;
        }

        SelectedNavigationItem = target;
    }

    private void SetSelectedCollectionSilently(CollectionListItemViewModel? value)
    {
        _silentCollectionSelection = true;
        try
        {
            SelectedCollection = value;
        }
        finally
        {
            _silentCollectionSelection = false;
        }
    }

    /// <summary>
    /// Toggles selection of a tag entry and notifies the shell. Selecting "无标签" clears ordinary
    /// tag selections; selecting an ordinary tag clears "无标签".
    /// </summary>
    public void ToggleTagSelection(TagListItemViewModel item)
    {
        if (item.IsNoTagEntry)
        {
            bool becomingSelected = !_selectedTags.Contains(item);
            _selectedTags.Clear();
            foreach (TagListItemViewModel tag in Tags)
            {
                tag.IsSelected = false;
            }

            if (becomingSelected)
            {
                _selectedTags.Add(item);
                item.IsSelected = true;
            }
        }
        else
        {
            TagListItemViewModel? noTag = Tags.FirstOrDefault(t => t.IsNoTagEntry);
            if (noTag is not null)
            {
                noTag.IsSelected = false;
                _selectedTags.Remove(noTag);
            }

            if (_selectedTags.Contains(item))
            {
                _selectedTags.Remove(item);
                item.IsSelected = false;
            }
            else
            {
                _selectedTags.Add(item);
                item.IsSelected = true;
            }
        }

        Raise(nameof(SelectedTags));
        TagSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Updates the pinned state and order to match <paramref name="pinnedTags"/>, preserving
    /// selection where possible.
    /// </summary>
    public void ApplyPinnedOrder(IReadOnlyList<string> pinnedTags)
    {
        if (Tags.Count == 0)
        {
            return;
        }

        TagListItemViewModel[] snapshot = Tags.OfType<TagListItemViewModel>().ToArray();
        TagListItemViewModel? noTag = snapshot.FirstOrDefault(t => t.IsNoTagEntry);
        List<TagListItemViewModel> ordinary = snapshot.Where(t => !t.IsNoTagEntry).ToList();
        foreach (TagListItemViewModel item in ordinary)
        {
            item.IsPinned = pinnedTags.Contains(item.Name, StringComparer.Ordinal);
        }

        List<TagListItemViewModel> sorted = SortTags(ordinary, pinnedTags);
        foreach (TagListItemViewModel item in sorted)
        {
            WireItemEvents(item);
        }

        Tags = new ObservableCollection<TagListItemViewModel>(sorted);
        if (noTag is not null)
        {
            Tags.Add(noTag);
        }
    }

    public IReadOnlyList<string> GetSelectedTagNames()
    {
        return _selectedTags
            .Where(item => !item.IsNoTagEntry)
            .Select(item => item.Name)
            .ToArray();
    }

    [ExcludeFromDerivedGeneration] public bool IsNoTagSelected => _selectedTags.Any(item => item.IsNoTagEntry);

    public void ClearTagSelection()
    {
        _selectedTags.Clear();
        foreach (TagListItemViewModel tag in Tags)
        {
            tag.IsSelected = false;
        }

        Raise(nameof(SelectedTags));
        TagSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private TagListItemViewModel CreateTagItem(string name, int count, bool isPinned)
    {
        TagListItemViewModel item = new(name, count, isPinned, false);
        WireItemEvents(item);
        return item;
    }

    private TagListItemViewModel CreateNoTagItem(int count)
    {
        TagListItemViewModel item = new("", count, false, true);
        item.RequestTogglePin = _ => Task.CompletedTask;
        item.RequestRemove = _ => Task.CompletedTask;
        item.RequestRename = _ => Task.CompletedTask;
        item.RequestMergeInto = _ => Task.CompletedTask;
        return item;
    }

    private void WireItemEvents(TagListItemViewModel item)
    {
        item.RequestTogglePin = tag =>
        {
            PinToggled?.Invoke(this, tag);
            return Task.CompletedTask;
        };
        item.RequestRemove = tag =>
        {
            RemoveRequested?.Invoke(this, tag);
            return Task.CompletedTask;
        };
        item.RequestRename = tag =>
        {
            RenameRequested?.Invoke(this, tag);
            return Task.CompletedTask;
        };
        item.RequestMergeInto = tag =>
        {
            MergeIntoRequested?.Invoke(this, tag);
            return Task.CompletedTask;
        };
    }

    private static List<TagListItemViewModel> SortTags(
        IReadOnlyList<TagListItemViewModel> tags,
        IReadOnlyList<string> pinnedTags)
    {
        Dictionary<string, int> pinnedIndex = pinnedTags
            .Select((name, index) => (name, index))
            .ToDictionary(pair => pair.name, pair => pair.index, StringComparer.Ordinal);

        return tags
            .OrderByDescending(item => pinnedIndex.TryGetValue(item.Name, out int index) ? -index : int.MinValue)
            .ThenByDescending(item => item.Count)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ToList();
    }
}
