namespace Patchouli.Infrastructure.Caching;

/// <summary>
/// A thread-safe, byte-budgeted least-recently-used cache. Entries may be pinned so that
/// current-page (or otherwise hot) values survive eviction while prefetched, evictable
/// values are dropped first. Eviction never removes pinned entries and never caches a
/// single value that exceeds the budget by itself.
/// </summary>
public sealed class BoundedLruCache<TKey, TValue> where TKey : notnull
{
    private readonly long _byteLimit;
    private readonly Dictionary<TKey, Entry> _entries = new();
    private readonly LinkedList<TKey> _lru = new();
    private readonly object _sync = new();
    private long _cachedBytes;

    public BoundedLruCache(long byteLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteLimit);
        _byteLimit = byteLimit;
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

    public long Hits { get; private set; }
    public long Misses { get; private set; }
    public long Evictions { get; private set; }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out Entry? entry))
            {
                Hits++;
                Touch(entry);
                value = entry.Value;
                return true;
            }

            Misses++;
            value = default!;
            return false;
        }
    }

    public bool Contains(TKey key)
    {
        lock (_sync)
        {
            return _entries.ContainsKey(key);
        }
    }

    /// <summary>
    /// Adds or replaces a value, charging <paramref name="estimatedBytes"/> against the
    /// budget. Values larger than the whole budget are not cached. The entry is inserted
    /// as most-recently-used. When pinned, later <see cref="Pin"/> calls are not required
    /// for this insertion to survive the next eviction pass.
    /// </summary>
    public void Set(TKey key, TValue value, long estimatedBytes, bool pinned = false)
    {
        if (estimatedBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(estimatedBytes));
        }

        lock (_sync)
        {
            if (estimatedBytes > _byteLimit)
            {
                if (_entries.Remove(key, out Entry? removed))
                {
                    _cachedBytes -= removed.EstimatedBytes;
                    _lru.Remove(removed.Node!);
                }

                return;
            }

            if (_entries.TryGetValue(key, out Entry? existing))
            {
                _cachedBytes -= existing.EstimatedBytes;
                existing.EstimatedBytes = estimatedBytes;
                existing.Value = value;
                existing.IsPinned = pinned || existing.IsPinned;
                _cachedBytes += estimatedBytes;
                _lru.Remove(existing.Node!);
                _lru.AddFirst(existing.Node!);
            }
            else
            {
                Entry entry = new(value, estimatedBytes, _lru.AddFirst(key), pinned);
                _entries.Add(key, entry);
                _cachedBytes += estimatedBytes;
            }

            EvictWhileOverBudgetLocked();
        }
    }

    public void Pin(TKey key)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out Entry? entry))
            {
                entry.IsPinned = true;
                Touch(entry);
            }
        }
    }

    public void Unpin(TKey key)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out Entry? entry))
            {
                entry.IsPinned = false;
            }
        }
    }

    public bool IsPinned(TKey key)
    {
        lock (_sync)
        {
            return _entries.TryGetValue(key, out Entry? entry) && entry.IsPinned;
        }
    }

    public bool Evict(TKey key)
    {
        lock (_sync)
        {
            if (_entries.Remove(key, out Entry? entry))
            {
                _cachedBytes -= entry.EstimatedBytes;
                _lru.Remove(entry.Node!);
                Evictions++;
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Removes every entry matching <paramref name="predicate"/>. Used when a projection
    /// must be invalidated (for example all rasters of one document) without clearing the
    /// whole cache.
    /// </summary>
    public int EvictWhere(Func<TKey, bool> predicate)
    {
        lock (_sync)
        {
            TKey[] keys = _entries.Keys.Where(predicate).ToArray();
            foreach (TKey key in keys)
            {
                if (_entries.Remove(key, out Entry? entry))
                {
                    _cachedBytes -= entry.EstimatedBytes;
                    _lru.Remove(entry.Node!);
                    Evictions++;
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

    private void EvictWhileOverBudgetLocked()
    {
        while (_cachedBytes > _byteLimit && _lru.Count > 0)
        {
            LinkedListNode<TKey>? node = _lru.Last;
            while (node is not null && _entries[node.Value].IsPinned)
            {
                node = node.Previous;
            }

            if (node is null)
            {
                return;
            }

            TKey key = node.Value;
            Entry entry = _entries[key];
            _entries.Remove(key);
            _cachedBytes -= entry.EstimatedBytes;
            _lru.Remove(node);
            Evictions++;
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

    private sealed class Entry(TValue value, long estimatedBytes, LinkedListNode<TKey> node, bool isPinned)
    {
        public TValue Value { get; set; } = value;
        public long EstimatedBytes { get; set; } = estimatedBytes;
        public LinkedListNode<TKey> Node { get; } = node;
        public bool IsPinned { get; set; } = isPinned;
    }
}
