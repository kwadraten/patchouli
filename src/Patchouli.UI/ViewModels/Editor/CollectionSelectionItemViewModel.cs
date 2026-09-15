using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Ids;
using Patchouli.UI.ViewModels.Core;

namespace Patchouli.UI.ViewModels.Editor;

/// <summary>
/// One selectable Collection membership checkbox in the Item editor. Selection is staged in the
/// editor and persisted as a full membership replacement when the Item is saved.
/// </summary>
public sealed partial class CollectionSelectionItemViewModel : ViewModelBase
{
    private readonly Action? _onSelectionChanged;

    [ObservableProperty] private bool _isSelected;

    public CollectionSelectionItemViewModel(CollectionId collectionId, string name, bool isSelected,
        Action? onSelectionChanged = null)
    {
        CollectionId = collectionId;
        Name = name;
        _isSelected = isSelected;
        _onSelectionChanged = onSelectionChanged;
    }

    public CollectionId CollectionId { get; }
    public string Name { get; }

    partial void OnIsSelectedChanged(bool value)
    {
        _onSelectionChanged?.Invoke();
    }
}
