using Avalonia.Threading;
using Patchouli.Core.Results;
using Patchouli.Host.Downloads;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.Services;

public sealed class DownloadOperationRunner
{
    private readonly IDialogService _dialogs;

    public DownloadOperationRunner(IDialogService dialogs)
    {
        _dialogs = dialogs;
    }

    public async Task<Result> RunAsync(string title, DownloadTaskService service,
        DownloadTaskHandle handle)
    {
        (DownloadOperationDialogViewModel viewModel, IDisposable observation, Task dialogTask) shown;
        try
        {
            shown = await OnUiAsync(() => ShowObserved(title, service, handle.TaskId));
        }
        catch
        {
            await handle.Completion.ConfigureAwait(false);
            throw;
        }

        Task dialogLifetime = ObserveDialogLifetimeAsync(shown.dialogTask, shown.observation);
        try
        {
            Result result = await handle.Completion.ConfigureAwait(false);
            await OnUiAsync<object?>(() =>
            {
                if (!dialogLifetime.IsCompleted)
                {
                    DownloadTaskStatus? status =
                        service.List().FirstOrDefault(task => task.TaskId == handle.TaskId);
                    if (status is not null)
                    {
                        shown.viewModel.Apply(status);
                    }

                    if (result.IsSuccess)
                    {
                        shown.viewModel.RequestClose?.Invoke(null);
                    }
                }

                return null;
            });
            await dialogLifetime.ConfigureAwait(false);
            return result;
        }
        finally
        {
            shown.observation.Dispose();
        }
    }

    public async Task ShowAsync(string title, DownloadTaskService service, Guid taskId)
    {
        (_, IDisposable observation, Task dialogTask) =
            await OnUiAsync(() => ShowObserved(title, service, taskId));
        await ObserveDialogLifetimeAsync(dialogTask, observation).ConfigureAwait(false);
    }

    private static async Task ObserveDialogLifetimeAsync(Task dialogTask, IDisposable observation)
    {
        try
        {
            await dialogTask.ConfigureAwait(false);
        }
        finally
        {
            observation.Dispose();
        }
    }

    private (DownloadOperationDialogViewModel, IDisposable, Task) ShowObserved(
        string title, DownloadTaskService service, Guid taskId)
    {
        DownloadOperationDialogViewModel viewModel = new(service, taskId) { Title = title };
        bool posted = false;
        bool disposed = false;

        void OnChanged(object? sender, EventArgs args)
        {
            lock (viewModel)
            {
                if (posted || disposed)
                {
                    return;
                }

                posted = true;
            }

            Dispatcher.UIThread.Post(() =>
            {
                lock (viewModel)
                {
                    posted = false;
                    if (disposed)
                    {
                        return;
                    }
                }

                DownloadTaskStatus? status =
                    service.List().FirstOrDefault(task => task.TaskId == taskId);
                if (status is not null)
                {
                    viewModel.Apply(status);
                }
            });
        }

        service.Changed += OnChanged;
        IDisposable observation = new Observation(service, OnChanged, () =>
        {
            lock (viewModel)
            {
                disposed = true;
            }
        });
        try
        {
            DownloadTaskStatus? current =
                service.List().FirstOrDefault(task => task.TaskId == taskId);
            if (current is not null)
            {
                viewModel.Apply(current);
            }

            Task dialogTask = _dialogs.ShowDialogAsync(viewModel);
            return (viewModel, observation, dialogTask);
        }
        catch
        {
            observation.Dispose();
            throw;
        }
    }

    private sealed class Observation(
        DownloadTaskService service,
        EventHandler handler,
        Action markDisposed) : IDisposable
    {
        public void Dispose()
        {
            markDisposed();
            service.Changed -= handler;
        }
    }

    private static Task<T> OnUiAsync<T>(Func<T> action)
    {
        return Dispatcher.UIThread.CheckAccess()
            ? Task.FromResult(action())
            : Dispatcher.UIThread.InvokeAsync(action).GetTask();
    }
}
