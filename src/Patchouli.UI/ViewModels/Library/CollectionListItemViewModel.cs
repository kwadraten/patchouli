using Patchouli.Core.Ids;
using Patchouli.UI.ViewModels.Core;

namespace Patchouli.UI.ViewModels;

/// <summary>
/// A single entry in the library sidebar collection list. Collections are one-level playlists:
/// selecting one filters the item grid to its members.
/// </summary>
public sealed class CollectionListItemViewModel : ViewModelBase
{
    private bool _isSelected;

    public CollectionListItemViewModel(CollectionId collectionId, string name, int itemCount)
    {
        CollectionId = collectionId;
        Name = name;
        ItemCount = itemCount;
        RenameCommand = new AsyncCommand(() => RequestRename?.Invoke(this) ?? Task.CompletedTask);
        DissolveCommand = new AsyncCommand(() => RequestDissolve?.Invoke(this) ?? Task.CompletedTask);
        AddToSelectionCommand = new AsyncCommand(() => RequestAddToSelection?.Invoke(this) ?? Task.CompletedTask);
    }

    public CollectionId CollectionId { get; }
    public string Name { get; }
    public int ItemCount { get; }

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
        }
    }

    public string CountText => ItemCount > 0 ? ItemCount.ToString() : "";
    public bool HasCount => ItemCount > 0;

    public AsyncCommand RenameCommand { get; }
    public AsyncCommand DissolveCommand { get; }
    public AsyncCommand AddToSelectionCommand { get; }

    public Func<CollectionListItemViewModel, Task>? RequestRename { get; set; }
    public Func<CollectionListItemViewModel, Task>? RequestDissolve { get; set; }
    public Func<CollectionListItemViewModel, Task>? RequestAddToSelection { get; set; }
}
