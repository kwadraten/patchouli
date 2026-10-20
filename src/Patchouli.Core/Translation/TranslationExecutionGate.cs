namespace Patchouli.Core.Translation;

/// <summary>Stops new model dispatches while allowing already dispatched requests to settle.</summary>
public sealed class TranslationExecutionGate
{
    private readonly object _gate = new();
    private TaskCompletionSource? _resume;

    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _resume is not null;
            }
        }
    }

    public void SetPaused(bool paused)
    {
        lock (_gate)
        {
            if (paused)
            {
                _resume ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            else
            {
                _resume?.TrySetResult();
                _resume = null;
            }
        }
    }

    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task? wait;
            lock (_gate)
            {
                wait = _resume?.Task;
            }

            if (wait is null)
            {
                return;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
