using Patchouli.Core.Diagnostics;

namespace Patchouli.Ocr;

/// <summary>
/// A bounded LRU cache of open PDFium document sessions shared across pages of one viewing
/// session. Opening a document once and reusing the native handle across page renders is the
/// primary win; the cache enforces a hard cap so a long browsing session never keeps an
/// unbounded number of documents open. Evicted and explicitly evicted sessions are disposed
/// deterministically, and sessions that are still rendering are never evicted.
///
/// In-flight opens use a register-before-start single-flight (issue #31): the pending entry
/// is placed in the dictionary before the open is started, its shared task exists from the
/// moment of registration, and failure cleanup runs inside the shared open itself with a
/// conditional (reference-equal) removal — so a factory that throws synchronously cannot
/// leave a faulted entry behind, and the next acquire retries the open. Waiters await the
/// shared task with their own token; cancelling one waiter never cancels the shared open.
/// If every waiter leaves before the open completes, the finished session is closed instead
/// of being cached or leaked. Pending opens (removed on success and on failure) stay a
/// separate concern from cached sessions (closed when their in-use count drains or they are
/// evicted).
/// </summary>
public sealed class PdfiumSessionCache : IAsyncDisposable
{
    public const int DefaultMaxEntries = 12;
    public const long DefaultMaxBytes = 768L * 1024 * 1024;
    private const long PerEntryOverhead = 64 * 1024;

    private readonly int _maxEntries;
    private readonly long _maxBytes;
    private readonly Func<string, CancellationToken, Task<IPdfPageSession>> _openFactory;
    private readonly object _sync = new();
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new();
    private readonly Dictionary<string, PendingOpen> _pendingOpens = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IPdfPageSession> _sessionsToDispose = [];
    private long _cachedBytes;
    private bool _disposed;

    public PdfiumSessionCache(Func<string, CancellationToken, Task<IPdfPageSession>> openFactory,
        int maxEntries = DefaultMaxEntries, long maxBytes = DefaultMaxBytes)
    {
        ArgumentNullException.ThrowIfNull(openFactory);
        if (maxEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        }

        _openFactory = openFactory;
        _maxEntries = maxEntries;
        _maxBytes = maxBytes;
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count;
            }
        }
    }

    public long CachedBytes
    {
        get
        {
            lock (_sync)
            {
                return _cachedBytes;
            }
        }
    }

    public int InUseCount
    {
        get
        {
            lock (_sync)
            {
                return _entries.Values.Sum(entry => entry.InUse);
            }
        }
    }

    public long Opens { get; private set; }
    public long Hits { get; private set; }
    public long Misses { get; private set; }
    public long Evictions { get; private set; }

    /// <summary>
    /// Returns the cached session for <paramref name="path"/> or opens a new one through the
    /// factory. Concurrent callers for the same path share a single in-flight open. The
    /// returned session must be released with <see cref="Return"/> after rendering so the
    /// cache can evict and dispose it later. If an eviction raced the in-flight open, the
    /// returned session may already be disposed; callers must treat a disposed-session
    /// failure as retryable (the next call reopens cleanly).
    /// </summary>
    public async Task<IPdfPageSession> GetOrOpenAsync(string path,
        CancellationToken cancellationToken = default)
    {
        string key = Path.GetFullPath(path);
        PendingOpen pending;
        bool startOpen = false;
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_entries.TryGetValue(key, out CacheEntry? cached))
            {
                Hits++;
                Touch(cached);
                cached.InUse++;
                return cached.Session;
            }

            Misses++;
            if (!_pendingOpens.TryGetValue(key, out pending!))
            {
                // Register first: the entry (and its shared task) is visible in the dictionary
                // before the open starts, so the open's own failure cleanup can remove it.
                pending = new PendingOpen();
                _pendingOpens.Add(key, pending);
                startOpen = true;
            }

            pending.Waiters++;
        }

        if (startOpen)
        {
            _ = OpenSessionCoreAsync(key, pending);
        }

        try
        {
            IPdfPageSession opened = await pending.Task.WaitAsync(cancellationToken);

            IPdfPageSession result;
            lock (_sync)
            {
                ThrowIfDisposed();
                if (pending.Evicted)
                {
                    if (!pending.Disposed)
                    {
                        pending.Disposed = true;
                        _sessionsToDispose.Add(opened);
                    }

                    result = opened;
                }
                else
                {
                    pending.Claimed = true;
                    if (_pendingOpens.TryGetValue(key, out PendingOpen? current) &&
                        ReferenceEquals(current, pending))
                    {
                        _pendingOpens.Remove(key);
                    }

                    // Every awaiter of the shared pending open receives the same session instance, so
                    // reusing the existing entry never leaves an unowned duplicate to dispose.
                    if (_entries.TryGetValue(key, out CacheEntry? existing))
                    {
                        existing.InUse++;
                        result = existing.Session;
                    }
                    else
                    {
                        CacheEntry entry = new(opened, EstimateBytes(key));
                        _entries.Add(key, entry);
                        entry.Node = _lru.AddFirst(key);
                        entry.InUse = 1;
                        _cachedBytes += entry.EstimatedBytes;
                        result = opened;
                    }

                    EvictWhileOverBudgetLocked();
                }
            }

            await DrainDisposedAsync();
            return result;
        }
        finally
        {
            // The last waiter to leave an open that completed but was never claimed into the
            // cache closes it, so a session whose waiters all cancelled cannot leak.
            IPdfPageSession? unclaimed = null;
            lock (_sync)
            {
                pending.Waiters--;
                if (pending.Waiters == 0 && pending.Session is { } session && !pending.Claimed && !pending.Disposed)
                {
                    pending.Disposed = true;
                    if (_pendingOpens.TryGetValue(key, out PendingOpen? current) &&
                        ReferenceEquals(current, pending))
                    {
                        _pendingOpens.Remove(key);
                    }

                    unclaimed = session;
                }
            }

            if (unclaimed is not null)
            {
                // Converged like the drain path: a close failure here must not throw out of
                // the finally and mask the waiter's own outcome (result, cancellation, or
                // failure), which is already decided by the time the last waiter leaves.
                await DisposeSessionConvergedAsync(unclaimed, "close-unclaimed-session");
            }
        }
    }

    /// <summary>
    /// Signals the end of one render against a session acquired from <see cref="GetOrOpenAsync"/>.
    /// </summary>
    public void Return(IPdfPageSession session)
    {
        bool mustDrain;
        lock (_sync)
        {
            if (_entries.TryGetValue(session.Path, out CacheEntry? entry) &&
                ReferenceEquals(entry.Session, session))
            {
                entry.InUse = Math.Max(0, entry.InUse - 1);
                if (entry.InUse == 0 && entry.EvictWhenIdle)
                {
                    RemoveEntryLocked(session.Path, entry);
                    _sessionsToDispose.Add(entry.Session);
                }
                else
                {
                    EvictWhileOverBudgetLocked();
                }
            }

            mustDrain = _sessionsToDispose.Count > 0;
        }

        if (mustDrain)
        {
            _ = DrainDisposedAsync();
        }
    }

    /// <summary>
    /// Closes and removes the session for a resolved path, for example when a document's
    /// source becomes invalid. In-use sessions are evicted lazily on the next release.
    /// </summary>
    public async Task EvictPathAsync(string path)
    {
        string key = Path.GetFullPath(path);
        IPdfPageSession? toDispose = null;
        lock (_sync)
        {
            if (_pendingOpens.TryGetValue(key, out PendingOpen? pending))
            {
                pending.Evicted = true;
            }

            if (_entries.TryGetValue(key, out CacheEntry? entry))
            {
                if (entry.InUse > 0)
                {
                    entry.EvictWhenIdle = true;
                }
                else
                {
                    RemoveEntryLocked(key, entry);
                    toDispose = entry.Session;
                }
            }
        }

        if (toDispose is not null)
        {
            await toDispose.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        IPdfPageSession[] sessions;
        lock (_sync)
        {
            _disposed = true;
            sessions = _entries.Values.Select(entry => entry.Session).ToArray();
            _entries.Clear();
            _lru.Clear();
            _cachedBytes = 0;
            _pendingOpens.Clear();
            sessions = sessions.Concat(_sessionsToDispose).Distinct().ToArray();
            _sessionsToDispose.Clear();
        }

        foreach (IPdfPageSession session in sessions)
        {
            try
            {
                await session.DisposeAsync();
            }
            catch (Exception exception)
            {
                // Teardown continues with the remaining sessions: one session failing
                // to close must not leak the rest or fault the cache's own disposal.
                UnexpectedExceptionReporter.Report(exception, "ocr.pdfium-session-cache",
                    "dispose-session");
            }
        }
    }

    private async Task OpenSessionCoreAsync(string key, PendingOpen pending)
    {
        IPdfPageSession session;
        try
        {
            // The shared open is bound to no single caller: CancellationToken.None.
            session = await _openFactory(key, CancellationToken.None);
        }
        catch (Exception exception)
        {
            // Failure cleanup lives inside the shared open and only removes its own entry;
            // because registration happened before the open started, a factory that throws
            // synchronously is cleaned up here too and never leaves a faulted entry behind.
            lock (_sync)
            {
                if (_pendingOpens.TryGetValue(key, out PendingOpen? current) &&
                    ReferenceEquals(current, pending))
                {
                    _pendingOpens.Remove(key);
                }
            }

            pending.Completion.TrySetException(exception);
            return;
        }

        bool dispose = false;
        lock (_sync)
        {
            Opens++;
            pending.Session = session;
            if (_disposed || pending.Evicted || pending.Waiters == 0)
            {
                if (!pending.Disposed)
                {
                    pending.Disposed = true;
                    dispose = true;
                }

                if (_pendingOpens.TryGetValue(key, out PendingOpen? current) &&
                    ReferenceEquals(current, pending))
                {
                    _pendingOpens.Remove(key);
                }
            }
        }

        pending.Completion.TrySetResult(session);
        if (dispose)
        {
            try
            {
                await session.DisposeAsync();
            }
            catch (Exception exception)
            {
                // The session was opened but never claimed; a failing close is reported,
                // not faulted into the (already completed) shared open.
                UnexpectedExceptionReporter.Report(exception, "ocr.pdfium-session-cache",
                    "dispose-unclaimed-session");
            }
        }
    }

    private void EvictWhileOverBudgetLocked()
    {
        while ((_entries.Count > _maxEntries || _cachedBytes > _maxBytes) && _lru.Last is { } oldest)
        {
            LinkedListNode<string>? candidate = oldest;
            CacheEntry? entry = null;
            while (candidate is not null &&
                   (!_entries.TryGetValue(candidate.Value, out entry) || entry.InUse > 0))
            {
                candidate = candidate.Previous;
            }

            if (candidate is null || entry is null)
            {
                return;
            }

            _entries.Remove(candidate.Value);
            _lru.Remove(candidate);
            _cachedBytes -= entry.EstimatedBytes;
            Evictions++;
            _sessionsToDispose.Add(entry.Session);
        }
    }

    private void RemoveEntryLocked(string key, CacheEntry entry)
    {
        _entries.Remove(key);
        _lru.Remove(entry.Node!);
        _cachedBytes -= entry.EstimatedBytes;
        Evictions++;
    }

    // A session that fails to close must not fault whichever caller happened to dispose
    // it: by the time this runs the session is already detached from the cache (or was
    // never claimed into it), and the caller's own outcome and lease accounting have to
    // stay unaffected. Report the failure and converge.
    private async Task DisposeSessionConvergedAsync(IPdfPageSession session, string operation)
    {
        try
        {
            await session.DisposeAsync();
        }
        catch (Exception exception)
        {
            UnexpectedExceptionReporter.Report(exception, "ocr.pdfium-session-cache", operation);
        }
    }

    private async Task DrainDisposedAsync()
    {
        IPdfPageSession[] sessions;
        lock (_sync)
        {
            if (_sessionsToDispose.Count == 0)
            {
                return;
            }

            sessions = _sessionsToDispose.ToArray();
            _sessionsToDispose.Clear();
        }

        foreach (IPdfPageSession session in sessions)
        {
            await DisposeSessionConvergedAsync(session, "drain-disposed-session");
        }
    }

    private void Touch(CacheEntry entry)
    {
        if (entry.Node is null)
        {
            return;
        }

        _lru.Remove(entry.Node);
        _lru.AddFirst(entry.Node);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static long EstimateBytes(string path)
    {
        try
        {
            return new FileInfo(path).Length + PerEntryOverhead;
        }
        catch (Exception)
        {
            return PerEntryOverhead;
        }
    }

    private sealed class CacheEntry(IPdfPageSession session, long estimatedBytes)
    {
        public IPdfPageSession Session { get; } = session;
        public long EstimatedBytes { get; } = estimatedBytes;
        public LinkedListNode<string>? Node { get; set; }
        public int InUse { get; set; }
        public bool EvictWhenIdle { get; set; }
    }

    private sealed class PendingOpen
    {
        public TaskCompletionSource<IPdfPageSession> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IPdfPageSession> Task => Completion.Task;
        public bool Evicted { get; set; }
        public bool Disposed { get; set; }
        public int Waiters { get; set; }
        public IPdfPageSession? Session { get; set; }
        public bool Claimed { get; set; }
    }
}
