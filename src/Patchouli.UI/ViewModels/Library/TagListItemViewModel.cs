using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.UI.ViewModels.Core;

namespace Patchouli.UI.ViewModels;

/// <summary>
/// A single entry in the library sidebar tag list. A normal entry represents a tag;
/// <see cref="IsNoTagEntry"/> represents the fixed "no tag" filter item.
/// </summary>
public sealed partial class TagListItemViewModel : ViewModelBase
{
    public TagListItemViewModel(
        string name,
        int count,
        bool isPinned,
        bool isNoTagEntry)
    {
        Name = name;
        Count = count;
        IsNoTagEntry = isNoTagEntry;
        IsPinned = isPinned;
        TogglePinCommand = new AsyncCommand(() => RequestTogglePin?.Invoke(this) ?? Task.CompletedTask);
        RemoveCommand = new AsyncCommand(() => RequestRemove?.Invoke(this) ?? Task.CompletedTask);
        RenameCommand = new AsyncCommand(() => RequestRename?.Invoke(this) ?? Task.CompletedTask);
        MergeIntoCommand = new AsyncCommand(() => RequestMergeInto?.Invoke(this) ?? Task.CompletedTask);
    }

    public string Name { get; }
    public int Count { get; }
    public bool IsNoTagEntry { get; }

    [ObservableProperty] public partial bool IsPinned { get; set; }

    [ObservableProperty] public partial bool IsSelected { get; set; }

    public string DisplayText => IsNoTagEntry ? "无标签" : Name;

    [ExcludeFromDerivedGeneration] public string CountText => Count > 0 ? Count.ToString() : "";

    [ExcludeFromDerivedGeneration] public bool HasCount => Count > 0;

    /// <summary>"无标签" is a fixed filter entry, not a real tag, so pinning is not offered.</summary>
    public bool CanPin => !IsNoTagEntry;

    public AsyncCommand TogglePinCommand { get; }

    public AsyncCommand RemoveCommand { get; }

    public AsyncCommand RenameCommand { get; }

    public AsyncCommand MergeIntoCommand { get; }

    public Func<TagListItemViewModel, Task>? RequestTogglePin { get; set; }

    public Func<TagListItemViewModel, Task>? RequestRemove { get; set; }

    public Func<TagListItemViewModel, Task>? RequestRename { get; set; }

    public Func<TagListItemViewModel, Task>? RequestMergeInto { get; set; }
}
