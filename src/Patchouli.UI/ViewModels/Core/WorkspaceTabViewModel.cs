using System.Windows.Input;

namespace Patchouli.UI.ViewModels;

public sealed class WorkspaceTabViewModel : ViewModelBase
{
    private string _title;
    private string _iconName;

    public string TabId { get; }
    public WorkspaceTabKind Kind { get; }
    public string Title
    {
        get => _title;
        set
        {
            if (_title == value) return;
            _title = value;
            Raise();
        }
    }

    public string IconName
    {
        get => _iconName;
        set
        {
            if (_iconName == value) return;
            _iconName = value;
            Raise();
        }
    }
    public bool IsClosable { get; }
    public ICommand? CloseCommand { get; }
    public ViewModelBase Content { get; }

    public WorkspaceTabViewModel(WorkspaceTabKind kind, string tabId, string title, string iconName, bool isClosable, ICommand? closeCommand, ViewModelBase content)
    {
        Kind = kind;
        TabId = tabId;
        _title = title;
        _iconName = iconName;
        IsClosable = isClosable;
        CloseCommand = closeCommand;
        Content = content;
    }
}


