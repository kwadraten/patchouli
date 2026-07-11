namespace Patchouli.UI.Diagnostics;

public static class TaskObservationExtensions
{
    public static void Observe(
        this Task task,
        string boundary,
        string? operation = null,
        CancellationToken cancellationToken = default,
        IUnexpectedExceptionSink? sink = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentException.ThrowIfNullOrWhiteSpace(boundary);
        ObserveAsync(task, boundary, operation, cancellationToken, sink ?? UnexpectedExceptions.Sink);
    }

    private static async void ObserveAsync(
        Task task,
        string boundary,
        string? operation,
        CancellationToken cancellationToken,
        IUnexpectedExceptionSink sink)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            sink.Report(exception, boundary, operation);
        }
    }
}
