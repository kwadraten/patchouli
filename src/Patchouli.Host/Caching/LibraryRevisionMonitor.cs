using Patchouli.Core.Diagnostics;
using Patchouli.Core.Library;
using Patchouli.Core.Results;

namespace Patchouli.Host.Caching;

/// <summary>
/// Watches the persistent library revision so the <see cref="LibraryItemCache"/> stays current
/// for both in-process writes and writes from other processes sharing the same SQLite database
/// (the desktop UI and the standalone MCP server). In-process commits arrive via
/// <see cref="ILibraryRevisionService.ChangeCommitted"/>; a polling loop compares
/// <see cref="ILibraryRevisionService.GetCurrentRevisionAsync"/> against the last observed
/// revision to catch everything else. Any change triggers a full cache reload — per-changeset
/// invalidation is a future optimization. The monitor never auto-starts; call
/// <see cref="Start"/> explicitly. Timer and refresh failures are reported, never thrown.
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
            if (_pollLoop is not null)
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
        Stop();
        _revisions.ChangeCommitted -= OnChangeCommitted;
        _refreshGate.Dispose();
    }

    // ChangeCommitted subscribers must not run database work on the committer's thread, so the
    // refresh is scheduled fire-and-forget; the gate serializes it against poll-triggered refreshes.
    private void OnChangeCommitted(object? sender, LibraryRevisionCommittedEventArgs eventArgs)
    {
        Interlocked.Exchange(ref _lastKnownRevision, eventArgs.ChangeSet.NewRevision);
        _ = RefreshAsync(false);
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
            Interlocked.Exchange(ref _lastKnownRevision, revision.Value);
            return;
        }

        if (revision.Value == lastKnown)
        {
            return;
        }

        Interlocked.Exchange(ref _lastKnownRevision, revision.Value);
        await RefreshAsync(true);
    }

    private async Task RefreshAsync(bool isExternal)
    {
        await _refreshGate.WaitAsync();
        try
        {
            Result result = await _cache.RefreshAsync();
            if (result.IsFailure)
            {
                return;
            }

            if (isExternal)
            {
                ExternalChangeDetected?.Invoke(this, EventArgs.Empty);
            }

            CacheRefreshed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "host.library-revision-monitor", "cache-refresh"))
        {
        }
        finally
        {
            _refreshGate.Release();
        }
    }
}
