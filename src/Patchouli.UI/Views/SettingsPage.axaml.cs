using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Patchouli.UI.Diagnostics;

namespace Patchouli.UI.Views;

public sealed partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    private async void OnOpenMinerUTokenPageClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await UnexpectedExceptionBoundary.RunAsync(OpenMinerUTokenPageAsync, "open-mineru-token-page");
    }

    private async Task OpenMinerUTokenPageAsync()
    {
        ILauncher? launcher = TopLevel.GetTopLevel(this)?.Launcher;
        if (launcher is not null)
        {
            await launcher.LaunchUriAsync(new Uri("https://mineru.net/apiManage/token"));
        }
    }
}
