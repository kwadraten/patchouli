using Patchouli.Core.Ids;
using Patchouli.UI.ViewModels.Core;

namespace Patchouli.UI.ViewModels.Editor;

/// <summary>
/// One selectable Collection membership checkbox in the Item editor. Selection is staged in the
/// editor and persisted as a full membership replacement when the Item is saved.
/// </summary>
public sealed class CollectionSelectionItemViewModel : ViewModelBase
{
    private readonly Action? _onSelectionChanged;
    private bool _isSelected;

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

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            Raise();
            _onSelectionChanged?.Invoke();
        }
    }
}
