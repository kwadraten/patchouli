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
        UnexpectedExceptionReporter.Configure(
            (exception, boundary, operation) => UnexpectedExceptions.Sink.Report(exception, boundary, operation));
        var sink = UnexpectedExceptions.Sink;
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
                sink.Report(exception, "app-domain", "unhandled-exception");
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

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception exception)
        {
            sink.Report(exception, "process-main", "desktop-lifetime");
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
    }
}
