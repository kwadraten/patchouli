using Avalonia.Controls;
using Patchouli.Mcp;
using Patchouli.UI.ViewModels;

namespace Patchouli.UI.Views;

/// <summary>
/// Non-modal popup container for a plugin-declared HTML view (placement "window"). The window
/// only owns the visual host: loading and registry bookkeeping live in
/// <see cref="MainWindowViewModel"/>; on close the web view and the session are released.
/// </summary>
public sealed partial class PluginViewWindow : Window, IPluginViewWindow
{
    /// <summary>XAML-loader constructor; runtime instances are created with a session.</summary>
    public PluginViewWindow()
    {
        Session = new PluginViewSession("design-time", new McpPluginViewInfo(
            "", "设计预览", "window", "patchouli://plugin/design-time/views/x", "", ""));
        InitializeComponent();
    }

    public PluginViewWindow(PluginViewSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Session = session;
        DataContext = session;
        Title = session.Title;
        InitializeComponent();
    }

    public PluginViewSession Session { get; }

    void IPluginViewWindow.Show(Window? owner)
    {
        if (owner is null)
        {
            Show();
        }
        else
        {
            Show(owner);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        WebHost.Release();
        Session.Release();
        base.OnClosed(e);
    }
}
