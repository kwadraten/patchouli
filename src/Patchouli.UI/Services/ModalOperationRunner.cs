using Avalonia.Threading;
using Patchouli.Core.Results;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.Services;

public sealed record ModalOperationOptions(
    string Title,
    string InitialStatus,
    bool CanCancel = false);

public sealed class ModalOperationContext
{
    private readonly BlockingOperationDialogViewModel _viewModel;
    private readonly bool _dispatchToUi;

    internal ModalOperationContext(BlockingOperationDialogViewModel viewModel, CancellationToken cancellationToken, bool dispatchToUi)
    {
        _viewModel = viewModel;
        CancellationToken = cancellationToken;
        _dispatchToUi = dispatchToUi;
    }

    public CancellationToken CancellationToken { get; }

    public void Report(int? current, int? total, string label, string? detail = null)
        => Dispatcher.UIThread.Post(() => ApplyProgress(current, total, label, detail));

    public Task ReportAsync(int? current, int? total, string label, string? detail = null)
    {
        if (!_dispatchToUi)
        {
            ApplyProgress(current, total, label, detail);
            return Task.CompletedTask;
        }
        return Dispatcher.UIThread.InvokeAsync(() => ApplyProgress(current, total, label, detail)).GetTask();
    }

    public Task AddLogAsync(string message)
    {
        if (!_dispatchToUi)
        {
            _viewModel.AddLog(message);
            return Task.CompletedTask;
        }
        return Dispatcher.UIThread.InvokeAsync(() => _viewModel.AddLog(message)).GetTask();
    }

    private void ApplyProgress(int? current, int? total, string label, string? detail)
    {
        if (!_viewModel.IsRunning) return;
        _viewModel.StatusMessage = label;
        _viewModel.IsIndeterminate = current is null || total is null || total <= 0;
        if (!_viewModel.IsIndeterminate)
            _viewModel.ProgressValue = Math.Clamp(current!.Value * 100d / total!.Value, 0, 100);
        if (!string.IsNullOrWhiteSpace(detail))
            _viewModel.AddLog(detail);
    }
}

public interface IModalOperationRunner
{
    Task<T> RunAsync<T>(
        ModalOperationOptions options,
        Func<ModalOperationContext, Task<T>> operation,
        CancellationToken cancellationToken = default);
}

public sealed class ModalOperationRunner : IModalOperationRunner
{
    private readonly IDialogService _dialogs;

    public ModalOperationRunner(IDialogService dialogs)
    {
        _dialogs = dialogs;
    }

    public async Task<T> RunAsync<T>(
        ModalOperationOptions options,
        Func<ModalOperationContext, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var viewModel = new BlockingOperationDialogViewModel(source.Cancel)
        {
            Title = options.Title,
            StatusMessage = options.InitialStatus,
            CanCancel = options.CanCancel
        };
        viewModel.AddLog(options.InitialStatus);

        var dialogTask = _dialogs.ShowDialogAsync(viewModel);
        if (!dialogTask.IsCompleted)
            await Dispatcher.UIThread.InvokeAsync(() => { });
        try
        {
            var context = new ModalOperationContext(viewModel, source.Token, !dialogTask.IsCompleted);
            var result = dialogTask.IsCompleted
                ? await operation(context)
                : await Task.Run(
                    async () => await operation(context).ConfigureAwait(false),
                    source.Token);
            if (result is IOperationOutcome { IsSuccess: false } outcome)
                viewModel.MarkFailed(outcome.ErrorMessage ?? "操作未完成。");
            else
                viewModel.MarkCompleted();
            await dialogTask;
            return result;
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            viewModel.MarkCancelled();
            await dialogTask;
            throw;
        }
        catch (Exception exception)
        {
            viewModel.MarkFailed(exception.Message);
            await dialogTask;
            throw;
        }
    }
}
