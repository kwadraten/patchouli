using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using System;
using System.Linq;
using Patchouli.Core.Diagnostics;
using Patchouli.UI.Diagnostics;
using Patchouli.UI.Services;
using Patchouli.UI.Themes;

namespace Patchouli.UI;

public sealed partial class App : Application
{
    private IDisposable? _activationSubscription;
    private IDisposable? _uriSubscription;

    internal IDesktopInstanceCoordinator? Coordinator { get; set; }
    internal string? StartupUri { get; set; }

    public override void Initialize()
    {
        // Never resolve UIThread from a worker: after headless teardown that getter can create a
        // fresh dispatcher on the worker itself. Pin this App's dispatcher and reject retired Apps.
        Dispatcher dispatcher = Dispatcher.UIThread;
        UiThreadExecutionGuard.Configure(() => ReferenceEquals(Current, this) && dispatcher.CheckAccess());
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        try
        {
            RichEditorChineseLocalization.Apply();
            PatchouliAppSettings settings = PatchouliAppSettings.Load();
            ThemePaletteApplier.Apply(settings.Ui.PaletteId, settings.Ui.ThemeMode);

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                MainWindow mainWindow = new();
                desktop.MainWindow = mainWindow;
                SubscribeToActivation(mainWindow);
                mainWindow.StartStartupAfterFirstFrame();
                if (StartupUri is { } uri)
                {
                    StartupUri = null;
                    Dispatcher.UIThread.Post(() => mainWindow.OpenProtocolUri(uri));
                }

                desktop.Exit += OnDesktopExit;
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
        _uriSubscription?.Dispose();
        _uriSubscription = targetCoordinator.SubscribeUris(uri =>
            Dispatcher.UIThread.Post(() => mainWindow.OpenProtocolUri(uri)));
        _activationSubscription = targetCoordinator.Subscribe(() =>
        {
            Dispatcher.UIThread.Post(() => { ActivateWindow(mainWindow); });
        });

        mainWindow.Closed += (_, _) =>
        {
            _activationSubscription?.Dispose();
            _activationSubscription = null;
            _uriSubscription?.Dispose();
            _uriSubscription = null;
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

    private static DispatcherTimer? _trayAttentionTimer;

    internal static void ShowTrayAttention(string message)
    {
        if (Current is not App app)
        {
            return;
        }

        TrayIcons? icons = TrayIcon.GetIcons(app);
        TrayIcon? icon = icons?.FirstOrDefault();
        if (icon is null)
        {
            return;
        }

        _trayAttentionTimer?.Stop();
        icon.ToolTipText = message;
        _trayAttentionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _trayAttentionTimer.Tick += (_, _) =>
        {
            _trayAttentionTimer?.Stop();
            _trayAttentionTimer = null;
            icon.ToolTipText = "Patchouli.NET";
        };
        _trayAttentionTimer.Start();
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

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        ThemePaletteApplier.DetachWatcher();
    }
}
