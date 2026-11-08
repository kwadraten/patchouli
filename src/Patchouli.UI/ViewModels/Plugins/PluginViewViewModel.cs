using Patchouli.Mcp;

namespace Patchouli.UI.ViewModels;

/// <summary>
/// Workspace-tab container for one plugin web view (placement "tab"). Loads the session HTML
/// once on first activation and releases it when the tab closes; the visual
/// <see cref="Controls.PluginWebViewHost"/> owns the actual NativeWebView lifetime.
/// </summary>
public sealed class PluginViewViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private int _loadStarted;

    public PluginViewViewModel(MainWindowViewModel main, string pluginId, McpPluginViewInfo view)
    {
        ArgumentNullException.ThrowIfNull(main);
        _main = main;
        PluginId = pluginId;
        View = view;
        Session = new PluginViewSession(pluginId, view);
    }

    public string PluginId { get; }
    public McpPluginViewInfo View { get; }
    public PluginViewSession Session { get; }
    public string Title => View.Title;

    /// <summary>Idempotent first load, triggered by the page on attach/activation.</summary>
    internal async Task EnsureLoadedAsync()
    {
        if (Interlocked.Exchange(ref _loadStarted, 1) != 0)
        {
            return;
        }

        try
        {
            IMcpPluginApi plugins = await _main.PluginsAsync();
            if (await Session.LoadAsync(plugins))
            {
                _main.Report($"已打开插件视图：{View.Title}");
            }
            else if (Session.ErrorText is { } error)
            {
                _main.ReportError(error);
            }
        }
        catch (OperationCanceledException)
        {
            // Library shutdown/switch canceled the load; the tab surfaces the session state.
        }
        catch (Exception exception)
        {
            _main.ReportError($"插件视图加载失败：{exception.Message}");
        }
    }

    /// <summary>Drops the session when the owning tab is closed (webview is released by the page).</summary>
    internal void OnClosed()
    {
        Session.Release();
    }
}
