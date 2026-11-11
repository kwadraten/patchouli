using Patchouli.Mcp;

namespace Patchouli.UI.ViewModels;

/// <summary>
/// One row in the main window's 扩展功能 menu: the fixed 插件与扩展管理 entry, or a view row
/// carrying the command that opens the declared view through <c>OpenPluginViewAsync</c>. View
/// rows are always flat second-level items; when several plugins contribute views their plugin
/// name prefixes the view title.
/// </summary>
public sealed class ExtensionMenuEntryViewModel
{
    private ExtensionMenuEntryViewModel(string header, AsyncCommand openCommand)
    {
        Header = header;
        OpenCommand = openCommand;
    }

    /// <summary>The menu caption: the view title or the plugin-prefixed view title.</summary>
    public string Header { get; }

    /// <summary>The command executed when the menu item is clicked.</summary>
    public AsyncCommand OpenCommand { get; }

    /// <summary>The fixed entry that opens the standalone 插件与扩展管理 page.</summary>
    public static ExtensionMenuEntryViewModel Command(string header, AsyncCommand open)
    {
        ArgumentNullException.ThrowIfNull(open);
        return new ExtensionMenuEntryViewModel(header, open);
    }

    /// <summary>One clickable view row routing to the declared container via <paramref name="open"/>.</summary>
    public static ExtensionMenuEntryViewModel View(string pluginId, McpPluginViewInfo view,
        string header, Func<string, string, Task> open)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(open);
        return new ExtensionMenuEntryViewModel(header,
            new AsyncCommand(() => open(pluginId, view.ViewId)));
    }
}
