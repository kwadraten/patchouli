namespace Patchouli.UI.Diagnostics;

public static class UnexpectedExceptionBoundary
{
    public static async Task RunAsync(Func<Task> action, string operation)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            UnexpectedExceptions.Sink.Report(exception, "ui-event", operation);
        }
    }
}
