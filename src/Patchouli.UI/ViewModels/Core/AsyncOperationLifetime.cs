using Patchouli.Core.Diagnostics;

namespace Patchouli.UI.ViewModels.Core;

/// <summary>Stops accepting work and drains actual tasks, not latest-wins publication signals.</summary>
internal sealed class AsyncOperationLifetime : IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private int _active;
    private bool _stopped;
    private TaskCompletionSource? _drained;

    public IDisposable? TryEnter()
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return null;
            }

            if (_active++ == 0)
            {
                _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        return new OperationLease(this);
    }

    public Task RunAsync(Func<Task> operation)
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return Task.CompletedTask;
            }

            if (_active++ == 0)
            {
                _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        return RunCoreAsync(operation);
    }

    private async Task RunCoreAsync(Func<Task> operation)
    {
        try
        {
            // This bookkeeping has no UI affinity. A test/application may retire the
            // originating dispatcher while cancellation unwinds the operation.
            await operation().ConfigureAwait(false);
        }
        finally
        {
            CompleteOperation();
        }
    }

    private void CompleteOperation()
    {
        lock (_gate)
        {
            if (--_active == 0)
            {
                _drained!.TrySetResult();
            }
        }
    }

    private sealed class OperationLease(AsyncOperationLifetime owner) : IDisposable
    {
        private AsyncOperationLifetime? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.CompleteOperation();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stopped = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task drained;
        lock (_gate)
        {
            _stopped = true;
            drained = _drained?.Task ?? Task.CompletedTask;
        }

        try
        {
            // A tracked operation stuck on a dead dispatcher or an unwoken completion channel
            // would otherwise hang the entire shutdown chain; bound the wait and surface it
            // as a reported failure instead of a silent deadlock.
            await drained.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException exception) when (UnexpectedExceptionReporter.ReportCatch(
                                                     exception, "ui.operation-lifetime", "drain-timeout"))
        {
            // Ownership must not be released while a timed-out write can still be running.
            throw;
        }
    }
}
