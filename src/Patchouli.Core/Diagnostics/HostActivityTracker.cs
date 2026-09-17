using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Patchouli.Core.Diagnostics;

public sealed class HostActivityTracker : IHostActivityTracker, IDisposable
{
    private readonly object _lock = new();
    private readonly object _dispatchLock = new();
    private readonly Dictionary<string, (HostActivityItem Item, int RefCount)> _items = new(StringComparer.Ordinal);
    private readonly TimeSpan _throttleInterval;
    private readonly TimeProvider _timeProvider;
    private readonly List<IObserver<HostActivitySnapshot>> _observers = new();
    private CancellationTokenSource? _throttleCts;
    private Task? _throttleTask;
    private HostActivitySnapshot _current = HostActivitySnapshot.Idle;
    private long _snapshotSequence;
    private long _deliveredSequence;
    private TaskCompletionSource _idleTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    public HostActivityTracker(TimeSpan? throttleInterval = null, TimeProvider? timeProvider = null)
    {
        // Default max ~30Hz detail coalescing interval
        _throttleInterval = throttleInterval ?? TimeSpan.FromMilliseconds(34);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _idleTcs.TrySetResult();
    }

    public HostActivitySnapshot Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    public event EventHandler<HostActivitySnapshot>? Changed;

    public IObservable<HostActivitySnapshot> SnapshotStream => new SnapshotObservable(this);

    public IActivityScope BeginScope(string name, HostActivityKind kind, string? initialDetail = null,
        string? correlationId = null)
    {
        string id = string.IsNullOrWhiteSpace(correlationId) ? Guid.NewGuid().ToString("N") : correlationId;
        PendingPublish? pendingPublish;
        lock (_lock)
        {
            if (_disposed)
            {
                return new ActivityScope(this, id);
            }

            if (_items.TryGetValue(id, out (HostActivityItem Item, int RefCount) existing))
            {
                // Correlation ID refcount deduplication
                HostActivityItem updated = existing.Item;
                if (initialDetail is not null &&
                    !string.Equals(updated.Detail, initialDetail, StringComparison.Ordinal))
                {
                    updated = updated with { Detail = initialDetail };
                }

                _items[id] = (updated, existing.RefCount + 1);
                pendingPublish = UpdateSnapshotAndNotifyLocked(false);
            }
            else
            {
                HostActivityItem item = new(id, name, kind, _timeProvider.GetUtcNow(), initialDetail);
                _items[id] = (item, 1);
                pendingPublish = UpdateSnapshotAndNotifyLocked(false);
            }
        }

        Publish(pendingPublish);
        return new ActivityScope(this, id);
    }

    private void UpdateScopeDetail(string id, string detail)
    {
        PendingPublish? pendingPublish;
        lock (_lock)
        {
            if (_disposed || !_items.TryGetValue(id, out (HostActivityItem Item, int RefCount) existing))
            {
                return;
            }

            // Skip no-op updates
            if (string.Equals(existing.Item.Detail, detail, StringComparison.Ordinal))
            {
                return;
            }

            _items[id] = (existing.Item with { Detail = detail }, existing.RefCount);
            pendingPublish = UpdateSnapshotAndNotifyLocked(true);
        }

        Publish(pendingPublish);
    }

    private void SetScopePaused(string id, bool paused, string? reason)
    {
        PendingPublish? pendingPublish;
        lock (_lock)
        {
            if (_disposed || !_items.TryGetValue(id, out (HostActivityItem Item, int RefCount) existing))
            {
                return;
            }

            string? nextReason = paused ? reason : null;
            // Skip no-op updates
            if (existing.Item.IsPaused == paused &&
                string.Equals(existing.Item.PauseReason, nextReason, StringComparison.Ordinal))
            {
                return;
            }

            // Separate pause reason from detail; original detail is preserved
            HostActivityItem updated = existing.Item with
            {
                IsPaused = paused,
                PauseReason = nextReason
            };

            _items[id] = (updated, existing.RefCount);
            pendingPublish = UpdateSnapshotAndNotifyLocked(false);
        }

        Publish(pendingPublish);
    }

    private void SetScopeWaitingRetry(string id, bool waiting, string? reason)
    {
        PendingPublish? pendingPublish;
        lock (_lock)
        {
            if (_disposed || !_items.TryGetValue(id, out (HostActivityItem Item, int RefCount) existing))
            {
                return;
            }

            string? nextReason = waiting ? reason : null;
            // Skip no-op updates
            if (existing.Item.IsWaitingRetry == waiting &&
                string.Equals(existing.Item.RetryReason, nextReason, StringComparison.Ordinal))
            {
                return;
            }

            // Separate retry reason from detail; original detail is preserved
            HostActivityItem updated = existing.Item with
            {
                IsWaitingRetry = waiting,
                RetryReason = nextReason
            };

            _items[id] = (updated, existing.RefCount);
            pendingPublish = UpdateSnapshotAndNotifyLocked(false);
        }

        Publish(pendingPublish);
    }

    private void EndScope(string id)
    {
        PendingPublish? pendingPublish = null;
        lock (_lock)
        {
            if (_disposed || !_items.TryGetValue(id, out (HostActivityItem Item, int RefCount) existing))
            {
                return;
            }

            if (existing.RefCount > 1)
            {
                _items[id] = (existing.Item, existing.RefCount - 1);
            }
            else
            {
                _items.Remove(id);
                pendingPublish = UpdateSnapshotAndNotifyLocked(false);
            }
        }

        Publish(pendingPublish);
    }

    private PendingPublish? UpdateSnapshotAndNotifyLocked(bool isDetailUpdate)
    {
        HostActivitySnapshot newSnapshot = ComputeSnapshotLocked();
        bool wasBusy = _current.IsBusy;
        _current = newSnapshot;
        long sequence = ++_snapshotSequence;

        if (newSnapshot.IsBusy && _idleTcs.Task.IsCompleted)
        {
            _idleTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        bool busyChanged = newSnapshot.IsBusy != wasBusy;
        bool busyRising = newSnapshot.IsBusy && !wasBusy;
        bool mustNotifyImmediate = !isDetailUpdate || busyChanged || busyRising;

        if (mustNotifyImmediate)
        {
            _throttleCts?.Cancel();
            _throttleCts = null;
            _throttleTask = null;

            // The caller publishes after releasing _lock. BeginScope still publishes
            // synchronously before returning, without reversing the lock order.
            return new PendingPublish(newSnapshot, sequence);
        }
        else if (_throttleCts is null)
        {
            _throttleCts = new CancellationTokenSource();
            CancellationToken token = _throttleCts.Token;

            _throttleTask = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(_throttleInterval, _timeProvider, token).ConfigureAwait(false);
                    HostActivitySnapshot latest;
                    long latestSeq;
                    lock (_lock)
                    {
                        if (_disposed || token.IsCancellationRequested)
                        {
                            return;
                        }

                        _throttleCts = null;
                        _throttleTask = null;
                        latest = _current;
                        latestSeq = _snapshotSequence;
                    }

                    DispatchSnapshot(latest, latestSeq);
                }
                catch (OperationCanceledException)
                {
                    // Throttled update superseded by immediate update or cancellation
                }
            }, token);
        }

        return null;
    }

    private void Publish(PendingPublish? pendingPublish)
    {
        if (pendingPublish is { } value)
        {
            DispatchSnapshot(value.Snapshot, value.Sequence);
        }
    }

    private HostActivitySnapshot ComputeSnapshotLocked()
    {
        HostActivityItem[] items = _items.Values
            .Select(x => x.Item)
            .OrderBy(i => i.StartedAt)
            .ThenBy(i => i.Id, StringComparer.Ordinal)
            .ToArray();

        if (items.Length == 0)
        {
            return HostActivitySnapshot.Idle;
        }

        bool hasActive = items.Any(i => !i.IsPaused && !i.IsWaitingRetry);
        bool isBusy = hasActive;
        bool isSleeping = !hasActive;

        string? sleepReason = null;
        if (isSleeping)
        {
            List<string> reasons = new();
            foreach (HostActivityItem item in items)
            {
                if (item.IsPaused)
                {
                    reasons.Add(string.IsNullOrWhiteSpace(item.PauseReason)
                        ? $"{item.Name}: 已暂停"
                        : $"{item.Name}: {item.PauseReason}");
                }
                else if (item.IsWaitingRetry)
                {
                    reasons.Add(string.IsNullOrWhiteSpace(item.RetryReason)
                        ? $"{item.Name}: 等待延迟重试"
                        : $"{item.Name}: {item.RetryReason}");
                }
            }

            sleepReason = reasons.Count > 0 ? string.Join("; ", reasons) : null;
        }

        string? activeSummary = null;
        if (isBusy)
        {
            HostActivityItem? primary = items.FirstOrDefault(i => !i.IsPaused && !i.IsWaitingRetry);
            if (primary is not null)
            {
                activeSummary = string.IsNullOrWhiteSpace(primary.Detail)
                    ? primary.Name
                    : $"{primary.Name} ({primary.Detail})";
            }
        }

        return new HostActivitySnapshot(
            isBusy,
            isSleeping,
            sleepReason,
            activeSummary,
            Array.AsReadOnly(items));
    }

    private void DispatchSnapshot(HostActivitySnapshot snapshot, long sequence)
    {
        lock (_dispatchLock)
        {
            // Monotonic delivery: drop stale out-of-order snapshots
            if (sequence <= _deliveredSequence)
            {
                return;
            }

            _deliveredSequence = sequence;

            // Isolate throwing Changed event handlers
            EventHandler<HostActivitySnapshot>? handler = Changed;
            if (handler is not null)
            {
                foreach (Delegate d in handler.GetInvocationList())
                {
                    try
                    {
                        ((EventHandler<HostActivitySnapshot>)d)(this, snapshot);
                    }
                    catch (Exception exception)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"Host activity Changed handler failed: {exception}");
                    }
                }
            }

            IObserver<HostActivitySnapshot>[] observersCopy;
            lock (_lock)
            {
                observersCopy = _observers.ToArray();
            }

            // Isolate throwing observers
            foreach (IObserver<HostActivitySnapshot> observer in observersCopy)
            {
                try
                {
                    observer.OnNext(snapshot);
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Host activity observer failed: {exception}");
                }
            }

            if (!snapshot.IsBusy)
            {
                _idleTcs.TrySetResult();
            }
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        Task? inFlightThrottle;
        lock (_lock)
        {
            inFlightThrottle = _throttleTask;
        }

        if (inFlightThrottle is not null)
        {
            try
            {
                await inFlightThrottle.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public async Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        await FlushAsync(cancellationToken).ConfigureAwait(false);

        while (true)
        {
            Task idleTask;
            lock (_lock)
            {
                if (!_current.IsBusy)
                {
                    return;
                }

                idleTask = _idleTcs.Task;
            }

            await idleTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            await FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        IObserver<HostActivitySnapshot>[] observersCopy;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _throttleCts?.Cancel();
            _throttleCts = null;
            _throttleTask = null;
            _items.Clear();
            _current = HostActivitySnapshot.Idle;
            _idleTcs.TrySetResult();

            observersCopy = _observers.ToArray();
            _observers.Clear();
        }

        foreach (IObserver<HostActivitySnapshot> observer in observersCopy)
        {
            try
            {
                observer.OnCompleted();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Host activity observer completion failed: {exception}");
            }
        }
    }

    private sealed class ActivityScope : IActivityScope
    {
        private readonly HostActivityTracker _tracker;
        private int _disposed;

        public ActivityScope(HostActivityTracker tracker, string id)
        {
            _tracker = tracker;
            Id = id;
        }

        public string Id { get; }

        public void UpdateDetail(string detail)
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                _tracker.UpdateScopeDetail(Id, detail);
            }
        }

        public void SetPaused(bool paused, string? reason = null)
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                _tracker.SetScopePaused(Id, paused, reason);
            }
        }

        public void SetWaitingRetry(bool waiting, string? reason = null)
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                _tracker.SetScopeWaitingRetry(Id, waiting, reason);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _tracker.EndScope(Id);
            }
        }
    }

    private sealed class SnapshotObservable : IObservable<HostActivitySnapshot>
    {
        private readonly HostActivityTracker _tracker;

        public SnapshotObservable(HostActivityTracker tracker)
        {
            _tracker = tracker;
        }

        public IDisposable Subscribe(IObserver<HostActivitySnapshot> observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            HostActivitySnapshot initial;
            bool isDisposed;
            lock (_tracker._lock)
            {
                isDisposed = _tracker._disposed;
                initial = _tracker._current;
                if (!isDisposed)
                {
                    _tracker._observers.Add(observer);
                }
            }

            if (isDisposed)
            {
                try
                {
                    observer.OnCompleted();
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Disposed host activity observer completion failed: {exception}");
                }

                return EmptySubscription.Instance;
            }

            // Deliver initial snapshot outside tracker lock, safe against throwing observers
            try
            {
                observer.OnNext(initial);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Initial host activity observer notification failed: {exception}");
            }

            return new Subscription(_tracker, observer);
        }

        private sealed class EmptySubscription : IDisposable
        {
            public static EmptySubscription Instance { get; } = new();

            public void Dispose()
            {
            }
        }

        private sealed class Subscription : IDisposable
        {
            private readonly HostActivityTracker _tracker;
            private readonly IObserver<HostActivitySnapshot> _observer;
            private int _disposed;

            public Subscription(HostActivityTracker tracker, IObserver<HostActivitySnapshot> observer)
            {
                _tracker = tracker;
                _observer = observer;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    lock (_tracker._lock)
                    {
                        _tracker._observers.Remove(_observer);
                    }
                }
            }
        }
    }

    private readonly record struct PendingPublish(HostActivitySnapshot Snapshot, long Sequence);
}
