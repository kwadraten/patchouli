namespace Patchouli.UI.ViewModels;

/// <summary>A matched snippet shown in the expandable full-width details section of a hit row.
/// The display text is a three-part inline (prefix | bold hit | suffix) centered on the hit.</summary>
public sealed class SearchHitSnippetViewModel : ViewModelBase
{
    public SearchHitSnippetViewModel(SearchMatchedUnitViewModel unit, string query,
        Func<SearchMatchedUnitViewModel, Task> jumpAsync)
    {
        Unit = unit;
        SearchSnippetParts parts = SearchSnippetFormatter.Format(unit.Text, query);
        Prefix = parts.Prefix;
        Hit = parts.Hit;
        Suffix = parts.Suffix;
        PageLabel = $"第 {unit.PageIndex + 1} 页";
        JumpCommand = new AsyncCommand(() => jumpAsync(unit));
    }

    public SearchMatchedUnitViewModel Unit { get; }
    public string Prefix { get; }
    public string Hit { get; }
    public string Suffix { get; }
    public string PageLabel { get; }
    public AsyncCommand JumpCommand { get; }

    public string NodeTypeLabel => string.IsNullOrWhiteSpace(Unit.NodeType) ? "文本" : Unit.NodeType;

    public string BadgeText => $"{NodeTypeLabel} · {PageLabel}";
}

/// <summary>A full-text hit row: the bibliographic item rendered in the grid columns, plus the
/// matched snippets rendered in the row's full-width details section when expanded.</summary>
public sealed class SearchHitItemViewModel : ViewModelBase
{
    private bool _isExpanded;

    private SearchHitItemViewModel(LibraryItemViewModel item, IReadOnlyList<SearchHitSnippetViewModel> snippets)
    {
        Item = item;
        Snippets = snippets;
        ToggleExpandedCommand = new RelayCommand(_ => IsExpanded = !IsExpanded);
    }

    public LibraryItemViewModel Item { get; }
    public IReadOnlyList<SearchHitSnippetViewModel> Snippets { get; }
    public bool HasSnippets => Snippets.Count > 0;
    public double DetailsHeight => Math.Min(Snippets.Count * 28 + 10, 360);
    public RelayCommand ToggleExpandedCommand { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            Raise();
        }
    }

    public static SearchHitItemViewModel HitItem(LibraryItemViewModel item,
        IReadOnlyList<SearchHitSnippetViewModel> snippets)
    {
        return new SearchHitItemViewModel(item, snippets);
    }

    public static SearchHitItemViewModel FallbackItem(string title, IReadOnlyList<SearchHitSnippetViewModel> snippets)
    {
        LibraryItemViewModel item = new(
            "",
            title,
            "",
            "",
            "",
            "",
            null,
            null,
            null,
            "",
            "",
            0,
            0,
            "not_indexed",
            _ => Task.CompletedTask,
            _ => Task.CompletedTask);
        return new SearchHitItemViewModel(item, snippets);
    }
}
