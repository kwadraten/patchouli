using Avalonia;
using Avalonia.Threading;
using Patchouli.Core.Diagnostics;
using Patchouli.Host.Lifecycle;
using Patchouli.UI.Diagnostics;

namespace Patchouli.UI;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == Host.Agent.AgentFsiReplWorker.Switch)
        {
            return Host.Agent.AgentFsiReplWorker.RunAsync(args[1]).GetAwaiter().GetResult();
        }

        if (args.Contains("--headless", StringComparer.Ordinal))
        {
            try
            {
                string databasePath = RequiredOption(args, "--db");
                int? port = OptionalPort(args);
                return HeadlessRuntimeHost.RunAsync(databasePath, port).GetAwaiter().GetResult();
            }
            catch (ArgumentException exception)
            {
                Console.Error.WriteLine($"Patchouli headless host: {exception.Message}");
                return 2;
            }
        }

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

    private static string RequiredOption(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        if (index < 0 || index == args.Length - 1 || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            throw new ArgumentException($"{name} requires a value.");
        }

        return args[index + 1];
    }

    private static int? OptionalPort(string[] args)
    {
        int index = Array.IndexOf(args, "--port");
        if (index < 0)
        {
            return null;
        }

        if (index == args.Length - 1 || !int.TryParse(args[index + 1], out int port) || port is < 1 or > 65535)
        {
            throw new ArgumentException("--port requires an integer from 1 through 65535.");
        }

        return port;
    }
}
