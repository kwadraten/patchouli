using Avalonia;
using Avalonia.Threading;
using Patchouli.Core.Diagnostics;
using Patchouli.UI.Diagnostics;

namespace Patchouli.UI;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        UnexpectedExceptions.Configure(new PlatformAppPaths());
        UnexpectedExceptionReporter.Configure((exception, boundary, operation) =>
            UnexpectedExceptions.Sink.Report(exception, boundary, operation));
        IUnexpectedExceptionSink sink = UnexpectedExceptions.Sink;
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
            {
                sink.Report(exception, "app-domain", "unhandled-exception");
            }
        };
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            sink.Report(eventArgs.Exception, "task-scheduler", "unobserved-task");
            eventArgs.SetObserved();
        };
        Dispatcher.UIThread.UnhandledException += (_, eventArgs) =>
        {
            sink.Report(eventArgs.Exception, "avalonia-dispatcher", "unhandled-callback");
            eventArgs.Handled = false;
        };

        DesktopInstanceCoordinator coordinator;
        try
        {
            coordinator = new DesktopInstanceCoordinator();
        }
        catch (Exception exception)
        {
            sink.Report(exception, "instance-election", "mutex-initialization");
            return 1;
        }

        if (!coordinator.IsPrimary)
        {
            try
            {
                bool notified = coordinator.NotifyPrimaryAsync().GetAwaiter().GetResult();
                if (notified)
                {
                    return 0;
                }

                sink.Report(
                    new InvalidOperationException("Failed to activate primary UI instance within retry timeout."),
                    "instance-election",
                    "notify-primary");
                return 1;
            }
            catch (Exception exception)
            {
                sink.Report(exception, "instance-election", "notify-primary");
                return 1;
            }
            finally
            {
                coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        try
        {
            coordinator.StartListener();
        }
        catch (Exception exception)
        {
            sink.Report(exception, "instance-election", "start-listener");
            coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return 1;
        }

        try
        {
            BuildAvaloniaApp(coordinator).StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception exception)
        {
            sink.Report(exception, "process-main", "desktop-lifetime");
            return 1;
        }
        finally
        {
            coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return BuildAvaloniaApp(null);
    }

    internal static AppBuilder BuildAvaloniaApp(IDesktopInstanceCoordinator? coordinator)
    {
        return AppBuilder.Configure(() => new App { Coordinator = coordinator })
            .UsePlatformDetect()
            .LogToTrace();
    }
}
