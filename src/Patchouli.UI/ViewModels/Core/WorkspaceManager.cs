namespace Patchouli.UI.ViewModels;

public sealed class WorkspaceManager
{
    public WorkspaceManager(WorkspaceLayoutViewModel layout)
    {
        Layout = layout;
    }

    public WorkspaceLayoutViewModel Layout { get; }

    public WorkspaceTabViewModel OpenOrActivate(
        WorkspaceTabKind kind,
        string tabId,
        string title,
        string iconName,
        bool isClosable,
        Func<ViewModelBase> contentFactory)
    {
        var tab = Find(tabId);
        if (tab is null)
        {
            tab = new WorkspaceTabViewModel(
                kind,
                tabId,
                title,
                iconName,
                isClosable,
                isClosable ? new AsyncCommand(() => CloseAsync(tabId)) : null,
                contentFactory());
            Layout.Tabs.Add(tab);
        }

        Layout.ActiveTab = tab;
        return tab;
    }

    public bool Activate(string tabId)
    {
        var tab = Find(tabId);
        if (tab is null) return false;

        Layout.ActiveTab = tab;
        return true;
    }

    public bool ActivateKind(WorkspaceTabKind kind)
    {
        var tab = FindKind(kind);
        if (tab is null) return false;

        Layout.ActiveTab = tab;
        return true;
    }

    public bool Close(string tabId)
    {
        var tab = Find(tabId);
        if (tab is null || !tab.IsClosable) return false;

        var wasActive = Layout.ActiveTab == tab;
        Layout.Tabs.Remove(tab);
        Cleanup(tab);

        if (wasActive || Layout.ActiveTab is null)
        {
            Layout.ActiveTab = FindKind(WorkspaceTabKind.Library) ?? Layout.Tabs.FirstOrDefault();
        }

        return true;
    }

    public int CloseKind(WorkspaceTabKind kind)
    {
        var tabs = Layout.Tabs.Where(tab => tab.Kind == kind).ToList();
        var closed = 0;
        foreach (var tab in tabs)
        {
            if (Close(tab.TabId)) closed++;
        }

        return closed;
    }

    public WorkspaceTabViewModel? Find(string tabId) =>
        Layout.Tabs.FirstOrDefault(tab => tab.TabId == tabId);

    public WorkspaceTabViewModel? FindKind(WorkspaceTabKind kind) =>
        Layout.Tabs.FirstOrDefault(tab => tab.Kind == kind);

    private Task CloseAsync(string tabId)
    {
        Close(tabId);
        return Task.CompletedTask;
    }

    private static void Cleanup(WorkspaceTabViewModel tab)
    {
        if (tab.Content is PdfWorkspaceViewModel pdf)
        {
            pdf.Clear();
        }
    }
}
