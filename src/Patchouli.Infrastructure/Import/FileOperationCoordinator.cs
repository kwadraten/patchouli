using Patchouli.Core.Import;

namespace Patchouli.Infrastructure.Import;

/// <summary>
/// Issue #30: in-memory <see cref="IFileOperationCoordinator"/>. Slots live in a plain
/// dictionary guarded by one lock; a slot is removed as soon as its last holder releases,
/// so the registry never accumulates entries for paths nobody is operating on. Waiters
/// sleep on the current holders' release signals instead of polling. Only explicit
/// imports (add) ever wait — background scanners use the non-blocking try-claim. Waiting
/// is hold-and-wait, though: an add claims its source first and then waits for its
/// target, so two adds with crossed source/target paths could in theory wait on each
/// other. Such a cycle is not a permanent hang — every wait runs under the caller's
/// cancellation token, so cancelling either request breaks the cycle and the cancelled
/// lease releases everything it held.
/// </summary>
public sealed class FileOperationCoordinator : IFileOperationCoordinator
{
    /// <summary>
    /// Process-wide fallback for constructions that were not given an explicit instance.
    /// Coordination only works when every importer shares one registry, so the fallback
    /// is a shared instance rather than a silent no-op.
    /// </summary>
    public static FileOperationCoordinator Shared { get; } = new();

    /// <summary>
    /// The single source of truth for path-case semantics: paths compare
    /// case-insensitively only on Windows. Anything that keys state by path — claim
    /// slots, watcher dirty sets, committed-location lookups — must use this comparer
    /// (or its SQL equivalent) so two components never disagree about whether two paths
    /// name the same file.
    /// </summary>
    public static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly object _sync = new();
    private readonly Dictionary<string, ClaimSlot> _slots = new(PathComparer);

    public async Task<IFileOperationLease> ClaimAsync(
        IEnumerable<string> paths,
        CancellationToken cancellationToken = default)
    {
        Lease lease = new(this);
        try
        {
            await lease.AddAsync(paths, cancellationToken);
            return lease;
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }
    }

    public IFileOperationLease? TryClaim(IEnumerable<string> paths)
    {
        Lease lease = new(this);
        if (lease.TryAdd(paths))
        {
            return lease;
        }

        // TryAdd is all-or-nothing, so a failed attempt registered nothing to release.
        return null;
    }

    public bool IsClaimed(string path)
    {
        string key = Normalize(path);
        lock (_sync)
        {
            return _slots.ContainsKey(key);
        }
    }

    internal static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path);
    }

    private sealed class ClaimSlot
    {
        public int RefCount;
        public TaskCompletionSource Released = NewSignal();

        public static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Lease : IFileOperationLease
    {
        private readonly FileOperationCoordinator _owner;
        private readonly HashSet<string> _paths = new(PathComparer);
        private bool _disposed;

        public Lease(FileOperationCoordinator owner)
        {
            _owner = owner;
        }

        public IReadOnlyCollection<string> Paths
        {
            get
            {
                lock (_owner._sync)
                {
                    return _paths.ToArray();
                }
            }
        }

        public async Task AddAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
        {
            List<string> wanted = NormalizeNewPaths(paths);
            while (wanted.Count > 0)
            {
                Task[] waitOn;
                lock (_owner._sync)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    List<Task>? busy = null;
                    foreach (string key in wanted)
                    {
                        if (_owner._slots.TryGetValue(key, out ClaimSlot? slot))
                        {
                            (busy ??= []).Add(slot.Released.Task);
                        }
                    }

                    if (busy is null)
                    {
                        // All-or-nothing: registration happens only when every wanted
                        // path is free, in this same lock hold.
                        foreach (string key in wanted)
                        {
                            _owner._slots[key] = new ClaimSlot { RefCount = 1 };
                            _paths.Add(key);
                        }

                        return;
                    }

                    waitOn = busy.ToArray();
                }

                // Sleep until at least one holder releases, then re-evaluate the whole
                // set. A cancelled wait registered nothing in this attempt.
                TaskCompletionSource wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
                foreach (Task task in waitOn)
                {
                    _ = task.ContinueWith(
                        static (_, state) => ((TaskCompletionSource)state!).TrySetResult(),
                        wake,
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }

                await wake.Task.WaitAsync(cancellationToken);
            }
        }

        public bool TryAdd(IEnumerable<string> paths)
        {
            List<string> wanted = NormalizeNewPaths(paths);
            lock (_owner._sync)
            {
                if (_disposed)
                {
                    return false;
                }

                foreach (string key in wanted)
                {
                    if (_owner._slots.ContainsKey(key))
                    {
                        return false;
                    }
                }

                foreach (string key in wanted)
                {
                    _owner._slots[key] = new ClaimSlot { RefCount = 1 };
                    _paths.Add(key);
                }

                return true;
            }
        }

        public ValueTask DisposeAsync()
        {
            List<TaskCompletionSource> toRelease = [];
            lock (_owner._sync)
            {
                if (_disposed)
                {
                    return ValueTask.CompletedTask;
                }

                _disposed = true;
                foreach (string key in _paths)
                {
                    if (!_owner._slots.TryGetValue(key, out ClaimSlot? slot))
                    {
                        continue;
                    }

                    slot.RefCount--;
                    if (slot.RefCount <= 0)
                    {
                        _owner._slots.Remove(key);
                        toRelease.Add(slot.Released);
                        slot.Released = ClaimSlot.NewSignal();
                    }
                }

                _paths.Clear();
            }

            foreach (TaskCompletionSource signal in toRelease)
            {
                signal.TrySetResult();
            }

            return ValueTask.CompletedTask;
        }

        private List<string> NormalizeNewPaths(IEnumerable<string> paths)
        {
            ArgumentNullException.ThrowIfNull(paths);
            HashSet<string> wanted = new(PathComparer);
            lock (_owner._sync)
            {
                foreach (string path in paths)
                {
                    string key = Normalize(path);
                    if (!_paths.Contains(key))
                    {
                        wanted.Add(key);
                    }
                }
            }

            return wanted.ToList();
        }
    }
}
