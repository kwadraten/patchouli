using System.Runtime.CompilerServices;

namespace Patchouli.Core.Diagnostics;

public static class UnexpectedExceptionReporter
{
    private static Action<Exception, string, string?> _report = NoOp;

    public static void Configure(Action<Exception, string, string?> report)
    {
        Volatile.Write(ref _report, report ?? throw new ArgumentNullException(nameof(report)));
    }

    public static void Reset()
    {
        Volatile.Write(ref _report, NoOp);
    }

    public static void Report(Exception exception, string boundary, string? operation = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(boundary);

        if (exception is OperationCanceledException)
        {
            return;
        }

        try
        {
            Volatile.Read(ref _report)(exception, boundary, operation);
        }
        catch
        {
            // Diagnostics must not replace the exception handling at the application boundary.
        }
    }

    public static bool ReportCatch(
        Exception exception,
        string boundary,
        [CallerMemberName] string? operation = null)
    {
        if (exception is OperationCanceledException)
        {
            return false;
        }

        Report(exception, boundary, operation);
        return true;
    }

    private static void NoOp(Exception exception, string boundary, string? operation)
    {
    }
}
