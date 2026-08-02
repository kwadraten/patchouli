using Patchouli.Core.Diagnostics;
using Patchouli.Core.Results;

namespace Patchouli.Infrastructure.Mcp;

/// <summary>
/// Immutable snapshot of the observable counters of a <see cref="SharedReadCache{TKey,TValue}"/>.
/// Counters never include content, query text, local paths, or secrets so they are safe for
/// performance logging.
/// </summary>
public sealed record ReadCacheMetrics(
    long Hits,
    long Misses,
    long Evictions,
    long Inserted,
    long Failed,
    long CachedEntries,
    long CachedBytes);

/// <summary>
/// Bounded, coalescing, rebuildable LRU for the runtime host shared read layer. It enforces a
/// hard memory cap using an object-estimated byte size, merges concurrent requests for the same
/// key into a single in-flight generation, and only caches successful values. Failed, canceled,
/// oversized, or exceptional generations are never cached and the in-flight entry is removed so
/// callers can retry. A waiter's own cancellation only cancels its wait, never the shared work.
/// </summary>
internal sealed class SharedReadCache<TKey, TValue>
    where TKey : notnull
    where TValue : class
{
    private readonly long _byteLimit;
    private readonly Func<TValue, long> _sizeEstimator;
    private readonly Dictionary<TKey, Entry> _entries = new();
    private readonly LinkedList<TKey> _lru = new();
    private readonly object _sync = new();

    private long _cachedBytes;
    private long _hits;
    private long _misses;
    private long _evictions;
    private long _inserted;
    private long _failed;

    public SharedReadCache(long byteLimit, Func<TValue, long>? sizeEstimator = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteLimit);
        _byteLimit = byteLimit;
        _sizeEstimator = sizeEstimator ?? (static _ => 1L);
    }

    public ReadCacheMetrics Metrics
    {
        get
        {
            lock (_sync)
            {
                return new ReadCacheMetrics(_hits, _misses, _evictions, _inserted, _failed, _entries.Count,
                    _cachedBytes);
            }
        }
    }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out Entry? entry) && entry.Value is not null)
            {
                Touch(entry);
                _hits++;
                value = entry.Value;
                return true;
            }
        }

        value = null!;
        return false;
    }

    /// <summary>
    /// Returns the cached value for <paramref name="key"/> or produces one through the shared
    /// coalescing factory. Concurrent callers await the same generation; a canceled or failed
    /// generation is removed from the cache and never returned again.
    /// </summary>
    public Task<Result<TValue>> GetOrAddAsync(
        TKey key,
        Func<CancellationToken, Task<Result<TValue>>> factory,
        CancellationToken cancellationToken)
    {
        Entry entry;
        TaskCompletionSource<Result<TValue>>? completion = null;
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out entry!))
            {
                Touch(entry);
                _hits++;
            }
            else
            {
                completion = new TaskCompletionSource<Result<TValue>>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                entry = new Entry(completion.Task);
                _entries.Add(key, entry);
                _misses++;
            }
        }

        if (completion is not null)
        {
            _ = ProduceAsync(key, entry, completion, factory);
        }

        return entry.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Explicitly invalidates one entry, for example after a commit notification.</summary>
    public void Invalidate(TKey key)
    {
        lock (_sync)
        {
            if (_entries.Remove(key, out Entry? entry) && entry.Node is not null)
            {
                _lru.Remove(entry.Node);
                _cachedBytes -= entry.Size;
            }
        }
    }

    /// <summary>
    /// Removes every entry matching <paramref name="predicate"/>. Used to invalidate all
    /// projections of one page or revision after a Box edit without clearing the whole cache.
    /// Explicit invalidations are not counted as budget evictions.
    /// </summary>
    public int EvictWhere(Func<TKey, bool> predicate)
    {
        lock (_sync)
        {
            TKey[] keys = _entries.Keys.Where(predicate).ToArray();
            foreach (TKey key in keys)
            {
                if (_entries.Remove(key, out Entry? entry) && entry.Node is not null)
                {
                    _lru.Remove(entry.Node);
                    _cachedBytes -= entry.Size;
                }
            }

            return keys.Length;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
            _lru.Clear();
            _cachedBytes = 0;
        }
    }

    private async Task ProduceAsync(
        TKey key,
        Entry entry,
        TaskCompletionSource<Result<TValue>> completion,
        Func<CancellationToken, Task<Result<TValue>>> factory)
    {
        try
        {
            Result<TValue> result = await factory(CancellationToken.None);
            lock (_sync)
            {
                if (result.IsSuccess)
                {
                    if (TryCache(key, entry, result.Value))
                    {
                        _inserted++;
                    }
                }
                else
                {
                    _failed++;
                    RemoveEntry(key, entry);
                }
            }

            completion.TrySetResult(result);
        }
        catch (OperationCanceledException exception)
        {
            lock (_sync)
            {
                _failed++;
                RemoveEntry(key, entry);
            }

            completion.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.shared-read-cache"))
        {
            lock (_sync)
            {
                _failed++;
                RemoveEntry(key, entry);
            }

            completion.TrySetException(exception);
        }
    }

    private bool TryCache(TKey key, Entry entry, TValue value)
    {
        entry.Value = value;
        entry.Size = Math.Max(1L, _sizeEstimator(value));
        if (entry.Size > _byteLimit)
        {
            _entries.Remove(key);
            return false;
        }

        entry.Node = _lru.AddFirst(key);
        _cachedBytes += entry.Size;
        while (_cachedBytes > _byteLimit && _lru.Last is { } oldest)
        {
            TKey oldestKey = oldest.Value;
            _lru.RemoveLast();
            if (_entries.Remove(oldestKey, out Entry? removed))
            {
                _cachedBytes -= removed.Size;
                _evictions++;
            }
        }

        return true;
    }

    private void RemoveEntry(TKey key, Entry entry)
    {
        if (_entries.TryGetValue(key, out Entry? current) && ReferenceEquals(current, entry))
        {
            _entries.Remove(key);
        }
    }

    private void Touch(Entry entry)
    {
        if (entry.Node is null)
        {
            return;
        }

        _lru.Remove(entry.Node);
        _lru.AddFirst(entry.Node);
    }

    private sealed class Entry(Task<Result<TValue>> task)
    {
        public Task<Result<TValue>> Task { get; } = task;
        public TValue? Value { get; set; }
        public LinkedListNode<TKey>? Node { get; set; }
        public long Size { get; set; }
    }
}
