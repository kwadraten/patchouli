using System.Collections.ObjectModel;
using Patchouli.Core.Bibliography;
using Patchouli.UI.ViewModels;

namespace Patchouli.UI.ViewModels.Editor;

public sealed class ItemFieldDescriptor : ViewModelBase
{
    private string _value = "";

    public string Key { get; }
    public string Label { get; }

    public string
        Type { get; } // "String", "MultilineString", "Date", "CreatorList", "IdentifierBacked", "ExtraCslBacked"

    /// <summary>The identifier scheme this field projects, when <see cref="Type" /> is "IdentifierBacked".</summary>
    public string? IdentifierScheme { get; }

    /// <summary>The extra-CSL variable this field projects, when <see cref="Type" /> is "ExtraCslBacked".</summary>
    public string? ExtraCslVariableKey { get; }

    /// <summary>Invoked after <see cref="Value" /> changes; used by identifier-backed projection fields.</summary>
    public Action<ItemFieldDescriptor, string>? ValueChanged { get; set; }

    /// <summary>Set by the host on identifier-backed fields; only surfaced for the URL projection.</summary>
    public AsyncCommand? LookupFromUrlCommand { get; set; }

    /// <summary>The URL projection field offers extracting an identifier from the URL and fetching metadata.</summary>
    public bool ShowsLookupButton => IsIdentifierBacked &&
                                     string.Equals(IdentifierScheme, BuiltInIdentifierSchemes.URL,
                                         StringComparison.Ordinal) &&
                                     LookupFromUrlCommand is not null;

    public bool IsString => Type == "String";
    public bool IsMultilineString => Type == "MultilineString";
    public bool IsDate => Type == "Date";
    public bool IsCreatorList => Type == "CreatorList";
    public bool IsIdentifierBacked => Type == "IdentifierBacked";
    public bool IsExtraCslBacked => Type == "ExtraCslBacked";

    public ItemFieldDescriptor(string key, string label, string type, string? identifierScheme = null,
        string? extraCslVariableKey = null)
    {
        Key = key;
        Label = label;
        Type = type;
        IdentifierScheme = identifierScheme;
        ExtraCslVariableKey = extraCslVariableKey;
    }

    public string Value
    {
        get => _value;
        set
        {
            if (_value == value)
            {
                return;
            }

            _value = value;
            Raise();
            ValueChanged?.Invoke(this, value);
        }
    }

    // specific for CreatorList
    public ObservableCollection<CreatorItemViewModel> Creators { get; } = new();
    public AsyncCommand? AddCreatorCommand { get; set; }

    // specific for Date (could just use Value as string, but maybe later we need more)
}
