using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Patchouli.UI.Diagnostics;

namespace Patchouli.UI;

public sealed partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        try
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                MainWindow mainWindow = new();
                desktop.MainWindow = mainWindow;
                await mainWindow.ShowFirstRunIfNeededAsync();
            }
        }
        catch (Exception exception)
        {
            UnexpectedExceptions.Sink.Report(exception, "application-initialization", "initialize-main-window");
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown(1);
            }
        }
        finally
        {
            base.OnFrameworkInitializationCompleted();
        }
    }
}
