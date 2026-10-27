using Patchouli.Core.Ids;
using Patchouli.Core.Translation;
using Patchouli.Infrastructure.Translation;

namespace Patchouli.Host.Translation;

/// <summary>
/// Owns one run's resources and protects its phase and per-page outcome buffer with one lock.
/// </summary>
internal sealed class TranslationRunTracker
{
    private readonly object _gate = new();
    private readonly TranslationPageTarget[] _targets;
    private readonly TranslationPageOutcome?[] _outcomes;
    private readonly Dictionary<int, int> _slotByPageIndex;
    private TranslationRunPhase _phase;
    private CancellationTokenSource? _cancellation;
    private bool _cancellationDisposed;

    public TranslationRunTracker(
        Guid runId,
        DocumentInstanceId documentInstanceId,
        long sequence,
        IReadOnlyList<TranslationPageTarget> targets,
        CancellationTokenSource cancellation,
        TranslationRunStartRequest request)
    {
        Request = request;
        RunId = runId;
        DocumentInstanceId = documentInstanceId;
        Sequence = sequence;
        _targets = [.. targets];
        _outcomes = new TranslationPageOutcome?[_targets.Length];
        _slotByPageIndex = new Dictionary<int, int>(_targets.Length);
        for (int index = 0; index < _targets.Length; index++)
        {
            _slotByPageIndex[_targets[index].PageIndex] = index;
        }

        _cancellation = cancellation;
        Token = cancellation.Token;
        _phase = TranslationRunPhase.Started(_targets.Length);
    }

    public int TotalPages => _targets.Length;
    public TranslationRunStartRequest Request { get; set; }
    public TranslationExtensionRun? ExtensionRun { get; set; }
    public TranslationExecutionGate ExecutionGate { get; } = new();
    public bool LocallyPaused { get; set; }
    public Guid RunId { get; }
    public DocumentInstanceId DocumentInstanceId { get; }
    public long Sequence { get; }
    public CancellationToken Token { get; }
    public Task RunTask { get; set; } = Task.CompletedTask;

    public bool IsCancellationRequested
    {
        get
        {
            lock (_gate)
            {
                return _phase.IsCancelled || Token.IsCancellationRequested;
            }
        }
    }

    public void RecordStep(TranslationStepProgress step)
    {
        lock (_gate)
        {
            _phase = _phase.WithStep(step);
        }
    }

    public void RecordProgress(TranslationRunProgress progress)
    {
        if (progress.Outcome is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_slotByPageIndex.TryGetValue(progress.Outcome.PageIndex, out int slot))
            {
                _outcomes[slot] = progress.Outcome;
            }
        }
    }

    public void Complete(TranslationRunResult result)
    {
        lock (_gate)
        {
            foreach (TranslationPageOutcome outcome in result.Pages)
            {
                if (_slotByPageIndex.TryGetValue(outcome.PageIndex, out int slot))
                {
                    _outcomes[slot] = outcome;
                }
            }

            _phase = _phase.Completed(result);
        }
    }

    public void CompleteCancelled()
    {
        lock (_gate)
        {
            _phase = _phase.Cancelled();
        }
    }

    public void Fail(string errorCode, string errorMessage)
    {
        lock (_gate)
        {
            _phase = _phase.Failed(errorCode, errorMessage);
        }
    }

    /// <summary>Marks a running run cancelled and signals cancellation.</summary>
    public bool TryCancel()
    {
        lock (_gate)
        {
            TranslationRunPhase? cancelled = _phase.TryCancelled();
            if (cancelled is null)
            {
                return false;
            }

            _phase = cancelled;
            try
            {
                if (!_cancellationDisposed && _cancellation is { } cts)
                {
                    cts.Cancel();
                }
            }
            catch (ObjectDisposedException)
            {
            }

            return true;
        }
    }

    public void DisposeCancellation()
    {
        CancellationTokenSource? cts = null;
        lock (_gate)
        {
            if (!_cancellationDisposed)
            {
                _cancellationDisposed = true;
                cts = _cancellation;
                _cancellation = null;
            }
        }

        cts?.Dispose();
    }

    public TranslationRunStatus Snapshot()
    {
        lock (_gate)
        {
            return _phase.Project(RunId, DocumentInstanceId, _targets.Length, _outcomes,
                _phase.IsRunning && ExecutionGate.IsPaused);
        }
    }
}
