using System.Diagnostics;
using Avalonia.Threading;

namespace Patchouli.Terminal;

/// <summary>
/// Tracks embedded terminal sessions, removes them when their process exits, and performs
/// tiered cleanup (kill, wait, then force-kill the process tree) when they are stopped or the
/// host shuts down.
/// </summary>
public sealed class TerminalSessionManager : IAsyncDisposable
{
    private readonly HashSet<EmbeddedTerminal> _sessions = [];
    private readonly object _gate = new();
    private bool _disposed;

    public TerminalSessionManager()
    {
        AppDomain.CurrentDomain.ProcessExit += OnHostExit;
        Dispatcher.UIThread.ShutdownStarted += OnHostExit;
    }

    /// <summary>
    /// Raised when a tracked session is added or removed.
    /// </summary>
    public event EventHandler? SessionsChanged;

    /// <summary>
    /// Gets a snapshot of currently tracked sessions. A session leaves the list automatically
    /// when its process exits or its control is disposed.
    /// </summary>
    public IReadOnlyList<EmbeddedTerminal> ActiveSessions
    {
        get
        {
            lock (_gate)
            {
                return [.. _sessions];
            }
        }
    }

    /// <summary>
    /// Creates and tracks a new terminal control. Add it to the visual tree, then call
    /// <see cref="StartAsync"/>.
    /// </summary>
    public EmbeddedTerminal CreateTerminal()
    {
        ThrowIfDisposed();
        EmbeddedTerminal terminal = new();
        Track(terminal);
        return terminal;
    }

    /// <summary>
    /// Tracks an externally created terminal. Tracking is idempotent.
    /// </summary>
    public void Track(EmbeddedTerminal terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        ThrowIfDisposed();

        bool added;
        lock (_gate)
        {
            added = _sessions.Add(terminal);
            if (added)
            {
                terminal.Exited += OnTerminalExited;
                terminal.Disposed += OnTerminalDisposed;
            }
        }

        if (added)
        {
            SessionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Stops tracking a terminal without stopping or disposing it.
    /// </summary>
    public bool Untrack(EmbeddedTerminal terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        return UntrackCore(terminal);
    }

    /// <summary>
    /// Tracks and starts a terminal in one step.
    /// </summary>
    public async Task StartAsync(EmbeddedTerminal terminal, TerminalSessionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfDisposed();

        Track(terminal);
        try
        {
            await terminal.StartAsync(options, cancellationToken);
        }
        catch
        {
            UntrackCore(terminal);
            throw;
        }
    }

    /// <summary>
    /// Stops a session with tiered cleanup: request Kill, wait for exit, then force-kill the
    /// process tree if it is still alive.
    /// </summary>
    /// <returns><see langword="true"/> when the process is no longer running.</returns>
    public async Task<bool> StopAsync(EmbeddedTerminal terminal, TerminalStopOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        ThrowIfDisposed();
        return await StopCoreAsync(terminal, options ?? TerminalStopOptions.Default, cancellationToken);
    }

    /// <summary>
    /// Stops every tracked session with tiered cleanup.
    /// </summary>
    public Task StopAllAsync(TerminalStopOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return StopAllCoreAsync(options ?? TerminalStopOptions.Default, cancellationToken);
    }

    /// <summary>
    /// Best-effort synchronous release used from host shutdown notifications. It requests a kill,
    /// waits briefly, then force-kills any remaining process tree.
    /// </summary>
    public void ReleaseAll()
    {
        EmbeddedTerminal[] snapshot = [.. ActiveSessions];
        foreach (EmbeddedTerminal terminal in snapshot)
        {
            try
            {
                if (terminal.IsRunning)
                {
                    terminal.Stop();
                    terminal.WaitForExit(TerminalStopOptions.HostShutdown.GracePeriod);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
            {
                // The control can already be torn down while the host is shutting down.
                _ = exception;
            }

            ForceKill(terminal.Pid);
            terminal.ReleasePty();
            UntrackCore(terminal);
        }
    }

    /// <summary>
    /// Stops and disposes every tracked session, then stops listening for host shutdown.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        AppDomain.CurrentDomain.ProcessExit -= OnHostExit;
        Dispatcher.UIThread.ShutdownStarted -= OnHostExit;

        EmbeddedTerminal[] snapshot = [.. ActiveSessions];
        await StopAllCoreAsync(TerminalStopOptions.Default, CancellationToken.None);
        foreach (EmbeddedTerminal terminal in snapshot)
        {
            terminal.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private async Task<bool> StopCoreAsync(EmbeddedTerminal terminal, TerminalStopOptions options,
        CancellationToken cancellationToken)
    {
        options.Validate();
        int processId = terminal.Pid;
        if (!terminal.IsRunning)
        {
            terminal.ReleasePty();
            UntrackCore(terminal);
            return true;
        }

        terminal.Stop();
        if (await terminal.WaitForExitAsync(options.GracePeriod, cancellationToken))
        {
            terminal.ReleasePty();
            UntrackCore(terminal);
            return true;
        }

        ForceKill(processId > 0 ? processId : terminal.Pid);
        bool exited = await terminal.WaitForExitAsync(options.ForceWait, cancellationToken);
        terminal.ReleasePty();
        UntrackCore(terminal);
        return exited && !terminal.IsRunning;
    }

    private async Task StopAllCoreAsync(TerminalStopOptions options, CancellationToken cancellationToken)
    {
        EmbeddedTerminal[] snapshot = [.. ActiveSessions];
        await Task.WhenAll(snapshot.Select(terminal => StopCoreAsync(terminal, options, cancellationToken)));
    }

    private bool UntrackCore(EmbeddedTerminal terminal)
    {
        bool removed;
        lock (_gate)
        {
            removed = _sessions.Remove(terminal);
            if (removed)
            {
                terminal.Exited -= OnTerminalExited;
                terminal.Disposed -= OnTerminalDisposed;
            }
        }

        if (removed)
        {
            SessionsChanged?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    private void OnTerminalExited(object? sender, TerminalExitedEventArgs e)
    {
        if (sender is EmbeddedTerminal terminal)
        {
            UntrackCore(terminal);
        }
    }

    private void OnTerminalDisposed(object? sender, EventArgs e)
    {
        if (sender is EmbeddedTerminal terminal)
        {
            UntrackCore(terminal);
        }
    }

    private void OnHostExit(object? sender, EventArgs e)
    {
        ReleaseAll();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static void ForceKill(int processId)
    {
        if (processId <= 0)
        {
            return;
        }

        try
        {
            using Process process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch (ArgumentException)
        {
            // The process already exited between the PID lookup and the access.
        }
        catch (InvalidOperationException)
        {
            // The process exited while HasExited was being read.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Access denied or the process vanished; there is nothing else to do here.
        }
    }
}
