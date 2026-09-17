using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Patchouli.Core.Diagnostics;
using Patchouli.UI.Diagnostics;

namespace Patchouli.UI.ViewModels;

public sealed class AsyncCommand : IAsyncRelayCommand, INotifyPropertyChanged
{
    private static readonly AsyncLocal<IHostActivityTracker?> ConstructionActivityTracker = new();
    private static readonly AsyncLocal<CommandActivity?> CurrentActivity = new();
    private readonly Func<CancellationToken, Task> _run;
    private readonly AsyncRelayCommand _inner;
    private readonly string _operation;
    private readonly IUnexpectedExceptionSink _unexpectedExceptions;
    private readonly IHostActivityTracker? _activityTracker;

    public AsyncCommand(
        Func<Task> run,
        Func<bool>? canExecute = null,
        IUnexpectedExceptionSink? unexpectedExceptions = null,
        [CallerArgumentExpression(nameof(run))]
        string? operation = null,
        IHostActivityTracker? activityTracker = null)
    {
        _operation = string.IsNullOrWhiteSpace(operation) ? "unknown-command" : operation;
        _unexpectedExceptions = unexpectedExceptions ?? UnexpectedExceptions.Sink;
        _activityTracker = activityTracker ?? ConstructionActivityTracker.Value;
        _run = cancellationToken => ExecuteWithActivityAsync(_ => run(), cancellationToken);
        _inner = canExecute is null
            ? new AsyncRelayCommand(ExecuteFromCommandBoundaryWithoutCancellationAsync)
            : new AsyncRelayCommand(ExecuteFromCommandBoundaryWithoutCancellationAsync, canExecute);
        _inner.PropertyChanged += OnInnerPropertyChanged;
    }

    public AsyncCommand(
        Func<CancellationToken, Task> run,
        Func<bool>? canExecute = null,
        IUnexpectedExceptionSink? unexpectedExceptions = null,
        [CallerArgumentExpression(nameof(run))]
        string? operation = null,
        IHostActivityTracker? activityTracker = null)
    {
        _operation = string.IsNullOrWhiteSpace(operation) ? "unknown-command" : operation;
        _unexpectedExceptions = unexpectedExceptions ?? UnexpectedExceptions.Sink;
        _activityTracker = activityTracker ?? ConstructionActivityTracker.Value;
        _run = cancellationToken => ExecuteWithActivityAsync(run, cancellationToken);
        _inner = canExecute is null
            ? new AsyncRelayCommand(ExecuteFromCommandBoundaryAsync)
            : new AsyncRelayCommand(ExecuteFromCommandBoundaryAsync, canExecute);
        _inner.PropertyChanged += OnInnerPropertyChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal static IDisposable UseActivityTracker(IHostActivityTracker activityTracker)
    {
        ArgumentNullException.ThrowIfNull(activityTracker);
        IHostActivityTracker? previous = ConstructionActivityTracker.Value;
        ConstructionActivityTracker.Value = activityTracker;
        return new ActivityTrackerLease(previous);
    }

    public AsyncCommand(
        Func<Task> run,
        IUnexpectedExceptionSink unexpectedExceptions,
        [CallerArgumentExpression(nameof(run))]
        string? operation = null)
        : this(run, null, unexpectedExceptions, operation)
    {
    }

    public AsyncCommand(
        Func<CancellationToken, Task> run,
        IUnexpectedExceptionSink unexpectedExceptions,
        [CallerArgumentExpression(nameof(run))]
        string? operation = null)
        : this(run, null, unexpectedExceptions, operation)
    {
    }

    public event EventHandler? CanExecuteChanged
    {
        add => _inner.CanExecuteChanged += value;
        remove => _inner.CanExecuteChanged -= value;
    }

    public bool CanExecute(object? parameter)
    {
        return _inner.CanExecute(parameter);
    }

    public void Execute(object? parameter)
    {
        _inner.Execute(parameter);
    }

    public bool IsRunning => _inner.IsRunning;

    public bool CanBeCanceled => _inner.CanBeCanceled;

    public bool IsCancellationRequested => _inner.IsCancellationRequested;

    public Task? ExecutionTask => _inner.ExecutionTask;

    public void Cancel()
    {
        _inner.Cancel();
    }

    public void NotifyCanExecuteChanged()
    {
        _inner.NotifyCanExecuteChanged();
    }

    public Task ExecuteAsync()
    {
        return _run(CancellationToken.None);
    }

    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        return _run(cancellationToken);
    }

    public Task ExecuteAsync(object? parameter)
    {
        // The parameter is ignored by design; keep this overload on the same explicit-await path
        // as the parameterless one so exceptions propagate to the caller instead of the sink.
        return _run(CancellationToken.None);
    }

    private async Task ExecuteFromCommandBoundaryAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _run(cancellationToken);
        }
        catch (OperationCanceledException exception) when (exception.CancellationToken.IsCancellationRequested ||
                                                           cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _unexpectedExceptions.Report(exception, "ui-command", _operation);
        }
    }

    private Task ExecuteFromCommandBoundaryWithoutCancellationAsync()
    {
        return ExecuteFromCommandBoundaryAsync(CancellationToken.None);
    }

    private async Task ExecuteWithActivityAsync(
        Func<CancellationToken, Task> run,
        CancellationToken cancellationToken)
    {
        if (_activityTracker is null)
        {
            await run(cancellationToken);
            return;
        }

        CommandActivity? parent = CurrentActivity.Value;
        string correlationId = ReferenceEquals(parent?.Tracker, _activityTracker)
            ? parent.CorrelationId
            : $"ui-command:{Guid.NewGuid():N}";
        string? detail = parent is null ? NormalizeOperation(_operation) : null;
        using IActivityScope scope = _activityTracker.BeginScope(
            "界面命令",
            HostActivityKind.UiCommand,
            detail,
            correlationId);
        using IDisposable constructionActivityTracker = UseActivityTracker(_activityTracker);
        CurrentActivity.Value = new CommandActivity(_activityTracker, correlationId);
        try
        {
            await run(cancellationToken);
        }
        finally
        {
            CurrentActivity.Value = parent;
        }
    }

    private static string NormalizeOperation(string operation)
    {
        const int maxLength = 120;
        string normalized = operation.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "…";
    }

    private void OnInnerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        PropertyChanged?.Invoke(this, e);
    }

    private sealed record CommandActivity(IHostActivityTracker Tracker, string CorrelationId);

    private sealed class ActivityTrackerLease : IDisposable
    {
        private readonly IHostActivityTracker? _previous;
        private int _disposed;

        public ActivityTrackerLease(IHostActivityTracker? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                ConstructionActivityTracker.Value = _previous;
            }
        }
    }
}
