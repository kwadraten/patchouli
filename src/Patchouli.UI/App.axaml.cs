using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Patchouli.UI.Diagnostics;

namespace Patchouli.UI;

public sealed partial class App : Application
{
    private IDisposable? _activationSubscription;

    internal IDesktopInstanceCoordinator? Coordinator { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        MainWindow? mainWindow = null;
        bool initialized = false;
        try
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                mainWindow = new MainWindow();
                desktop.MainWindow = mainWindow;
                SubscribeToActivation(mainWindow);
                await mainWindow.ShowFirstRunIfNeededAsync(false);
                initialized = true;
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
            if (initialized)
            {
                mainWindow?.StartMcpServerInBackground();
            }
        }
    }

    internal void SubscribeToActivation(MainWindow mainWindow, IDesktopInstanceCoordinator? coordinator = null)
    {
        IDesktopInstanceCoordinator? targetCoordinator = coordinator ?? Coordinator;
        if (targetCoordinator is null)
        {
            return;
        }

        _activationSubscription?.Dispose();
        _activationSubscription = targetCoordinator.Subscribe(() =>
        {
            Dispatcher.UIThread.Post(() => { ActivateWindow(mainWindow); });
        });

        mainWindow.Closed += (_, _) =>
        {
            _activationSubscription?.Dispose();
            _activationSubscription = null;
        };
    }

    internal static void ActivateWindow(Window window)
    {
        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }
}
