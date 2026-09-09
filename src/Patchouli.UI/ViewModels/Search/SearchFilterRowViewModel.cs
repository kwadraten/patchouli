using Patchouli.Core.Search;

namespace Patchouli.UI.ViewModels;

public enum SearchMode
{
    Bibliographic,
    FullText
}

public sealed record SearchModeOption(SearchMode Mode, string Label, string Hint);

/// <summary>One selectable key in the advanced-search filter row.</summary>
public sealed record SearchFilterKeyOption(string Key, string Label, bool IsText)
{
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
        new(BibliographicSearchFilterKeys.Citable, "可引用", false)
    ];

    public static SearchFilterKeyOption For(string key)
    {
        return All.FirstOrDefault(option => option.Key == key) ?? All[1];
    }
}

/// <summary>A dynamic filter row in the advanced-search form; rows combine with AND.</summary>
public sealed class SearchFilterRowViewModel : ViewModelBase
{
    private readonly SearchEvidenceViewModel _owner;
    private SearchFilterKeyOption _key;
    private string _value = "";

    public SearchFilterRowViewModel(SearchEvidenceViewModel owner, SearchFilterKeyOption? key = null)
    {
        _owner = owner;
        _key = key ?? SearchFilterKeyOption.All[1];
        RemoveCommand = new RelayCommand(_ => owner.RemoveFilterRow(this));
    }

    public RelayCommand RemoveCommand { get; }

    public SearchFilterKeyOption Key
    {
        get => _key;
        set
        {
            if (_key == value)
            {
                return;
            }

            _key = value;
            if (!IsKeyword)
            {
                Value = "";
            }

            Raise();
            Raise(nameof(IsTextKey));
            Raise(nameof(IsChoiceKey));
            Raise(nameof(IsKeyword));
            Raise(nameof(CanRemove));
            Raise(nameof(AvailableValues));
        }
    }

    public bool IsKeyword => _key.Key == SearchFilterKeyOption.KeywordKey;
    public bool CanRemove => !IsKeyword;

    public bool IsTextKey => _key.IsText;
    public bool IsChoiceKey => !_key.IsText;
    public string PlaceholderText => IsKeyword ? "关键词" : "输入包含的文本";

    public IReadOnlyList<SearchFilterOption> AvailableValues => _owner.FilterOptionsFor(_key.Key);

    public string Value
    {
        get => IsKeyword ? _owner.Query : _value;
        set
        {
            if (IsKeyword)
            {
                _owner.Query = value;
                return;
            }

            if (_value == value)
            {
                return;
            }

            _value = value;
            Raise();
        }
    }

    public void RefreshValueFromQuery()
    {
        if (IsKeyword)
        {
            Raise(nameof(Value));
        }
    }

    public BibliographicSearchFilter? ToFilter()
    {
        if (IsKeyword)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(Value) ? null : new BibliographicSearchFilter(_key.Key, Value.Trim());
    }

    public void RefreshAvailableValues()
    {
        Raise(nameof(AvailableValues));
    }
}
