namespace Patchouli.Ocr;

/// <summary>
/// A bounded LRU cache of open PDFium document sessions shared across pages of one viewing
/// session. Opening a document once and reusing the native handle across page renders is the
/// primary win; the cache enforces a hard cap so a long browsing session never keeps an
/// unbounded number of documents open. Evicted and explicitly evicted sessions are disposed
/// deterministically, and sessions that are still rendering are never evicted.
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
    private readonly Dictionary<string, Task<IPdfPageSession>> _pendingOpens = new(StringComparer.OrdinalIgnoreCase);
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
    /// cache can evict and dispose it later.
    /// </summary>
    public async Task<IPdfPageSession> GetOrOpenAsync(string path,
        CancellationToken cancellationToken = default)
    {
        string key = Path.GetFullPath(path);
        Task<IPdfPageSession> pending;
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
                pending = OpenSessionCoreAsync(key);
                _pendingOpens.Add(key, pending);
            }
        }

        IPdfPageSession opened = await pending.WaitAsync(cancellationToken);

        IPdfPageSession result;
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_pendingOpens.TryGetValue(key, out Task<IPdfPageSession>? current) &&
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

        await DrainDisposedAsync();
        return result;
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
            await session.DisposeAsync();
        }
    }

    private async Task<IPdfPageSession> OpenSessionCoreAsync(string key)
    {
        try
        {
            IPdfPageSession session = await _openFactory(key, CancellationToken.None);
            Opens++;
            return session;
        }
        catch
        {
            lock (_sync)
            {
                _pendingOpens.Remove(key);
            }

            throw;
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
            await session.DisposeAsync();
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
}
