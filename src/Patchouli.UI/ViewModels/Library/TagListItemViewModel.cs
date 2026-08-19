using Patchouli.UI.ViewModels.Core;

namespace Patchouli.UI.ViewModels;

/// <summary>
/// A single entry in the library sidebar tag list. A normal entry represents a tag;
/// <see cref="IsNoTagEntry"/> represents the fixed "no tag" filter item.
/// </summary>
public sealed class TagListItemViewModel : ViewModelBase
{
    private bool _isPinned;
    private bool _isSelected;

    public TagListItemViewModel(
        string name,
        int count,
        bool isPinned,
        bool isNoTagEntry)
    {
        Name = name;
        Count = count;
        IsNoTagEntry = isNoTagEntry;
        _isPinned = isPinned;
        TogglePinCommand = new AsyncCommand(() => RequestTogglePin?.Invoke(this) ?? Task.CompletedTask);
        RemoveCommand = new AsyncCommand(() => RequestRemove?.Invoke(this) ?? Task.CompletedTask);
        RenameCommand = new AsyncCommand(() => RequestRename?.Invoke(this) ?? Task.CompletedTask);
        MergeIntoCommand = new AsyncCommand(() => RequestMergeInto?.Invoke(this) ?? Task.CompletedTask);
    }

    public string Name { get; }
    public int Count { get; }
    public bool IsNoTagEntry { get; }

    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (_isPinned == value)
            {
                return;
            }

            _isPinned = value;
            Raise();
        }
    }

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

    public string DisplayText => IsNoTagEntry ? "无标签" : Name;
    public string CountText => Count > 0 ? Count.ToString() : "";

    public AsyncCommand TogglePinCommand { get; }
    public AsyncCommand RemoveCommand { get; }
    public AsyncCommand RenameCommand { get; }
    public AsyncCommand MergeIntoCommand { get; }

    public Func<TagListItemViewModel, Task>? RequestTogglePin { get; set; }
    public Func<TagListItemViewModel, Task>? RequestRemove { get; set; }
    public Func<TagListItemViewModel, Task>? RequestRename { get; set; }
    public Func<TagListItemViewModel, Task>? RequestMergeInto { get; set; }
}
