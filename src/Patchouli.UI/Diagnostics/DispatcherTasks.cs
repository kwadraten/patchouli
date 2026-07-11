using Avalonia.Threading;

namespace Patchouli.UI.Diagnostics;

public static class DispatcherTasks
{
    public static Task RunAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Dispatcher.UIThread.CheckAccess())
        {
            return action();
        }

        return Dispatcher.UIThread.InvokeAsync(action);
    }
}
