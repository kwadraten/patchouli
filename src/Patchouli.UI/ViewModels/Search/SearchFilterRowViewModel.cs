using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Search;

namespace Patchouli.UI.ViewModels;

public enum SearchMode
{
    Bibliographic,
    FullText,
    DocumentFullText
}

public sealed record SearchModeOption
{
    public SearchModeOption(SearchMode mode, string label, string hint)
    {
        Mode = mode;
        Label = label;
        Hint = hint;
    }

    public SearchMode Mode { get; }
    public string Label { get; }
    public string Hint { get; }
}

/// <summary>One selectable key in the advanced-search filter row.</summary>
public sealed record SearchFilterKeyOption
{
    public SearchFilterKeyOption(string key, string label, bool isText)
    {
        Key = key;
        Label = label;
        IsText = isText;
    }

    public string Key { get; }
    public string Label { get; }
    public bool IsText { get; }

    public static readonly SearchFilterKeyOption[] All =
    [
        new(BibliographicSearchFilterKeys.Title, "题名", true),
        new(BibliographicSearchFilterKeys.Author, "作者", true),
        new(BibliographicSearchFilterKeys.Identifier, "标识符", true),
        new(BibliographicSearchFilterKeys.ItemType, "题录类型", false),
        new(BibliographicSearchFilterKeys.ItemStatus, "条目状态", false),
        new(BibliographicSearchFilterKeys.PrimaryDocumentOcrIndexStatus, "OCR 索引状态", false),
        new(BibliographicSearchFilterKeys.Citable, "可引用", false),
        new(BibliographicSearchFilterKeys.Tag, "标签（精确）", true),
        new(BibliographicSearchFilterKeys.CollectionId, "集合", false)
    ];

    public static SearchFilterKeyOption For(string key)
    {
        return All.FirstOrDefault(option => option.Key == key) ?? All[0];
    }
}

/// <summary>A dynamic filter row in the advanced-search form; rows combine with AND.</summary>
public sealed partial class SearchFilterRowViewModel : ViewModelBase
{
    private readonly SearchFiltersViewModel _owner;

    public SearchFilterRowViewModel(SearchFiltersViewModel owner)
        : this(owner, null, null)
    {
    }

    internal SearchFilterRowViewModel(SearchFiltersViewModel owner, CollectionListItemViewModel collection)
        : this(owner, null, collection)
    {
    }

    internal SearchFilterRowViewModel(SearchFiltersViewModel owner, TagListItemViewModel tag)
        : this(owner, tag, null)
    {
    }

    private SearchFilterRowViewModel(
        SearchFiltersViewModel owner,
        TagListItemViewModel? sidebarTag,
        CollectionListItemViewModel? sidebarCollection)
    {
        _owner = owner;
        SidebarTag = sidebarTag;
        SidebarCollection = sidebarCollection;
        RemoveCommand = new RelayCommand(_ => owner.RemoveFilterRow(this));
        if (sidebarTag is not null)
        {
            Key = SearchFilterKeyOption.For(BibliographicSearchFilterKeys.Tag);
            Value = sidebarTag.Name;
        }
        else if (sidebarCollection is not null)
        {
            Key = SearchFilterKeyOption.For(BibliographicSearchFilterKeys.CollectionId);
            Value = sidebarCollection.CollectionId.ToString();
        }
    }

    public RelayCommand RemoveCommand { get; }

    public TagListItemViewModel? SidebarTag { get; }
    public CollectionListItemViewModel? SidebarCollection { get; }
    public bool IsSidebarLinked => SidebarTag is not null || SidebarCollection is not null;
    public bool IsReadOnly => IsSidebarLinked;
    public string DisplayValue => SidebarTag?.DisplayText ?? SidebarCollection?.Name ?? Value;

    [ObservableProperty] public partial SearchFilterKeyOption Key { get; set; } = SearchFilterKeyOption.All[0];

    partial void OnKeyChanged(SearchFilterKeyOption value)
    {
        Value = "";
        Raise(nameof(AvailableValues));
    }

    public bool IsTextKey => !IsSidebarLinked && Key.IsText;
    public bool IsChoiceKey => !IsSidebarLinked && !Key.IsText;

    [ExcludeFromDerivedGeneration]
    public IReadOnlyList<SearchFilterOption> AvailableValues => _owner.FilterOptionsFor(Key.Key);

    [ObservableProperty] public partial string Value { get; set; } = "";

    public BibliographicSearchFilter? ToFilter()
    {
        return IsSidebarLinked || string.IsNullOrWhiteSpace(Value)
            ? null
            : new BibliographicSearchFilter(Key.Key, Value.Trim());
    }

    public void RefreshAvailableValues()
    {
        Raise(nameof(AvailableValues));
    }
}
