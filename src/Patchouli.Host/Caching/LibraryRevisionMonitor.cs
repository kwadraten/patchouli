using Patchouli.Core.Diagnostics;
using Patchouli.Core.Library;
using Patchouli.Core.Results;

namespace Patchouli.Host.Caching;

/// <summary>
/// Keeps the <see cref="LibraryItemCache"/> current. Normal desktop, CLI, and MCP writes are
/// serialized by the single runtime host and arrive through
/// <see cref="ILibraryRevisionService.ChangeCommitted"/>; a polling loop compares
/// <see cref="ILibraryRevisionService.GetCurrentRevisionAsync"/> against the last observed
/// revision only when a caller explicitly enables abnormal external-write recovery. The desktop
/// does not start that loop during normal operation. Local changes refresh their item scopes;
/// external changes reload the cache. The monitor never auto-starts; call <see cref="Start"/>
/// explicitly for recovery.
/// </summary>
public sealed class LibraryRevisionMonitor : IDisposable
{
    private static readonly Action<Exception, string, string?> FallbackUnexpectedExceptionReporter =
        static (exception, boundary, operation) =>
            System.Diagnostics.Trace.WriteLine(
                $"Patchouli unexpected error at {boundary}/{operation}: {exception}");

    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);

    private readonly ILibraryRevisionService _revisions;
    private readonly LibraryItemCache _cache;
    private readonly TimeSpan _pollInterval;
    private readonly Action<Exception, string, string?> _reportUnexpectedException;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _gate = new();
    private CancellationTokenSource? _pollCts;
    private Task? _pollLoop;
    private bool _disposed;
    private LibraryChangeSet? _pendingChanges;
    private bool _pendingFullReload;
    private bool _refreshScheduled;

    /// <summary>-1 until the first revision read establishes a baseline.</summary>
    private long _lastKnownRevision = -1;

    public LibraryRevisionMonitor(
        ILibraryRevisionService revisions,
        LibraryItemCache cache,
        TimeSpan? pollInterval = null,
        Action<Exception, string, string?>? reportUnexpectedException = null)
    {
        _revisions = revisions;
        _cache = cache;
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _reportUnexpectedException = reportUnexpectedException ?? FallbackUnexpectedExceptionReporter;
        _revisions.ChangeCommitted += OnChangeCommitted;
    }

    /// <summary>Raised when a revision change was detected via polling, i.e. not published in-process.</summary>
    public event EventHandler? ExternalChangeDetected;

    /// <summary>Raised after any revision change (in-process or external) once the cache reload succeeds.</summary>
    public event EventHandler? CacheRefreshed;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _pollLoop is not null;
            }
        }
    }

    /// <summary>Starts the cross-process polling loop. No-op when already running.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _pollLoop is not null)
            {
                return;
            }

            _pollCts = new CancellationTokenSource();
            _pollLoop = Task.Run(() => PollLoopAsync(_pollCts.Token));
        }
    }

    /// <summary>Stops the polling loop. The in-process subscription stays active until Dispose.</summary>
    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _pollCts;
            _pollCts = null;
            _pollLoop = null;
        }

        cts?.Cancel();
        cts?.Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Stop();
        _revisions.ChangeCommitted -= OnChangeCommitted;
        try
        {
            _refreshGate.Wait(TimeSpan.FromSeconds(5));
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            _refreshGate.Dispose();
        }
    }

    // ChangeCommitted subscribers must not run database work on the committer's thread, so the
    // refresh is scheduled fire-and-forget; the gate serializes it against poll-triggered refreshes.
    private void OnChangeCommitted(object? sender, LibraryRevisionCommittedEventArgs eventArgs)
    {
        bool schedule;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            LibraryChangeSet changes = eventArgs.ChangeSet;
            long knownRevision = Interlocked.Read(ref _lastKnownRevision);
            // A skipped revision may belong to another process and has no local ID scope.
            _pendingFullReload |= changes.IsEmpty || knownRevision < 0 ||
                                  changes.NewRevision > knownRevision + 1;
            _pendingChanges = _pendingChanges is null ? changes : MergeChanges(_pendingChanges, changes);
            schedule = !_refreshScheduled;
            _refreshScheduled = true;
            Interlocked.Exchange(ref _lastKnownRevision,
                Math.Max(Interlocked.Read(ref _lastKnownRevision), changes.NewRevision));
        }

        if (schedule)
        {
            _ = Task.Run(RefreshPendingChangesAsync);
        }
    }

    private async Task RefreshPendingChangesAsync()
    {
        int failures = 0;
        while (true)
        {
            LibraryChangeSet? changes;
            bool reload;
            lock (_gate)
            {
                changes = _pendingChanges;
                reload = _pendingFullReload;
                if (_disposed || (changes is null && !reload))
                {
                    _refreshScheduled = false;
                    return;
                }

                _pendingChanges = null;
                _pendingFullReload = false;
            }

            bool refreshed = await RefreshAsync(false, reload ? null : changes);
            if (refreshed)
            {
                failures = 0;
            }
            else if (++failures >= 3)
            {
                lock (_gate)
                {
                    _refreshScheduled = false;
                }

                // Preserve the full-reload flag for the next poll or local event.
                return;
            }
            else
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }
        }
    }

    private static LibraryChangeSet MergeChanges(LibraryChangeSet first, LibraryChangeSet second)
    {
        return new LibraryChangeSet(Math.Max(first.NewRevision, second.NewRevision),
            first.ItemIds.Concat(second.ItemIds).Distinct().ToArray(),
            first.DocumentInstanceIds.Concat(second.DocumentInstanceIds).Distinct().ToArray(),
            first.StyleIds.Concat(second.StyleIds).Distinct(StringComparer.Ordinal).ToArray(),
            first.PageIds.Concat(second.PageIds).Distinct().ToArray(),
            first.OcrRunIds.Concat(second.OcrRunIds).Distinct().ToArray(),
            first.CollectionIds.Concat(second.CollectionIds).Distinct().ToArray());
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using PeriodicTimer timer = new(_pollInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    await PollOnceAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                                      "host.library-revision-monitor", "poll-once"))
                {
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "host.library-revision-monitor", "poll-loop"))
        {
        }
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        Result<long> revision = await _revisions.GetCurrentRevisionAsync(cancellationToken);
        if (revision.IsFailure)
        {
            return;
        }

        long lastKnown = Interlocked.Read(ref _lastKnownRevision);
        if (lastKnown < 0)
        {
            AdvanceKnownRevision(revision.Value);
            await RefreshAsync(false);
            return;
        }

        bool retry;
        lock (_gate)
        {
            retry = _pendingFullReload && !_refreshScheduled;
            if (retry)
            {
                _pendingFullReload = false;
            }
        }

        if (revision.Value <= lastKnown && !retry)
        {
            return;
        }

        AdvanceKnownRevision(revision.Value);
        await RefreshAsync(revision.Value > lastKnown);
    }

    private void AdvanceKnownRevision(long revision)
    {
        lock (_gate)
        {
            Interlocked.Exchange(ref _lastKnownRevision,
                Math.Max(Interlocked.Read(ref _lastKnownRevision), revision));
        }
    }

    private async Task<bool> RefreshAsync(bool isExternal, LibraryChangeSet? changes = null)
    {
        try
        {
            await _refreshGate.WaitAsync();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return false;
                }
            }

            Result result;
            if (changes is null)
            {
                Result<long> before = await _revisions.GetCurrentRevisionAsync();
                result = before.IsFailure
                    ? Result.Failure(before.ErrorCode!, before.ErrorMessage!)
                    : await _cache.RefreshAsync();
                if (result.IsSuccess)
                {
                    Result<long> after = await _revisions.GetCurrentRevisionAsync();
                    if (after.IsFailure)
                    {
                        result = Result.Failure(after.ErrorCode!, after.ErrorMessage!);
                    }
                    else if (before.Value != after.Value)
                    {
                        // Rows and aggregate counts are separate reads. A concurrent commit
                        // requires a full retry before subsequent deltas can adjust these counts.
                        result = Result.Failure(AppErrorCodes.InvalidState,
                            "Library changed during cache refresh; retrying.");
                    }
                    else
                    {
                        AdvanceKnownRevision(after.Value);
                    }
                }
            }
            else
            {
                result = await _cache.ApplyChangesAsync(changes);
            }

            if (result.IsFailure)
            {
                lock (_gate)
                {
                    _pendingFullReload = true;
                }

                return false;
            }

            if (isExternal)
            {
                ExternalChangeDetected?.Invoke(this, EventArgs.Empty);
            }

            CacheRefreshed?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "host.library-revision-monitor", "cache-refresh"))
        {
            lock (_gate)
            {
                _pendingFullReload = true;
            }

            return false;
        }
        finally
        {
            try
            {
                _refreshGate.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
