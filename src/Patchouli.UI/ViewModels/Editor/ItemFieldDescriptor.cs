using System.Collections.ObjectModel;
using Patchouli.UI.ViewModels;

namespace Patchouli.UI.ViewModels.Editor;

public sealed class ItemFieldDescriptor : ViewModelBase
{
    private string _value = "";

    public string Key { get; }
    public string Label { get; }
    public string Type { get; } // "String", "MultilineString", "Date", "CreatorList"

    public bool IsString => Type == "String";
    public bool IsMultilineString => Type == "MultilineString";
    public bool IsDate => Type == "Date";
    public bool IsCreatorList => Type == "CreatorList";

    public ItemFieldDescriptor(string key, string label, string type)
    {
        Key = key;
        Label = label;
        Type = type;
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
        }
    }

    // specific for CreatorList
    public ObservableCollection<CreatorItemViewModel> Creators { get; } = new();
    public AsyncCommand? AddCreatorCommand { get; set; }

    // specific for Date (could just use Value as string, but maybe later we need more)
}
