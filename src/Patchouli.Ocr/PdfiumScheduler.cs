using System.Diagnostics;

namespace Patchouli.Ocr;

/// <summary>Priority of a scheduled PDFium native operation.</summary>
public enum PdfiumPriority
{
    /// <summary>Interactive work: preview renders, page counts, metadata reads.</summary>
    Interactive = 0,

    /// <summary>Background work: OCR renders, page extraction / splitting.</summary>
    Background = 1
}

/// <summary>
/// The single process-wide gate for every PDFium native call (issue #31). All native work runs
/// on one dedicated worker thread fed by two queues: <see cref="PdfiumPriority.Interactive"/>
/// items drain first, and after <see cref="BackgroundAgingThreshold"/> consecutive interactive
/// items one background item is served so background work cannot starve. Queued items honour
/// their cancellation token (a cancelled item never executes); an item that has started runs
/// to completion because a native call cannot be interrupted safely. Calls made from the
/// worker thread itself execute inline so nested scheduling cannot self-deadlock.
/// </summary>
public sealed class PdfiumScheduler
{
    private const long SlowHoldWarningMilliseconds = 200;

    private static readonly Lazy<PdfiumScheduler> Shared = new(() => new PdfiumScheduler());

    [ThreadStatic] private static bool _onWorkerThread;

    private readonly object _sync = new();
    private readonly LinkedList<WorkItem> _interactive = new();
    private readonly LinkedList<WorkItem> _background = new();
    private Thread? _worker;
    private int _consecutiveInteractive;

    internal PdfiumScheduler()
    {
    }

    /// <summary>The process-wide scheduler instance. Every PDFium engine shares it.</summary>
    public static PdfiumScheduler Instance => Shared.Value;

    /// <summary>
    /// Number of consecutive interactive items served before one queued background item is let
    /// through (aging against background starvation). Clamped to 1..64.
    /// </summary>
    public int BackgroundAgingThreshold { get; set; } = 12;

    /// <summary>Runs a synchronous native operation on the dedicated PDFium worker thread.</summary>
    public Task<T> RunAsync<T>(Func<T> nativeOp, PdfiumPriority priority, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nativeOp);
        if (_onWorkerThread)
        {
            // Re-entrant call from inside a scheduled native operation: execute inline instead
            // of queueing behind ourselves.
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(ExecuteGuarded(nativeOp));
            }
            catch (Exception exception)
            {
                return Task.FromException<T>(exception);
            }
        }

        WorkItem<T> item = new(nativeOp, cancellationToken);
        Enqueue(item, priority);
        return item.Task;
    }

    /// <summary>Runs a synchronous native operation on the dedicated PDFium worker thread.</summary>
    public Task RunAsync(Action nativeOp, PdfiumPriority priority, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nativeOp);
        return RunAsync(() =>
        {
            nativeOp();
            return true;
        }, priority, cancellationToken);
    }

    /// <summary>
    /// Blocking entry point reserved for release/shutdown paths that cannot await (process-exit
    /// teardown). Regular code must use <see cref="RunAsync{T}"/>.
    /// </summary>
    public T RunSynchronously<T>(Func<T> nativeOp, PdfiumPriority priority = PdfiumPriority.Interactive)
    {
        ArgumentNullException.ThrowIfNull(nativeOp);
        if (_onWorkerThread)
        {
            return ExecuteGuarded(nativeOp);
        }

        return RunAsync(nativeOp, priority, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>Blocking entry point reserved for release/shutdown paths; see the generic overload.</summary>
    public void RunSynchronously(Action nativeOp, PdfiumPriority priority = PdfiumPriority.Interactive)
    {
        ArgumentNullException.ThrowIfNull(nativeOp);
        RunSynchronously(() =>
        {
            nativeOp();
            return true;
        }, priority);
    }

    private static T ExecuteGuarded<T>(Func<T> nativeOp)
    {
        try
        {
            return nativeOp();
        }
        catch (DllNotFoundException exception)
        {
            throw new PdfRendererUnavailableException("PDFium native runtime could not be loaded.", exception);
        }
        catch (BadImageFormatException exception)
        {
            throw new PdfRendererUnavailableException("PDFium native runtime has an incompatible architecture.",
                exception);
        }
    }

    private void Enqueue(WorkItem item, PdfiumPriority priority)
    {
        lock (_sync)
        {
            EnsureWorkerStartedLocked();
            LinkedList<WorkItem> queue = priority == PdfiumPriority.Interactive ? _interactive : _background;
            item.Node = queue.AddLast(item);
            item.Queue = queue;
            if (item.CancellationToken.CanBeCanceled)
            {
                item.Registration = item.CancellationToken.Register(state =>
                {
                    (PdfiumScheduler scheduler, WorkItem queued) = ((PdfiumScheduler, WorkItem))state!;
                    scheduler.RemoveIfQueued(queued);
                }, (this, item));
            }

            Monitor.Pulse(_sync);
        }
    }

    private void RemoveIfQueued(WorkItem item)
    {
        lock (_sync)
        {
            if (item.Node is not null && item.Queue is not null)
            {
                item.Queue.Remove(item.Node);
                item.Node = null;
                item.Queue = null;
                item.TrySetCanceled();
            }
        }
    }

    private void EnsureWorkerStartedLocked()
    {
        if (_worker is not null)
        {
            return;
        }

        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "PdfiumWorker"
        };
        _worker.Start();
    }

    private void WorkerLoop()
    {
        _onWorkerThread = true;
        while (true)
        {
            WorkItem item;
            lock (_sync)
            {
                while (_interactive.Count == 0 && _background.Count == 0)
                {
                    Monitor.Wait(_sync);
                }

                item = DequeueNextLocked();
            }

            item.Execute();
        }
    }

    private WorkItem DequeueNextLocked()
    {
        bool serveBackground = _background.Count > 0 &&
                               (_interactive.Count == 0 || _consecutiveInteractive >= Math.Clamp(BackgroundAgingThreshold, 1, 64));
        LinkedList<WorkItem> queue = serveBackground ? _background : _interactive;
        WorkItem item = queue.First!.Value;
        queue.RemoveFirst();
        item.Node = null;
        item.Queue = null;
        _consecutiveInteractive = serveBackground ? 0 : _consecutiveInteractive + 1;
        return item;
    }

    private abstract class WorkItem
    {
        protected WorkItem(CancellationToken cancellationToken)
        {
            CancellationToken = cancellationToken;
            EnqueuedAt = Stopwatch.GetTimestamp();
        }

        public CancellationToken CancellationToken { get; }
        public long EnqueuedAt { get; }
        public LinkedListNode<WorkItem>? Node { get; set; }
        public LinkedList<WorkItem>? Queue { get; set; }
        public CancellationTokenRegistration Registration { get; set; }

        public abstract void Execute();
        public abstract bool TrySetCanceled();
    }

    private sealed class WorkItem<T> : WorkItem
    {
        private readonly Func<T> _nativeOp;
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WorkItem(Func<T> nativeOp, CancellationToken cancellationToken) : base(cancellationToken)
        {
            _nativeOp = nativeOp;
        }

        public Task<T> Task => _completion.Task;

        public override bool TrySetCanceled()
        {
            return _completion.TrySetCanceled(CancellationToken);
        }

        public override void Execute()
        {
            Registration.Dispose();
            if (CancellationToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled(CancellationToken);
                return;
            }

            long waitMs = (long)Stopwatch.GetElapsedTime(EnqueuedAt).TotalMilliseconds;
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                T result = ExecuteGuarded(_nativeOp);
                _completion.TrySetResult(result);
            }
            catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled(CancellationToken);
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
            finally
            {
                stopwatch.Stop();
                if (stopwatch.ElapsedMilliseconds > SlowHoldWarningMilliseconds)
                {
                    Trace.WriteLine(
                        $"[PdfiumScheduler] Native operation held the PDFium worker for {stopwatch.ElapsedMilliseconds} ms " +
                        $"after waiting {waitMs} ms (warning threshold {SlowHoldWarningMilliseconds} ms).");
                }
            }
        }
    }
}
