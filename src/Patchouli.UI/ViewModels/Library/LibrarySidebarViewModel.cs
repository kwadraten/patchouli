using System.Collections.ObjectModel;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Library;
using Patchouli.Core.Results;

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
public sealed class LibrarySidebarViewModel : ViewModelBase
{
    private LibrarySidebarSectionViewModel _selectedSection;
    private ObservableCollection<TagListItemViewModel> _tags = new();
    private readonly List<TagListItemViewModel> _selectedTags = new();

    public LibrarySidebarViewModel()
    {
        Sections =
        [
            new LibrarySidebarSectionViewModel("我的书库", "Database", LibrarySidebarScope.Active),
            new LibrarySidebarSectionViewModel("回收站", "Trash2", LibrarySidebarScope.Trash)
        ];
        _selectedSection = Sections[0];

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
    }

    public ObservableCollection<LibrarySidebarSectionViewModel> Sections { get; }

    public LibrarySidebarSectionViewModel SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (ReferenceEquals(_selectedSection, value) || value is null)
            {
                return;
            }

            _selectedSection = value;
            Raise();
            Raise(nameof(SelectedScope));
            Raise(nameof(IsTrashSelected));
            Raise(nameof(IsActiveSelected));
            Raise(nameof(CanRestore));
            Raise(nameof(CanDelete));
            Raise(nameof(CanPurge));
            Raise(nameof(IsTagAreaVisible));
            ScopeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public LibrarySidebarScope SelectedScope => _selectedSection.Scope;

    public bool IsTrashSelected => _selectedSection.Scope == LibrarySidebarScope.Trash;

    public bool IsActiveSelected => _selectedSection.Scope == LibrarySidebarScope.Active;

    public bool CanRestore => IsTrashSelected;

    public bool CanDelete => IsActiveSelected;

    public bool CanPurge => IsTrashSelected;

    public bool IsTagAreaVisible => IsActiveSelected;

    public AsyncCommand SelectActiveCommand { get; }

    public AsyncCommand SelectTrashCommand { get; }

    public ObservableCollection<TagListItemViewModel> Tags
    {
        get => _tags;
        private set
        {
            if (ReferenceEquals(_tags, value))
            {
                return;
            }

            _tags = value;
            Raise();
        }
    }

    /// <summary>
    /// The currently selected tag filters. AND semantics: an item must carry every selected tag,
    /// or (when "无标签" is selected) carry no tags at all. "无标签" is mutually exclusive with
    /// ordinary tag selection.
    /// </summary>
    public IReadOnlyList<TagListItemViewModel> SelectedTags => _selectedTags;

    public event EventHandler? ScopeChanged;

    public event EventHandler? TagSelectionChanged;

    public event EventHandler<TagListItemViewModel>? PinToggled;

    public event EventHandler<TagListItemViewModel>? RemoveRequested;

    public event EventHandler<TagListItemViewModel>? RenameRequested;

    public event EventHandler<TagListItemViewModel>? MergeIntoRequested;

    /// <summary>
    /// Loads the tag list from the given services and rebuilds the sidebar entries. Preserves the
    /// current selection when the same tags are still present.
    /// </summary>
    public async Task LoadTagsAsync(
        IItemTagService tagService,
        ILibraryItemQueryService queryService,
        IReadOnlyList<string> pinnedTags,
        CancellationToken cancellationToken = default)
    {
        // Microsoft.Data.Sqlite executes synchronously under the async facade, so run the
        // queries on a thread-pool thread to keep the UI responsive.
        Result<IReadOnlyList<TagInfo>> tagsResult =
            await Task.Run(() => tagService.ListTagsAsync(cancellationToken), cancellationToken);
        if (tagsResult.IsFailure || tagsResult.Value is null)
        {
            return;
        }

        IReadOnlyList<TagInfo> tags = tagsResult.Value;
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

        int noTagCount = await Task.Run(() => CountUntaggedItemsAsync(queryService, cancellationToken),
            cancellationToken);
        TagListItemViewModel noTagItem = CreateNoTagItem(noTagCount);
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

        TagListItemViewModel? noTag = Tags.FirstOrDefault(t => t.IsNoTagEntry);
        List<TagListItemViewModel> ordinary = Tags.Where(t => !t.IsNoTagEntry).ToList();
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

    public bool IsNoTagSelected => _selectedTags.Any(item => item.IsNoTagEntry);

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

    private static async Task<int> CountUntaggedItemsAsync(
        ILibraryItemQueryService queryService,
        CancellationToken cancellationToken)
    {
        Result<int> result = await queryService.CountUntaggedItemsAsync(cancellationToken);
        return result.IsFailure ? 0 : result.Value;
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
