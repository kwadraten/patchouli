using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using LiteratureApp.Core.Configuration;

namespace LiteratureApp.UI;

public sealed partial class App : Application
{
    public override void Initialize()
    {
        DotEnv.LoadNearest();
        AvaloniaXamlLoader.Load(this);
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;
            await mainWindow.ShowFirstRunIfNeededAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
