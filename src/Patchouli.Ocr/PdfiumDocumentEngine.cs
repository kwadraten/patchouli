using System.Collections.Concurrent;
using Patchouli.Core.Import;
using Patchouli.Ocr.Native;
using PDFiumCore;

namespace Patchouli.Ocr;

/// <summary>
/// Native text-layer extraction surface for a PDF source. Implemented by
/// <see cref="PdfiumDocumentEngine"/>; host input sources depend on the interface so page
/// validation and context assembly can be tested without a live PDFium process.
/// </summary>
public interface IPdfTextExtractor
{
    /// <summary>
    /// Reads the complete native text layer of one page inside the PDFium scheduler without
    /// rendering. Returns an empty glyph list — not an exception — when the page has no text
    /// object; selected-provider failure semantics belong to the caller.
    /// </summary>
    Task<PdfiumTextPage> ExtractTextPageAsync(string pdfPath, int pageIndex,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A PDFium document that stays open across page renders so a viewing session does not
/// reopen the PDF for every page. The session is lease-counted (issue #31): rendering holds a
/// lease, disposal first blocks new leases, waits for in-flight leases to drain, and only
/// then closes the native document inside the scheduler. Closing is idempotent and the same
/// document is never closed twice.
/// </summary>
public sealed class PdfiumDocumentSession : IAsyncDisposable
{
    private readonly PdfiumDocumentEngine _engine;
    private readonly FpdfDocumentT _document;
    private readonly object _leaseSync = new();
    private int _leases;
    private bool _closing;
    private int _disposed;
    private int _closeStarted;
    private TaskCompletionSource? _drained;

    internal PdfiumDocumentSession(PdfiumDocumentEngine engine, FpdfDocumentT document, string path)
    {
        _engine = engine;
        _document = document;
        Path = path;
        PageCount = PdfiumNative.GetPageCount(document);
    }

    public string Path { get; }

    public int PageCount { get; }

    public Task<PdfiumPageBitmap> RenderPageAsync(int pageIndex, int dpi,
        CancellationToken cancellationToken = default)
    {
        return _engine.RenderPageAsync(this, pageIndex, dpi, cancellationToken);
    }

    /// <summary>Reads the native text layer of one page while the document stays open.</summary>
    public Task<PdfiumTextPage> ExtractTextPageAsync(int pageIndex,
        CancellationToken cancellationToken = default)
    {
        return _engine.ExtractTextPageAsync(this, pageIndex, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closeStarted, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        return new ValueTask(_engine.CloseSessionAsync(this));
    }

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal FpdfDocumentT Document
    {
        get
        {
            ThrowIfDisposed();
            return _document;
        }
    }

    /// <summary>Handle access for the close unit itself, which runs after MarkDisposed ordering is decided by the engine.</summary>
    internal FpdfDocumentT DocumentForClose => _document;

    internal void MarkDisposed()
    {
        Interlocked.Exchange(ref _disposed, 1);
    }

    internal void ThrowIfDisposed()
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException(nameof(PdfiumDocumentSession));
        }
    }

    /// <summary>Acquires a lease that keeps the native document alive until disposed.</summary>
    internal IDisposable AcquireLease()
    {
        lock (_leaseSync)
        {
            if (_closing || IsDisposed)
            {
                throw new ObjectDisposedException(nameof(PdfiumDocumentSession));
            }

            _leases++;
            return new Lease(this);
        }
    }

    /// <summary>Blocks new leases; existing leases keep the document alive until they drain.</summary>
    internal void BeginClose()
    {
        lock (_leaseSync)
        {
            _closing = true;
        }
    }

    internal Task WaitForLeasesAsync()
    {
        lock (_leaseSync)
        {
            if (_leases == 0)
            {
                return Task.CompletedTask;
            }

            _drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _drained.Task;
        }
    }

    private void ReleaseLease()
    {
        TaskCompletionSource? drained = null;
        lock (_leaseSync)
        {
            _leases--;
            if (_leases == 0)
            {
                drained = _drained;
                _drained = null;
            }
        }

        drained?.TrySetResult();
    }

    private sealed class Lease(PdfiumDocumentSession session) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                session.ReleaseLease();
            }
        }
    }
}

/// <summary>
/// Facade over the process-wide <see cref="PdfiumScheduler"/>: every native operation is a
/// single scheduled unit on the dedicated PDFium worker thread, executed through the internal
/// native layer (<see cref="PdfiumNative"/>). Engine instances are cheap and share the one
/// scheduler, so constructing several engines never creates competing native gates.
/// </summary>
public sealed class PdfiumDocumentEngine : IPdfTextExtractor
{
    public const string Version = "151.0.7920";
    private static readonly ConcurrentDictionary<PdfiumDocumentSession, byte> AllSessions = new();
    private static int _shutdownHookRegistered;

    public Task CheckAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        return RunAsync(() =>
        {
            PdfiumNative.EnsureInitialized();
            return true;
        }, PdfiumPriority.Interactive, cancellationToken);
    }

    public async Task<int> GetPageCountAsync(string pdfPath, CancellationToken cancellationToken = default)
    {
        return await RunAsync(() => PdfiumNative.GetPageCountForPath(pdfPath),
            PdfiumPriority.Interactive, cancellationToken);
    }

    public async Task<IReadOnlyList<PdfPageInfoResult>> GetPageInfosAsync(string pdfPath, int pageCount,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PdfiumPageInfoEntry> entries = await RunAsync(
            () => PdfiumNative.ReadPageInfos(pdfPath, pageCount), PdfiumPriority.Interactive, cancellationToken);
        return entries.Select(entry => entry.Success
                ? new PdfPageInfoResult(true,
                    new PdfPageInfo(entry.Info!.Value.Width, entry.Info.Value.Height, entry.Info.Value.Rotation), null)
                : new PdfPageInfoResult(false, null, entry.ErrorMessage))
            .ToArray();
    }

    /// <summary>
    /// Opens a PDF once and keeps the native document handle alive for the returned session.
    /// The handle is registered globally so process-exit shutdown closes every session before
    /// the PDFium library is destroyed.
    /// </summary>
    public async Task<PdfiumDocumentSession> OpenSessionAsync(string pdfPath,
        CancellationToken cancellationToken = default)
    {
        return await RunAsync(() =>
        {
            FpdfDocumentT document = PdfiumNative.LoadDocument(pdfPath);
            PdfiumDocumentSession session = new(this, document, Path.GetFullPath(pdfPath));
            AllSessions[session] = 0;
            return session;
        }, PdfiumPriority.Interactive, cancellationToken);
    }

    internal async Task<PdfiumPageBitmap> RenderPageAsync(PdfiumDocumentSession session, int pageIndex, int dpi,
        CancellationToken cancellationToken = default)
    {
        using IDisposable lease = session.AcquireLease();
        return await RunAsync(() => PdfiumNative.RenderPage(session.Document, pageIndex, dpi, session.Path),
            PdfiumPriority.Interactive, cancellationToken);
    }

    internal async Task<PdfiumTextPage> ExtractTextPageAsync(PdfiumDocumentSession session, int pageIndex,
        CancellationToken cancellationToken = default)
    {
        using IDisposable lease = session.AcquireLease();
        return await RunAsync(() => PdfiumNative.ReadTextPage(session.Document, pageIndex, session.Path),
            PdfiumPriority.Interactive, cancellationToken);
    }

    /// <summary>
    /// Reads one page's native text layer as a single scheduled unit — load document, load
    /// text page, walk character indices, close — with no page rendering. A document opened
    /// for repeated extraction should use <see cref="PdfiumDocumentSession"/> instead.
    /// </summary>
    public async Task<PdfiumTextPage> ExtractTextPageAsync(string pdfPath, int pageIndex,
        CancellationToken cancellationToken = default)
    {
        return await RunAsync(() =>
        {
            FpdfDocumentT document = PdfiumNative.LoadDocument(pdfPath);
            try
            {
                return PdfiumNative.ReadTextPage(document, pageIndex, pdfPath);
            }
            finally
            {
                PdfiumNative.CloseDocument(document);
            }
        }, PdfiumPriority.Background, cancellationToken);
    }

    internal async Task CloseSessionAsync(PdfiumDocumentSession session)
    {
        // Lease protocol: block new leases, wait for in-flight renders to drain, then close
        // the native document inside the scheduler.
        session.BeginClose();
        await session.WaitForLeasesAsync();
        await RunAsync(() =>
        {
            if (!session.IsDisposed)
            {
                PdfiumNative.CloseDocument(session.DocumentForClose);
                session.MarkDisposed();
            }

            _ = AllSessions.TryRemove(session, out _);
            return true;
        }, PdfiumPriority.Interactive, CancellationToken.None);
    }

    public async Task<PdfiumPageBitmap> RenderPageAsync(string pdfPath, int pageIndex, int dpi,
        CancellationToken cancellationToken = default)
    {
        return await RenderPageAsync(pdfPath, pageIndex, dpi, PdfiumPriority.Background, cancellationToken);
    }

    public async Task<PdfiumPageBitmap> RenderPageAsync(string pdfPath, int pageIndex, int dpi,
        PdfiumPriority priority, CancellationToken cancellationToken = default)
    {
        return await RunAsync(() =>
        {
            FpdfDocumentT document = PdfiumNative.LoadDocument(pdfPath);
            try
            {
                return PdfiumNative.RenderPage(document, pageIndex, dpi, pdfPath);
            }
            finally
            {
                PdfiumNative.CloseDocument(document);
            }
        }, priority, cancellationToken);
    }

    public async Task ExtractPagesAsync(string sourcePath, string outputPath, int startPageIndex, int pageCount,
        CancellationToken cancellationToken = default)
    {
        byte[] bytes = await RunAsync(() => PdfiumNative.ExtractPagesToBytes(sourcePath, startPageIndex, pageCount),
            PdfiumPriority.Background, cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        await File.WriteAllBytesAsync(outputPath, bytes, cancellationToken);
    }

    private static Task<T> RunAsync<T>(Func<T> nativeOp, PdfiumPriority priority, CancellationToken cancellationToken)
    {
        RegisterShutdownHookOnce();
        return PdfiumScheduler.Instance.RunAsync(nativeOp, priority, cancellationToken);
    }

    private static void RegisterShutdownHookOnce()
    {
        if (Interlocked.Exchange(ref _shutdownHookRegistered, 1) != 0)
        {
            return;
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                // Shutdown teardown runs through the same scheduler as every other native
                // call; the blocking entry point exists for exactly this release path.
                PdfiumScheduler.Instance.RunSynchronously(() =>
                {
                    foreach (PdfiumDocumentSession session in AllSessions.Keys)
                    {
                        if (!session.IsDisposed)
                        {
                            PdfiumNative.CloseDocument(session.DocumentForClose);
                            session.MarkDisposed();
                        }
                    }

                    AllSessions.Clear();
                    PdfiumNative.DestroyLibrary();
                });
            }
            catch (Exception)
            {
                // Process exit: there is no caller left to report teardown failures to.
            }
        };
    }
}
