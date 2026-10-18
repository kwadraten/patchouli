using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Bibliography;
using Patchouli.UI.ViewModels;

namespace Patchouli.UI.ViewModels.Editor;

public sealed partial class ItemFieldDescriptor : ViewModelBase
{
    private bool _isUpdatingLanguageSelection;

    private static readonly IReadOnlyList<LanguageOption> KnownLanguages = Array.AsReadOnly(new LanguageOption[]
    {
        new("", "未指定"),
        new("ar", "阿拉伯语"), new("cs", "捷克语"), new("da", "丹麦语"), new("de", "德语"),
        new("el", "希腊语"), new("en", "英语"), new("es", "西班牙语"), new("fi", "芬兰语"),
        new("fr", "法语"), new("he", "希伯来语"), new("hi", "印地语"), new("hu", "匈牙利语"),
        new("id", "印度尼西亚语"), new("it", "意大利语"), new("ja", "日语"), new("ko", "韩语"),
        new("nl", "荷兰语"), new("no", "挪威语"), new("pl", "波兰语"), new("pt", "葡萄牙语"),
        new("ro", "罗马尼亚语"), new("ru", "俄语"), new("sv", "瑞典语"), new("th", "泰语"),
        new("tr", "土耳其语"), new("uk", "乌克兰语"), new("vi", "越南语"), new("zh", "中文")
    });

    [ObservableProperty] private string _value = "";
    [ObservableProperty] private LanguageOption? _selectedLanguageOption;

    public string Key { get; }
    public string Label { get; }

    public string
        Type
    {
        get;
    } // "String", "MultilineString", "Note", "Language", "Date", "CreatorList", "IdentifierBacked", "ExtraCslBacked"

    /// <summary>The identifier scheme this field projects, when <see cref="Type" /> is "IdentifierBacked".</summary>
    public string? IdentifierScheme { get; }

    /// <summary>The extra-CSL variable this field projects, when <see cref="Type" /> is "ExtraCslBacked".</summary>
    public string? ExtraCslVariableKey { get; }

    /// <summary>Invoked after <see cref="Value" /> changes; used by identifier-backed projection fields.</summary>
    public Action<ItemFieldDescriptor, string>? ValueChanged { get; set; }

    /// <summary>Set by the host on identifier-backed fields; only surfaced for the URL projection.</summary>
    public AsyncCommand? LookupFromUrlCommand { get; set; }

    /// <summary>The URL projection field offers extracting an identifier from the URL and fetching metadata.</summary>
    [ExcludeFromDerivedGeneration]
    public bool ShowsLookupButton => IsIdentifierBacked &&
                                     string.Equals(IdentifierScheme, BuiltInIdentifierSchemes.URL,
                                         StringComparison.Ordinal) &&
                                     LookupFromUrlCommand is not null;

    [ExcludeFromDerivedGeneration] public bool IsString => Type == "String";
    [ExcludeFromDerivedGeneration] public bool IsMultilineString => Type == "MultilineString";
    [ExcludeFromDerivedGeneration] public bool IsNoteLineEditor => Type == "Note";
    [ExcludeFromDerivedGeneration] public bool IsLanguage => Type == "Language";

    [ExcludeFromDerivedGeneration]
    public IReadOnlyList<LanguageOption> LanguageOptions => !IsLanguage || string.IsNullOrEmpty(Value) ||
                                                            KnownLanguages.Any(option => option.Code == Value)
        ? KnownLanguages
        : Array.AsReadOnly(KnownLanguages.Append(ResolveLanguageOption(Value)).ToArray());

    [ExcludeFromDerivedGeneration] public bool IsDate => Type == "Date";
    [ExcludeFromDerivedGeneration] public bool IsCreatorList => Type == "CreatorList";
    [ExcludeFromDerivedGeneration] public bool IsIdentifierBacked => Type == "IdentifierBacked";
    [ExcludeFromDerivedGeneration] public bool IsExtraCslBacked => Type == "ExtraCslBacked";

    public ItemFieldDescriptor(string key, string label, string type, string? identifierScheme = null,
        string? extraCslVariableKey = null)
    {
        Key = key;
        Label = label;
        Type = type;
        IdentifierScheme = identifierScheme;
        ExtraCslVariableKey = extraCslVariableKey;
        if (IsLanguage)
        {
            SelectedLanguageOption = ResolveLanguageOption(Value);
        }
    }

    partial void OnValueChanged(string value)
    {
        if (IsLanguage && !_isUpdatingLanguageSelection)
        {
            _isUpdatingLanguageSelection = true;
            try
            {
                Raise(nameof(LanguageOptions));
                SelectedLanguageOption = ResolveLanguageOption(value);
            }
            finally
            {
                _isUpdatingLanguageSelection = false;
            }
        }

        ValueChanged?.Invoke(this, value);
    }

    partial void OnSelectedLanguageOptionChanged(LanguageOption? value)
    {
        // Avalonia can clear SelectedItem while a ComboBox is being detached or refreshed. Only the
        // explicit "未指定" row represents a user request to clear the stored language.
        if (!IsLanguage || _isUpdatingLanguageSelection)
        {
            return;
        }

        if (value is null)
        {
            _isUpdatingLanguageSelection = true;
            try
            {
                SelectedLanguageOption = ResolveLanguageOption(Value);
            }
            finally
            {
                _isUpdatingLanguageSelection = false;
            }

            return;
        }

        _isUpdatingLanguageSelection = true;
        try
        {
            Value = value.Code;
        }
        finally
        {
            _isUpdatingLanguageSelection = false;
        }
    }

    private static LanguageOption ResolveLanguageOption(string value)
    {
        LanguageOption? exact = KnownLanguages.FirstOrDefault(option => option.Code == value);
        if (exact is not null)
        {
            return exact;
        }

        string? normalized = ItemLanguage.Normalize(value);
        string? primaryLanguage = ItemLanguage.GetPrimaryLanguage(normalized);
        string? knownName = KnownLanguages.FirstOrDefault(option => option.Code == primaryLanguage)?.DisplayName;
        string displayName = knownName is null
            ? $"保留原值（{value}）"
            : $"{knownName}（{value}，保留原值）";
        return new LanguageOption(value, displayName);
    }

    // specific for CreatorList
    public ObservableCollection<CreatorItemViewModel> Creators { get; } = new();
    public AsyncCommand? AddCreatorCommand { get; set; }

    // specific for Date (could just use Value as string, but maybe later we need more)
}

public sealed record LanguageOption(string Code, string DisplayName);
