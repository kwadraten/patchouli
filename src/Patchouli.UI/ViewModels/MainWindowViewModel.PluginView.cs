using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Patchouli.Mcp;
using Patchouli.UI.Views;

namespace Patchouli.UI.ViewModels;

/// <summary>
/// Plugin web-view opening entry: resolves the declared view through
/// <see cref="IMcpPluginApi.FindView"/>, enforces the device "view" grant and dispatches to the
/// declared container — a deduplicated workspace tab, a tracked non-modal window, or the PDF
/// workspace's right sidebar (falling back to a tab when no PDF workspace exists).
/// </summary>
public sealed partial class MainWindowViewModel
{
    private const string PluginViewPlacementTab = "tab";
    private const string PluginViewPlacementWindow = "window";
    private const string PluginViewPlacementSidebar = "pdf-sidebar";

    private readonly Dictionary<PluginViewWindowKey, IPluginViewWindow> _pluginViewWindows = [];

    /// <summary>Test seam: replaces real window construction for routing tests.</summary>
    internal Func<PluginViewSession, IPluginViewWindow>? PluginViewWindowFactory { get; set; }

    /// <summary>Test seam: substitutes the plugin API without starting host services.</summary>
    internal Func<IMcpPluginApi>? PluginApiProvider { get; set; }

    /// <summary>The plugin API — the provider override in tests, else live host services.</summary>
    internal async Task<IMcpPluginApi> PluginsAsync()
    {
        return PluginApiProvider?.Invoke() ?? (await ServicesAsync()).Plugins;
    }

    /// <summary>Opens a declared plugin view reached through patchouli://plugin/{id}/views/{view-id}.</summary>
    internal async Task OpenPluginViewAsync(string pluginId, string viewId)
    {
        IMcpPluginApi plugins = await PluginsAsync();
        McpPluginViewInfo? view = plugins.FindView(pluginId, viewId);
        if (view is null)
        {
            ReportError("插件视图不存在或未启用");
            return;
        }

        if (!plugins.AllowsVerb(pluginId, PluginViewSession.RequiredVerb))
        {
            ReportError($"插件「{pluginId}」的视图权限未授予（view）。");
            return;
        }

        switch (view.Placement)
        {
            case PluginViewPlacementTab:
                await ActivatePluginViewTabAsync(pluginId, view);
                return;
            case PluginViewPlacementWindow:
                await OpenPluginViewWindowAsync(pluginId, view);
                return;
            case PluginViewPlacementSidebar:
                await OpenPluginViewSidebarAsync(pluginId, view);
                return;
            default:
                ReportError($"插件视图「{view.Title}」的放置位置不受支持：{view.Placement}");
                return;
        }
    }

    /// <summary>Tab placement: one retained workspace tab per (pluginId, viewId).</summary>
    private Task ActivatePluginViewTabAsync(string pluginId, McpPluginViewInfo view)
    {
        return ActivateTabAsync(
            WorkspaceTabKind.PluginView,
            $"PluginView:{pluginId}:{view.ViewId}",
            view.Title,
            "Puzzle",
            true,
            () => new PluginViewViewModel(this, pluginId, view));
    }

    /// <summary>
    /// Window placement: a non-modal tracked popup per (pluginId, viewId); a second open
    /// request reactivates the existing window instead of duplicating it.
    /// </summary>
    private async Task OpenPluginViewWindowAsync(string pluginId, McpPluginViewInfo view)
    {
        PluginViewWindowKey key = new(pluginId, view.ViewId);
        if (_pluginViewWindows.TryGetValue(key, out IPluginViewWindow? existing))
        {
            existing.Activate();
            return;
        }

        PluginViewSession session = new(pluginId, view);
        IPluginViewWindow window = PluginViewWindowFactory?.Invoke(session)
                                   ?? new PluginViewWindow(session);
        _pluginViewWindows[key] = window;
        window.Closed += OnPluginViewWindowClosed;
        window.Show(ResolveMainWindow());
        await LoadPluginViewSessionAsync(session);
    }

    /// <summary>
    /// Sidebar placement: uses the active PDF workspace when the current tab is one, else the
    /// first open PdfWorkspace tab, else falls back to the tab container (page context stays
    /// null there — the view simply renders without a document attachment).
    /// </summary>
    private async Task OpenPluginViewSidebarAsync(string pluginId, McpPluginViewInfo view)
    {
        WorkspaceTabViewModel? pdfTab = Layout.ActiveTab is { Kind: WorkspaceTabKind.PdfWorkspace } active
            ? active
            : Layout.Tabs.FirstOrDefault(tab =>
                tab.Kind == WorkspaceTabKind.PdfWorkspace && tab.Content is PdfWorkspaceViewModel);
        if (pdfTab?.Content is not PdfWorkspaceViewModel pdf)
        {
            await ActivatePluginViewTabAsync(pluginId, view);
            return;
        }

        if (!ReferenceEquals(Layout.ActiveTab, pdfTab))
        {
            Workspace.Activate(pdfTab.TabId);
            await Layout.WaitForNavigationAsync();
        }

        await pdf.ActivatePluginViewAsync(view);
    }

    private async Task LoadPluginViewSessionAsync(PluginViewSession session)
    {
        try
        {
            IMcpPluginApi plugins = await PluginsAsync();
            if (!await session.LoadAsync(plugins) && session.ErrorText is { } error)
            {
                ReportError(error);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown/switch canceled the load; the container keeps the session's error surface.
        }
        catch (Exception exception)
        {
            ReportError($"插件视图加载失败：{exception.Message}");
        }
    }

    private void OnPluginViewWindowClosed(object? sender, EventArgs e)
    {
        foreach (KeyValuePair<PluginViewWindowKey, IPluginViewWindow> pair in _pluginViewWindows
                     .Where(pair => ReferenceEquals(pair.Value, sender))
                     .ToArray())
        {
            _pluginViewWindows.Remove(pair.Key);
        }

        if (sender is IPluginViewWindow window)
        {
            window.Closed -= OnPluginViewWindowClosed;
            window.Session.Release();
        }
    }

    /// <summary>Shutdown path: close every tracked plugin window so its webview is released.</summary>
    internal void CloseAllPluginViewWindows()
    {
        foreach (IPluginViewWindow window in _pluginViewWindows.Values.ToArray())
        {
            window.Closed -= OnPluginViewWindowClosed;
            try
            {
                window.Close();
            }
            catch (Exception exception)
            {
                UnexpectedExceptions.Sink.Report(exception, nameof(MainWindowViewModel), "close-plugin-view");
            }

            window.Session.Release();
        }

        _pluginViewWindows.Clear();
    }

    private static Window? ResolveMainWindow()
    {
        return (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow;
    }

    private readonly record struct PluginViewWindowKey(string PluginId, string ViewId);
}
