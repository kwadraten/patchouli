using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Patchouli.UI.ViewModels;

public sealed partial class WorkspaceTabViewModel : ViewModelBase
{
    [ObservableProperty] public partial string Title { get; set; }

    [ObservableProperty] public partial string IconName { get; set; }

    public string TabId { get; }
    public WorkspaceTabKind Kind { get; }

    public bool IsClosable { get; }
    public ICommand? CloseCommand { get; }
    public ViewModelBase Content { get; }

    public WorkspaceTabViewModel(WorkspaceTabKind kind, string tabId, string title, string iconName, bool isClosable,
        ICommand? closeCommand, ViewModelBase content)
    {
        Kind = kind;
        TabId = tabId;
        Title = title;
        IconName = iconName;
        IsClosable = isClosable;
        CloseCommand = closeCommand;
        Content = content;
    }
}
