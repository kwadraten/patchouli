using Patchouli.Core.Diagnostics;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.Rendering;
using Patchouli.UI;

namespace Patchouli.Host.Composition;

/// <summary>
/// Periodic disk-cache maintenance (issue #57): sweeps the bounded page-render cache and
/// prunes stale files from the four ocr-work directories. Runs once at startup and then on a
/// timer bound to the host lifetime. All work is best-effort — per-file failures are skipped
/// and loop failures are reported through the host's unexpected-error sink, never thrown.
/// </summary>
public sealed class CacheMaintenanceService : IAsyncDisposable
{
    /// <summary>Files older than this inside ocr-work roots are treated as leftovers.</summary>
    internal static readonly TimeSpan OcrWorkFileGrace = TimeSpan.FromHours(24);

    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(5);

    private readonly PageRenderService _pageRenders;
    private readonly OcrStorageLocations _storage;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public CacheMaintenanceService(PageRenderService pageRenders, OcrStorageLocations storage,
        TimeProvider? timeProvider = null, TimeSpan? interval = null)
    {
        _pageRenders = pageRenders;
        _storage = storage;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _interval = interval ?? DefaultInterval;
    }

    /// <summary>
    /// Starts the maintenance loop bound to <paramref name="lifetimeToken"/> and runs one
    /// immediate pass (startup sweep) before returning.
    /// </summary>
    public async Task Start(CancellationToken lifetimeToken)
    {
        await RunOnceAsync(lifetimeToken);
        _loop = Task.Run(() => RunLoopAsync(lifetimeToken));
    }

    /// <summary>One maintenance pass: render-cache LRU sweep plus ocr-work pruning.</summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _pageRenders.SweepDiskCacheAsync();
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "host.cache-maintenance", "page-render-sweep"))
        {
            // Reported; the next pass retries.
        }

        foreach (string workRoot in OcrWorkRoots())
        {
            try
            {
                SweepOcrWorkRoot(workRoot, DateTimeOffset.UtcNow - OcrWorkFileGrace);
            }
            catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                                  "host.cache-maintenance", "ocr-work-sweep"))
            {
                // One unreadable root must not hide the others.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
                // The loop exits through the stop token; cancellation is the expected end.
            }
            catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                                  "host.cache-maintenance", "loop"))
            {
                // A faulted loop is reported; disposal still completes.
            }
        }

        _stop.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken lifetimeToken)
    {
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken, _stop.Token);
        using PeriodicTimer timer = new(_interval, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(linked.Token))
            {
                await RunOnceAsync(linked.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown or disposal stops the loop.
        }
    }

    private IEnumerable<string> OcrWorkRoots()
    {
        yield return _storage.MinerUWorkDirectory;
        yield return _storage.NdlKotenWorkDirectory;
        yield return _storage.NdlLiteWorkDirectory;
        yield return _storage.RapidOcrWorkDirectory;
    }

    /// <summary>
    /// Deletes files older than the grace cutoff and then prunes empty directories below the
    /// root; the root itself is kept. The walk never follows or deletes reparse points
    /// (junctions/symlinks), so linked directories can never pull external content into the
    /// sweep (issue #57 safety review).
    /// </summary>
    private static void SweepOcrWorkRoot(string root, DateTimeOffset olderThan)
    {
        if (!Directory.Exists(root) || ReparsePointGuard.IsReparsePoint(root))
        {
            return;
        }

        foreach (string file in ReparsePointGuard.EnumerateFilesRecursive(root).ToArray())
        {
            try
            {
                // Re-check at delete time: a path swapped for a link after enumeration is
                // skipped — the file itself and the directory it was found through
                // (deleting through a linked parent would remove external content).
                string? parent = Path.GetDirectoryName(file);
                if (parent is null || ReparsePointGuard.IsReparsePoint(parent) ||
                    ReparsePointGuard.IsReparsePoint(file))
                {
                    continue;
                }

                if (File.GetLastWriteTimeUtc(file) < olderThan.UtcDateTime)
                {
                    File.Delete(file);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Locked or transient files are retried by the next pass.
            }
        }

        foreach (string directory in ReparsePointGuard.EnumerateDirectoriesRecursive(root)
                     .OrderByDescending(path => path.Length).ToArray())
        {
            try
            {
                if (!ReparsePointGuard.IsReparsePoint(directory) &&
                    !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Non-empty or locked directories stay.
            }
        }
    }
}
