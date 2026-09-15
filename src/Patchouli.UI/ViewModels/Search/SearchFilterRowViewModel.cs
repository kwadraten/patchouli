using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Search;

namespace Patchouli.UI.ViewModels;

public enum SearchMode
{
    Bibliographic,
    FullText
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

    public const string KeywordKey = "keyword";

    public static readonly SearchFilterKeyOption[] All =
    [
        new(KeywordKey, "关键词", true),
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
        return All.FirstOrDefault(option => option.Key == key) ?? All[1];
    }
}

/// <summary>A dynamic filter row in the advanced-search form; rows combine with AND.</summary>
public sealed partial class SearchFilterRowViewModel : ViewModelBase
{
    private readonly SearchEvidenceViewModel _owner;

    public SearchFilterRowViewModel(SearchEvidenceViewModel owner, SearchFilterKeyOption? key = null)
    {
        _owner = owner;
        Key = key ?? SearchFilterKeyOption.All[1];
        if (IsKeyword)
        {
            Value = _owner.Query;
        }

        RemoveCommand = new RelayCommand(_ => owner.RemoveFilterRow(this));
    }

    public RelayCommand RemoveCommand { get; }

    [ObservableProperty] public partial SearchFilterKeyOption Key { get; set; } = null!;

    partial void OnKeyChanged(SearchFilterKeyOption value)
    {
        if (!IsKeyword)
        {
            Value = "";
        }
        else
        {
            Value = _owner.Query;
        }

        Raise(nameof(AvailableValues));
    }

    public bool IsKeyword => Key.Key == SearchFilterKeyOption.KeywordKey;
    public bool CanRemove => !IsKeyword;

    public bool IsTextKey => Key.IsText;
    public bool IsChoiceKey => !Key.IsText;
    public string PlaceholderText => IsKeyword ? "关键词" : "输入包含的文本";

    [ExcludeFromDerivedGeneration]
    public IReadOnlyList<SearchFilterOption> AvailableValues => _owner.FilterOptionsFor(Key.Key);

    [ObservableProperty] public partial string Value { get; set; } = "";

    partial void OnValueChanged(string value)
    {
        if (IsKeyword)
        {
            if (_owner.Query != value)
            {
                _owner.Query = value;
            }
        }
    }

    public void RefreshValueFromQuery()
    {
        if (IsKeyword)
        {
            if (Value != _owner.Query)
            {
                Value = _owner.Query;
            }
            else
            {
                Raise(nameof(Value));
            }
        }
    }

    public BibliographicSearchFilter? ToFilter()
    {
        if (IsKeyword)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(Value) ? null : new BibliographicSearchFilter(Key.Key, Value.Trim());
    }

    public void RefreshAvailableValues()
    {
        Raise(nameof(AvailableValues));
    }
}
