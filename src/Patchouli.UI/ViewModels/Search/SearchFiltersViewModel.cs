using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Reactive.Disposables;
using Patchouli.Core.Results;
using Patchouli.Core.Search;
using Patchouli.Host.Composition;

namespace Patchouli.UI.ViewModels;

public sealed class SearchFiltersViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private BibliographicSearchFilterOptions? _filterOptions;
    private bool _synchronizingSidebarFilters;

    public SearchFiltersViewModel(MainWindowViewModel main)
    {
        _main = main;
        AddFilterRowCommand = new RelayCommand(_ => FilterRows.Add(new SearchFilterRowViewModel(this)));
        FilterRows.CollectionChanged += OnFilterRowsChanged;
        Register(Disposable.Create(() => FilterRows.CollectionChanged -= OnFilterRowsChanged));
        FilterRows.Add(new SearchFilterRowViewModel(this));

        PropertyChangedEventHandler sidebarHandler = OnSidebarPropertyChanged;
        _main.Shell.Sidebar.PropertyChanged += sidebarHandler;
        Register(Disposable.Create(() => _main.Shell.Sidebar.PropertyChanged -= sidebarHandler));
        SynchronizeSidebarFilters();
    }

    public ObservableCollection<SearchFilterRowViewModel> FilterRows { get; } = new();
    public RelayCommand AddFilterRowCommand { get; }

    [ExcludeFromDerivedGeneration]
    public bool HasActiveFilters => FilterRows.Any(row => row.IsSidebarLinked || !string.IsNullOrWhiteSpace(row.Value));

    public IReadOnlyList<BibliographicSearchFilter> BuildFilters()
    {
        return FilterRows.Select(row => row.ToFilter()).OfType<BibliographicSearchFilter>().ToArray();
    }

    internal IReadOnlyList<SearchFilterOption> FilterOptionsFor(string key)
    {
        return key switch
        {
            BibliographicSearchFilterKeys.ItemType => _filterOptions?.ItemTypes ?? [],
            BibliographicSearchFilterKeys.ItemStatus => _filterOptions?.ItemStatuses ?? [],
            BibliographicSearchFilterKeys.PrimaryDocumentOcrIndexStatus => _filterOptions?.OcrIndexStatuses ?? [],
            BibliographicSearchFilterKeys.CollectionId => _filterOptions?.Collections ?? [],
            BibliographicSearchFilterKeys.Citable =>
            [
                new SearchFilterOption("true", "可引用"),
                new SearchFilterOption("false", "不可引用")
            ],
            _ => []
        };
    }

    internal void RemoveFilterRow(SearchFilterRowViewModel row)
    {
        if (!row.IsSidebarLinked)
        {
            FilterRows.Remove(row);
            return;
        }

        LibrarySidebarViewModel sidebar = _main.Shell.Sidebar;
        _synchronizingSidebarFilters = true;
        try
        {
            if (row.SidebarTag is { } tag)
            {
                TagListItemViewModel? selected = sidebar.SelectedTags.FirstOrDefault(candidate =>
                    candidate.IsNoTagEntry == tag.IsNoTagEntry &&
                    string.Equals(candidate.Name, tag.Name, StringComparison.Ordinal));
                if (selected is not null)
                {
                    sidebar.ToggleTagSelection(selected);
                }
            }
            else if (row.SidebarCollection is { } collection &&
                     sidebar.SelectedCollection?.CollectionId == collection.CollectionId)
            {
                sidebar.ClearCollectionSelection();
            }
        }
        finally
        {
            _synchronizingSidebarFilters = false;
        }

        FilterRows.Remove(row);
        SynchronizeSidebarFilters();
    }

    private void SynchronizeSidebarFilters()
    {
        if (_synchronizingSidebarFilters)
        {
            return;
        }

        foreach (SearchFilterRowViewModel row in FilterRows.Where(row => row.IsSidebarLinked).ToArray())
        {
            FilterRows.Remove(row);
        }

        LibrarySidebarViewModel sidebar = _main.Shell.Sidebar;
        int index = 0;
        if (sidebar.SelectedCollection is { } collection)
        {
            FilterRows.Insert(index++, new SearchFilterRowViewModel(this, collection));
        }

        foreach (TagListItemViewModel tag in sidebar.SelectedTags)
        {
            FilterRows.Insert(index++, new SearchFilterRowViewModel(this, tag));
        }
    }

    private void OnSidebarPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(LibrarySidebarViewModel.SelectedTags)
            or nameof(LibrarySidebarViewModel.SelectedCollection))
        {
            SynchronizeSidebarFilters();
        }
    }

    private void OnFilterRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (SearchFilterRowViewModel row in e.OldItems)
            {
                row.PropertyChanged -= OnFilterRowPropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (SearchFilterRowViewModel row in e.NewItems)
            {
                row.PropertyChanged += OnFilterRowPropertyChanged;
            }
        }

        Raise(nameof(HasActiveFilters));
    }

    private void OnFilterRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(SearchFilterRowViewModel.Key) or nameof(SearchFilterRowViewModel.Value))
        {
            Raise(nameof(HasActiveFilters));
        }
    }

    internal Task ReloadOptionsAsync()
    {
        return _filterOptions is null ? Task.CompletedTask : LoadOptionsAsync();
    }

    internal void DetachLibraryContext()
    {
        _filterOptions = null;
        foreach (SearchFilterRowViewModel row in FilterRows.ToArray())
        {
            if (row.IsSidebarLinked)
            {
                FilterRows.Remove(row);
                continue;
            }

            if (row.Key.Key == BibliographicSearchFilterKeys.CollectionId)
            {
                row.Value = "";
            }

            row.RefreshAvailableValues();
        }
    }

    internal async Task LoadOptionsAsync()
    {
        int generation = _main.LibraryGeneration;
        HostServices services = await _main.ServicesAsync();
        Result<BibliographicSearchFilterOptions> options =
            await Task.Run(() => services.LibraryItems.GetSearchFilterOptionsAsync());
        if (!_main.IsCurrentLibraryContext(services, generation))
        {
            return;
        }

        if (options.IsFailure)
        {
            _main.ReportError($"筛选选项加载失败：{options.ErrorMessage}");
            return;
        }

        _filterOptions = options.Value;
        foreach (SearchFilterRowViewModel row in FilterRows)
        {
            row.RefreshAvailableValues();
        }
    }
}
