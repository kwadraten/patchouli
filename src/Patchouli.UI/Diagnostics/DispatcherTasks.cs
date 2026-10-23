using Avalonia.Threading;

namespace Patchouli.UI.Diagnostics;

public static class DispatcherTasks
{
    public static Task RunAsync(Func<Task> action)
    {
        return RunAsync(action, CancellationToken.None);
    }

    public static Task RunAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        if (Dispatcher.UIThread.CheckAccess())
        {
            return action();
        }

        return RunDispatchedAsync(action, cancellationToken);
    }

    private static async Task RunDispatchedAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        // Cancel queued publication when its owner stops, but still await an action that has
        // actually started. Merely cancelling the wait could leave database work unobserved.
        // The outer await is token-bound too: a dispatcher operation that can neither run nor
        // complete (e.g. after its owner is cancelled mid-enqueue) would otherwise park the
        // caller inside AsyncOperationLifetime's drain and hang shutdown.
        Task running = await Dispatcher.UIThread.InvokeAsync<Task>(action, DispatcherPriority.Normal,
                cancellationToken)
            .GetTask()
            .WaitAsync(cancellationToken);
        await running;
    }
}
