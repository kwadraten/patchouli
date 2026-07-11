using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace Patchouli.UI.ViewModels;

public sealed class WorkspaceLayoutViewModel : ViewModelBase
{
    private WorkspaceTabViewModel? _activeTab;
    private bool _showInspectorPane = true;

    public WorkspaceLayoutViewModel()
    {
        Tabs.CollectionChanged += OnTabsChanged;
    }

    public ObservableCollection<WorkspaceTabViewModel> Tabs { get; } = new();

    public WorkspaceTabViewModel? ActiveTab
    {
        get => _activeTab;
        set
        {
            if (_activeTab == value)
            {
                return;
            }

            _activeTab = value;
            Raise();
            RaiseActiveDerivedProperties();
        }
    }

    public bool ShowInspectorPane
    {
        get => _showInspectorPane;
        set
        {
            if (_showInspectorPane == value)
            {
                return;
            }

            _showInspectorPane = value;
            Raise();
            Raise(nameof(IsInspectorVisible));
        }
    }

    public bool ShowSidebar => ActiveTab?.Kind == WorkspaceTabKind.Library;
    public bool IsInspectorVisible => ActiveTab?.Kind == WorkspaceTabKind.Library && ShowInspectorPane;
    public bool HasPdfWorkspaceTab => Tabs.Any(tab => tab.Kind == WorkspaceTabKind.PdfWorkspace);
    public bool HasSettingsTab => Tabs.Any(tab => tab.Kind == WorkspaceTabKind.Settings);
    public bool HasItemEditorTab => Tabs.Any(tab => tab.Kind == WorkspaceTabKind.ItemEditor);
    public bool IsLibraryActive => ActiveTab?.Kind == WorkspaceTabKind.Library;
    public bool IsReaderActive => ActiveTab?.Kind == WorkspaceTabKind.PdfWorkspace;
    public bool IsSettingsActive => ActiveTab?.Kind == WorkspaceTabKind.Settings;
    public bool IsItemEditorActive => ActiveTab?.Kind == WorkspaceTabKind.ItemEditor;

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RaiseTabCollectionDerivedProperties();
    }

    private void RaiseActiveDerivedProperties()
    {
        Raise(nameof(ShowSidebar));
        Raise(nameof(IsInspectorVisible));
        Raise(nameof(IsLibraryActive));
        Raise(nameof(IsReaderActive));
        Raise(nameof(IsSettingsActive));
        Raise(nameof(IsItemEditorActive));
    }

    private void RaiseTabCollectionDerivedProperties()
    {
        Raise(nameof(HasPdfWorkspaceTab));
        Raise(nameof(HasSettingsTab));
        Raise(nameof(HasItemEditorTab));
    }
}
