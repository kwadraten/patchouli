using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Mcp;
using Patchouli.UI.Services;

namespace Patchouli.UI.ViewModels;

/// <summary>
/// pdf-sidebar plugin views: the sidebar's plugin tab hosts one web view declared by a plugin
/// ([[ui.views]] placement "pdf-sidebar"). The workspace tracks the active view/session, offers
/// the eligible views of every enabled mount for the sidebar menu, and continuously projects the
/// canonical URI of the page the workspace is showing (<see cref="CurrentPageUri"/>) into the
/// session so the web view's pushed context always reflects the live document position.
/// </summary>
public sealed partial class PdfWorkspaceViewModel
{
    /// <summary>The declared view hosted by the sidebar's plugin tab, or null.</summary>
    [ObservableProperty]
    public partial McpPluginViewInfo? ActivePluginView { get; private set; }

    /// <summary>The live session feeding the plugin web view (HTML, errors, page context).</summary>
    [ObservableProperty]
    public partial PluginViewSession? ActivePluginSession { get; private set; }

    /// <summary>Canonical URI of the currently rendered page (with ?rev= of the committed
    /// revision when one is loaded); null when no document is loaded.</summary>
    [ObservableProperty]
    public partial string? CurrentPageUri { get; private set; }

    /// <summary>Every mount's declared pdf-sidebar views, refreshed on workspace load.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<McpPluginViewInfo> AvailablePluginSidebarViews { get; private set; } = [];

    /// <summary>Menu-bound projection of <see cref="AvailablePluginSidebarViews"/>.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PluginViewMenuEntry> PluginSidebarMenuEntries { get; private set; } = [];

    /// <summary>True while no enabled mount declares a pdf-sidebar view.</summary>
    [ObservableProperty]
    public partial bool HasNoPluginSidebarViews { get; private set; } = true;

    /// <summary>True while the plugin view tab is the active sidebar surface.</summary>
    public bool IsPluginTabActive =>
        !IsEditMode && !IsBookReadingMode && ActiveSidebarTab == SidebarTab.Plugin;

    /// <summary>
    /// Activates a declared view inside the sidebar: opens the sidebar, switches to the plugin
    /// tab (resolving a pending edit session like every other tab switch) and loads the view
    /// HTML through the permission gate.
    /// </summary>
    public async Task ActivatePluginViewAsync(McpPluginViewInfo view)
    {
        ArgumentNullException.ThrowIfNull(view);
        string? pluginId = TryParsePluginId(view.Uri);
        if (pluginId is null)
        {
            Status = $"插件视图 URI 无效：{view.Uri}";
            return;
        }

        PluginViewSession session = new(pluginId, view);
        _main.ConfigurePluginViewSession(session);
        ActivePluginSession?.Release();
        ActivePluginView = view;
        ActivePluginSession = session;
        IsSidebarOpen = true;
        await SwitchSidebarTabAsync(SidebarTab.Plugin);
        // The session loads even when a pending edit blocked the tab switch: it stays hidden
        // (IsPluginTabActive is false) and renders once the user resolves the edit.
        PushPluginViewPageUri();
        try
        {
            IMcpPluginApi plugins = await _main.PluginsAsync();
            if (!await session.LoadAsync(plugins, _workspaceCancellation.Token) &&
                session.ErrorText is { } error)
            {
                Status = error;
            }
        }
        catch (OperationCanceledException)
        {
            // Workspace teardown canceled the load.
        }
        catch (Exception exception)
        {
            session.Release();
            Status = $"插件视图加载失败：{exception.Message}";
        }
    }

    internal async Task RemovePluginViewsAsync(string pluginId)
    {
        if (ActivePluginView is { } view &&
            string.Equals(TryParsePluginId(view.Uri), pluginId, StringComparison.Ordinal))
        {
            ActivePluginSession?.Release();
            ActivePluginSession = null;
            ActivePluginView = null;
            if (ActiveSidebarTab == SidebarTab.Plugin)
            {
                ActiveSidebarTab = SidebarTab.Content;
            }
        }

        await RefreshPluginSidebarViewsAsync();
    }

    /// <summary>Rebuilds the sidebar's plugin-view menu from every enabled mount's declarations.</summary>
    private async Task RefreshPluginSidebarViewsAsync()
    {
        try
        {
            IMcpPluginApi plugins = await _main.PluginsAsync();
            List<McpPluginViewInfo> views = [];
            foreach (McpPluginMountInfo mount in plugins.Mounts)
            {
                if (!plugins.AllowsVerb(mount.PluginId, PluginViewSession.RequiredVerb))
                {
                    continue;
                }

                foreach (McpPluginViewInfo view in plugins.Views(mount.PluginId))
                {
                    if (string.Equals(view.Placement, "pdf-sidebar", StringComparison.Ordinal))
                    {
                        views.Add(view);
                    }
                }
            }

            AvailablePluginSidebarViews = views;
            PluginSidebarMenuEntries = views
                .Select(view => new PluginViewMenuEntry(view, ActivatePluginViewAsync))
                .ToArray();
            HasNoPluginSidebarViews = views.Count == 0;
        }
        catch (OperationCanceledException)
        {
            AvailablePluginSidebarViews = [];
            PluginSidebarMenuEntries = [];
            HasNoPluginSidebarViews = true;
        }
    }

    /// <summary>
    /// Recomputes the canonical page URI and pushes it into the active plugin session. Called
    /// on every page-index change, committed/historical revision switch and workspace clear;
    /// the URI is null until a document has actually been loaded into the workspace.
    /// </summary>
    private void PushPluginViewPageUri()
    {
        CurrentPageUri = _loadedDocumentInstanceId is { } documentId
            ? PatchouliUriNavigationParser.BuildTextPageUri(documentId, PageIndex, _currentRevisionId)
            : null;
        if (ActivePluginSession is { } session)
        {
            session.PageUri = CurrentPageUri;
        }
    }

    /// <summary>Drops the plugin view session on workspace clear/teardown.</summary>
    private void DeactivatePluginView()
    {
        ActivePluginSession?.Release();
        ActivePluginSession = null;
        ActivePluginView = null;
        AvailablePluginSidebarViews = [];
        PluginSidebarMenuEntries = [];
        HasNoPluginSidebarViews = true;
        if (ActiveSidebarTab == SidebarTab.Plugin)
        {
            ActiveSidebarTab = SidebarTab.Content;
        }
    }

    /// <summary>Extracts the plugin id from a view URI (patchouli://plugin/{id}/views/{view-id}).</summary>
    internal static string? TryParsePluginId(string uri)
    {
        Result<McpUriParseResult> parsed = McpResourceUris.Parse(uri);
        return parsed.IsSuccess && parsed.Value.Kind == McpUriKind.PluginResource
            ? parsed.Value.PluginId
            : null;
    }
}
