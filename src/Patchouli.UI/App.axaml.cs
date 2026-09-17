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

    public override void OnFrameworkInitializationCompleted()
    {
        try
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                MainWindow mainWindow = new();
                desktop.MainWindow = mainWindow;
                SubscribeToActivation(mainWindow);
                mainWindow.StartStartupAfterFirstFrame();
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

    private void OnTrayIconClicked(object? sender, EventArgs e)
    {
        ShowMainWindow();
    }

    private void OnOpenFromTrayClick(object? sender, EventArgs e)
    {
        ShowMainWindow();
    }

    private void OnExitFromTrayClick(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow mainWindow })
        {
            ActivateWindow(mainWindow);
            mainWindow.RequestExit();
        }
    }

    private void ShowMainWindow()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } mainWindow })
        {
            ActivateWindow(mainWindow);
        }
    }
}
