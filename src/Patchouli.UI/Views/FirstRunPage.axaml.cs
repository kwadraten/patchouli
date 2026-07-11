using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Patchouli.UI.ViewModels;
using Patchouli.UI.Diagnostics;

namespace Patchouli.UI.Views;

public sealed partial class FirstRunPage : UserControl
{
    public FirstRunPage()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private async void OnOpenMinerUTokenPageClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await UnexpectedExceptionBoundary.RunAsync(OpenMinerUTokenPageAsync, "open-mineru-token-page");

    private async Task OpenMinerUTokenPageAsync()
    {
        var launcher = TopLevel.GetTopLevel(this)?.Launcher;
        if (launcher is not null)
        {
            await launcher.LaunchUriAsync(new Uri("https://mineru.net/apiManage/token"));
        }
    }
}
