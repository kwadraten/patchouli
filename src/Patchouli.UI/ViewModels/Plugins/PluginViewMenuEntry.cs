using Patchouli.Mcp;

namespace Patchouli.UI.ViewModels;

/// <summary>
/// One row in the PDF workspace sidebar's plugin-view menu: the declared view plus the
/// command that opens it as the sidebar's plugin tab.
/// </summary>
public sealed class PluginViewMenuEntry
{
    public PluginViewMenuEntry(McpPluginViewInfo view, Func<McpPluginViewInfo, Task> activate)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(activate);
        View = view;
        ActivateCommand = new AsyncCommand(() => activate(view));
    }

    public McpPluginViewInfo View { get; }
    public string Title => View.Title;
    public AsyncCommand ActivateCommand { get; }
}
