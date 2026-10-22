using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using Iciclecreek.Terminal;
using Porta.Pty;

namespace Patchouli.Terminal;

/// <summary>
/// A reusable Avalonia control that hosts an external command in a real PTY and wraps
/// <see cref="TerminalControl"/> with a stable, process-oriented surface.
/// </summary>
public sealed class EmbeddedTerminal : UserControl, IDisposable
{
    private readonly TerminalControl _terminal;
    private readonly Decoder _utf8Decoder = new UTF8Encoding(false, false).GetDecoder();
    private readonly object _decoderGate = new();
    private TaskCompletionSource<int>? _exitCompletion;
    private bool _disposed;
    private bool _started;
    private int _exitCode = -1;
    private bool _exitCodeKnown;
    private int _pid;

    public EmbeddedTerminal()
    {
        _terminal = new TerminalControl
        {
            Focusable = true
        };
        Content = _terminal;
        _terminal.OutputReceived += OnTerminalOutputReceived;
        _terminal.ProcessExited += OnTerminalProcessExited;
        _terminal.UrlClicked += OnTerminalUrlClicked;
    }

    /// <summary>
    /// Raised for each chunk of raw output bytes. The payload is a private copy, safe to retain.
    /// </summary>
    public event EventHandler<TerminalOutputEventArgs>? OutputReceived;

    /// <summary>
    /// Raised for incrementally decoded UTF-8 text. Multi-byte sequences split across reads are preserved.
    /// </summary>
    public event EventHandler<TerminalTextOutputEventArgs>? TextOutputReceived;

    /// <summary>
    /// Raised after the hosted process exits, including when it is killed.
    /// </summary>
    public event EventHandler<TerminalExitedEventArgs>? Exited;

    /// <summary>
    /// Raised once for each hyperlink the user activates with Ctrl+click, forwarding the
    /// terminal control's UrlClicked report unchanged. This layer only forwards; deciding
    /// whether and how to navigate (in-app routing, confirmation, external launch) is the
    /// host's responsibility, and the terminal itself never opens the URL.
    /// </summary>
    /// <remarks>
    /// Upstream limitation (Iciclecreek.Avalonia.Terminal 4.1.0): plain-text output is
    /// hit-tested with the hardcoded <c>https?://</c> regex in TerminalView — the detection
    /// entry point (FindUrlAtColumn) is internal and the package exposes no link-pattern or
    /// link-provider extension point — so a plain-text patchouli:// address is NOT clickable.
    /// OSC 8 declared hyperlinks are detected for any scheme, so terminal content can make a
    /// patchouli:// target clickable today by emitting the sequence instead of the bare
    /// address. A proper fix is an upstream PR making the URL detection schemes configurable.
    /// </remarks>
    public event EventHandler<TerminalLinkActivatedEventArgs>? LinkActivated;

    /// <summary>
    /// Raised when this control releases the terminal and its PTY.
    /// </summary>
    public event EventHandler? Disposed;

    /// <summary>
    /// Gets the underlying Iciclecreek terminal control for appearance or advanced integration.
    /// </summary>
    public TerminalControl TerminalControl => _terminal;

    /// <summary>
    /// Gets the operating-system process identifier of the hosted process, or zero before launch.
    /// </summary>
    public int Pid => _pid;

    /// <summary>
    /// Gets whether a hosted process is currently alive.
    /// </summary>
    public bool IsRunning
    {
        get
        {
            if (_disposed || !_started)
            {
                return false;
            }

            try
            {
                return _terminal.IsLive;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Gets the last reported exit code. Killed processes report <c>-1</c>.
    /// </summary>
    public int ExitCode => _exitCode;

    /// <summary>
    /// Gets whether <see cref="ExitCode"/> was reported by the terminal instead of being a kill fallback.
    /// </summary>
    public bool ExitCodeKnown => _exitCodeKnown;

    /// <summary>
    /// Gets the current terminal session identifier.
    /// </summary>
    public long SessionId => _disposed ? 0 : _terminal.SessionId;

    /// <summary>
    /// Gets the current emulator grid. Because the PTY size follows layout, this is also the
    /// size the shell observes after the control has been arranged.
    /// </summary>
    public TerminalSize CurrentSize => _disposed
        ? default
        : new TerminalSize(_terminal.Terminal.Cols, _terminal.Terminal.Rows);

    /// <summary>
    /// Launches the configured process. The control must already be attached to a visual tree and
    /// its template applied (for example, add it to a shown window and let one layout pass run).
    /// </summary>
    /// <remarks>
    /// Iciclecreek.Avalonia.Terminal has no public PTY resize API: subsequent size changes are
    /// driven by the control's layout, not by <see cref="TerminalSessionOptions.InitialCols"/> and
    /// <see cref="TerminalSessionOptions.InitialRows"/>, which only seed the grid when no layout size is available.
    /// </remarks>
    public async Task StartAsync(TerminalSessionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfDisposed();
        options.Validate();
        if (IsRunning)
        {
            throw new InvalidOperationException(
                "The embedded terminal already has a running process. Stop it or wait for Exited before starting another one.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        ApplyTemplate();
        if (_terminal.Template is null)
        {
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
            ApplyTemplate();
        }

        if (_terminal.Template is null)
        {
            throw new InvalidOperationException(
                "EmbeddedTerminal must be attached to a shown visual tree before StartAsync is called; " +
                "its terminal template has not been applied yet.");
        }

        _terminal.Process = options.Executable;
        _terminal.ProcessArgs = options.Arguments.ToList();
        _terminal.VerbatimCommandLine = options.VerbatimCommandLine;
        _terminal.StartingDirectory = string.IsNullOrWhiteSpace(options.WorkingDirectory)
            ? null
            : options.WorkingDirectory;
        _terminal.EnvironmentVariables = options.EnvironmentVariables is { Count: > 0 } environment
            ? new Dictionary<string, string>(environment, StringComparer.OrdinalIgnoreCase)
            : null;

        lock (_decoderGate)
        {
            _utf8Decoder.Reset();
        }

        TaskCompletionSource<int> exitCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _exitCompletion = exitCompletion;
        _exitCode = -1;
        _exitCodeKnown = false;
        _pid = 0;
        _started = true;

        // LaunchProcess takes the PTY dimensions from the emulator. Preserve the grid
        // calculated by layout: replacing it with the default 120x30 here also starts
        // full-screen applications at that smaller size, without another layout to fix it.
        UpdateLayout();
        if (_terminal.Bounds.Width <= 0 || _terminal.Bounds.Height <= 0)
        {
            ApplyInitialGrid(options.InitialCols, options.InitialRows);
        }

        try
        {
            await _terminal.LaunchProcess();
        }
        catch
        {
            _started = false;
            exitCompletion.TrySetResult(-1);
            throw;
        }

        _pid = _terminal.Pid;
    }

    /// <summary>
    /// Kills the hosted process. Does nothing when no process is running.
    /// </summary>
    public void Stop()
    {
        if (_disposed || !IsRunning)
        {
            return;
        }

        try
        {
            _terminal.Kill();
        }
        catch (InvalidOperationException)
        {
            // The terminal can reject Kill after its process already exited; that is the desired state.
        }
    }

    /// <summary>
    /// Alias for <see cref="Stop"/>.
    /// </summary>
    public void Kill()
    {
        Stop();
    }

    /// <summary>
    /// Waits for the hosted process to exit.
    /// </summary>
    /// <returns><see langword="true"/> when the process is no longer running.</returns>
    public async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!IsRunning)
        {
            return true;
        }

        TaskCompletionSource<int>? completion = _exitCompletion;
        if (completion is null)
        {
            return !IsRunning;
        }

        try
        {
            await completion.Task.WaitAsync(timeout, cancellationToken);
            return true;
        }
        catch (TimeoutException)
        {
            return !IsRunning;
        }
    }

    /// <summary>
    /// Synchronously waits for the hosted process to exit. Intended for host-shutdown cleanup.
    /// </summary>
    /// <returns><see langword="true"/> when the process is no longer running.</returns>
    public bool WaitForExit(TimeSpan timeout)
    {
        if (!IsRunning)
        {
            return true;
        }

        int milliseconds = (int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue);
        try
        {
            _terminal.WaitForExit(milliseconds);
        }
        catch (ObjectDisposedException)
        {
            return true;
        }

        return !IsRunning;
    }

    /// <summary>
    /// Writes UTF-8 input bytes to the hosted process. For control keys, pass their terminal byte
    /// sequences (for example <c>\r</c> to submit a command).
    /// </summary>
    public Task WriteInputAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (input.IsEmpty || !IsRunning)
        {
            return Task.CompletedTask;
        }

        string text = Encoding.UTF8.GetString(input.Span);
        return _terminal.SendInputAsync(text, cancellationToken);
    }

    /// <summary>
    /// Writes UTF-8 input bytes to the hosted process.
    /// </summary>
    public Task WriteInput(byte[] input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        return WriteInputAsync(input, cancellationToken);
    }

    /// <summary>
    /// Writes text to the hosted process verbatim. The caller is responsible for appending
    /// <c>\r</c> to submit a command.
    /// </summary>
    public Task WriteInputAsync(string text, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return string.IsNullOrEmpty(text) || !IsRunning
            ? Task.CompletedTask
            : _terminal.SendInputAsync(text, cancellationToken);
    }

    /// <summary>Pastes text without submitting it, honoring the application's bracketed-paste mode.</summary>
    public Task PasteTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(text) || !IsRunning)
        {
            return Task.CompletedTask;
        }

        string input = _terminal.Terminal.BracketedPasteMode
            ? $"\u001b[200~{text}\u001b[201~"
            : text;
        return _terminal.SendInputAsync(input, cancellationToken);
    }

    /// <summary>
    /// Captures the visible terminal contents, including the active alternate screen. Useful for
    /// diagnostics and assertions; it is not a complete scrollback dump.
    /// </summary>
    public string CaptureViewportText()
    {
        ThrowIfDisposed();
        return string.Join("\n", _terminal.Terminal.GetVisibleLines());
    }

    /// <summary>
    /// Releases the PTY connection after the hosted process has exited. The terminal keeps its
    /// buffer and can launch again; disposing the detached connection is what closes the
    /// out-of-band ConPTY host (OpenConsole.exe on Windows).
    /// </summary>
    public void ReleasePty()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            IPtyConnection? connection = _terminal.DetachConnection();
            connection?.Dispose();
        }
        catch (InvalidOperationException)
        {
            // No connection is attached (already released, never launched, or template missing).
        }
    }

    /// <summary>
    /// Kills the hosted process and releases the PTY. Must be called on the Avalonia UI thread.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _started = false;
        _terminal.OutputReceived -= OnTerminalOutputReceived;
        _terminal.ProcessExited -= OnTerminalProcessExited;
        _terminal.UrlClicked -= OnTerminalUrlClicked;

        try
        {
            // TerminalControl owns the PTY connection; disposing it is what releases the
            // out-of-band ConPTY host. Without this, OpenConsole.exe can outlive the shell.
            _terminal.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Idempotent disposal.
        }

        Content = null;
        _exitCompletion?.TrySetResult(_exitCode);
        Disposed?.Invoke(this, EventArgs.Empty);
        GC.SuppressFinalize(this);
    }

    private void ApplyInitialGrid(int cols, int rows)
    {
        if (cols <= 0 || rows <= 0)
        {
            return;
        }

        if (_terminal.Terminal.Cols == cols && _terminal.Terminal.Rows == rows)
        {
            return;
        }

        _terminal.Terminal.Resize(cols, rows);
    }

    private void OnTerminalOutputReceived(object? sender, OutputReceivedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        byte[] bytes = e.Bytes.ToArray();
        if (bytes.Length == 0)
        {
            return;
        }

        OutputReceived?.Invoke(this, new TerminalOutputEventArgs(bytes));

        string text = Decode(bytes);
        if (text.Length > 0)
        {
            TextOutputReceived?.Invoke(this, new TerminalTextOutputEventArgs(text));
        }
    }

    private void OnTerminalUrlClicked(object? sender, UrlClickedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        // The control arms a link on Ctrl+press and raises UrlClicked once when the release lands
        // on the same link, so forwarding each report as-is keeps one activation to one event.
        LinkActivated?.Invoke(this, new TerminalLinkActivatedEventArgs(e.Url, e.FromSequence));
    }

    private void OnTerminalProcessExited(object? sender, ProcessExitedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        int processId = _terminal.Pid;
        if (processId > 0)
        {
            _pid = processId;
        }

        _exitCode = e.ExitCode;
        _exitCodeKnown = e.ExitCodeKnown;
        _started = false;
        FlushDecoder();
        _exitCompletion?.TrySetResult(e.ExitCode);
        // TerminalControl keeps the PTY connection until DetachConnection or Dispose. Releasing it
        // here is what closes the out-of-band ConPTY host even when the shell exits on its own.
        ReleasePty();
        Exited?.Invoke(this, new TerminalExitedEventArgs(e.ExitCode, e.ExitCodeKnown, _pid));
    }

    private string Decode(ReadOnlySpan<byte> bytes)
    {
        lock (_decoderGate)
        {
            char[] chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
            int count = _utf8Decoder.GetChars(bytes, chars, false);
            return count == 0 ? string.Empty : new string(chars, 0, count);
        }
    }

    private void FlushDecoder()
    {
        string text;
        lock (_decoderGate)
        {
            char[] chars = new char[4];
            int count = _utf8Decoder.GetChars(ReadOnlySpan<byte>.Empty, chars, true);
            text = count == 0 ? string.Empty : new string(chars, 0, count);
        }

        if (text.Length > 0)
        {
            TextOutputReceived?.Invoke(this, new TerminalTextOutputEventArgs(text));
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
