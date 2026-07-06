using Avalonia.Controls;

namespace Patchouli.UI.Views;

public sealed partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    private async void OnOpenMinerUTokenPageClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var launcher = TopLevel.GetTopLevel(this)?.Launcher;
        if (launcher is not null)
        {
            await launcher.LaunchUriAsync(new Uri("https://mineru.net/apiManage/token"));
        }
    }
}
