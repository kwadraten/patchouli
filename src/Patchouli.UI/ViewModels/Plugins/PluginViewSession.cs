using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Results;
using Patchouli.Mcp;

namespace Patchouli.UI.ViewModels;

/// <summary>
/// One live plugin web-view session: the declared view, the permission-gated HTML payload
/// fetched through <see cref="IMcpPluginApi.FetchAsync"/>, the load/error surface for the
/// hosting container, and the host-provided page context (<see cref="PageUri"/> — non-null
/// only for pdf-sidebar placements). The session is pure view-model: it never touches
/// <c>NativeWebView</c>, so headless tests exercise it fully.
/// </summary>
public sealed partial class PluginViewSession : ViewModelBase
{
    /// <summary>The registry grant verb required before any view content is fetched.</summary>
    public const string RequiredVerb = "view";

    private int _loadGeneration;
    private CancellationTokenSource? _loadCancellation;

    public PluginViewSession(string pluginId, McpPluginViewInfo view)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(view);
        PluginId = pluginId;
        View = view;
    }

    public string PluginId { get; }
    public McpPluginViewInfo View { get; }
    [ExcludeFromDerivedGeneration] public string Title => View.Title;

    /// <summary>True while the view HTML fetch is in flight.</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>The fetched view HTML; null until the first successful load.</summary>
    [ObservableProperty]
    public partial string? Html { get; private set; }

    /// <summary>User-facing load failure text (permission denied, fetch error); null on success.</summary>
    [ObservableProperty]
    public partial string? ErrorText { get; private set; }

    /// <summary>
    /// Canonical URI of the PDF workspace page this view's context should track, or null for
    /// tab/window placements. Written by the owning workspace on page/revision changes.
    /// </summary>
    [ObservableProperty]
    public partial string? PageUri { get; internal set; }

    /// <summary>
    /// Permission gate + content fetch. Rejects when the device registry does not grant the
    /// "view" verb, then fetches <see cref="McpPluginViewInfo.SourcePath"/> from the plugin.
    /// A superseding call or <see cref="Release"/> discards the in-flight result so a stale
    /// load can never repopulate a closed/reused session.
    /// </summary>
    public async Task<bool> LoadAsync(IMcpPluginApi plugins, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        int generation = ++_loadGeneration;
        CancellationTokenSource load = ReplaceLoadCancellation(cancellationToken);
        IsLoading = true;
        ErrorText = null;
        try
        {
            if (!plugins.AllowsVerb(PluginId, RequiredVerb))
            {
                ErrorText = "插件视图权限未授予（view）。";
                return false;
            }

            Result<McpPluginFetchContent> result =
                await plugins.FetchAsync(PluginId, View.SourcePath, load.Token);
            if (generation != _loadGeneration)
            {
                return false;
            }

            if (result.IsFailure)
            {
                ErrorText = $"插件视图加载失败：{result.ErrorCode} {result.ErrorMessage}";
                return false;
            }

            Html = result.Value.Content;
            return true;
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            if (generation == _loadGeneration)
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>
    /// Detaches the session from its container: cancels the in-flight fetch and drops the
    /// loaded HTML and page context so a released view can never be repopulated by a late
    /// response.
    /// </summary>
    public void Release()
    {
        _loadGeneration++;
        if (_loadCancellation is not null)
        {
            _loadCancellation.Cancel();
            _loadCancellation.Dispose();
            _loadCancellation = null;
        }

        IsLoading = false;
        Html = null;
        PageUri = null;
    }

    private CancellationTokenSource ReplaceLoadCancellation(CancellationToken linked)
    {
        if (_loadCancellation is not null)
        {
            _loadCancellation.Cancel();
            _loadCancellation.Dispose();
        }

        _loadCancellation = linked.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(linked)
            : new CancellationTokenSource();
        return _loadCancellation;
    }
}
