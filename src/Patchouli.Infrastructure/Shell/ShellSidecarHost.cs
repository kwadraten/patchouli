using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Results;

namespace Patchouli.Infrastructure.Shell;

public sealed class ShellSidecarHost : IAsyncDisposable
{
    private readonly ShellDomainService _domain;
    private readonly ShellResourceLimits _limits;
    private readonly string _sidecarPath;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _statusGate = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<ShellRpcEnvelope>> _pending = new();
    private readonly ChildProcessLifetime _childLifetime = new();
    private readonly object _processGate = new();
    private long _nextRequestId = -1;
    private Process? _process;
    private int? _processId;
    private Stream? _stdin;
    private CancellationTokenSource? _readerCts;
    private Task? _readerTask;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private string _status = ShellSandboxStatus.Stopped;
    private bool _exitHooksRegistered;
    private int _libraryGeneration;
    private int _acceptingCommands = 1;

    public ShellSidecarHost(ShellDomainService domain, string? sidecarPath = null,
        ShellResourceLimits? limits = null)
    {
        _domain = domain;
        _limits = limits ?? ShellResourceLimits.Default;
        _sidecarPath = sidecarPath ?? ResolveDefaultSidecarPath();
        EnsureExitHooks();
    }

    public string Status
    {
        get
        {
            lock (_statusGate)
            {
                return _status;
            }
        }
        private set
        {
            bool changed;
            lock (_statusGate)
            {
                changed = !string.Equals(_status, value, StringComparison.Ordinal);
                _status = value;
            }

            if (changed)
            {
                StatusChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public event EventHandler? StatusChanged;

    /// <summary>OS process id of the running sidecar, if any.</summary>
    public int? SidecarProcessId
    {
        get
        {
            lock (_processGate)
            {
                return _processId;
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (Status is ShellSandboxStatus.Ready or ShellSandboxStatus.Starting)
            {
                return;
            }

            await StartCoreAsync(cancellationToken);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            Interlocked.Exchange(ref _acceptingCommands, 0);
            await StopCoreAsync(cancellationToken);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task ForceRestartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            Interlocked.Exchange(ref _acceptingCommands, 0);
            await StopCoreAsync(cancellationToken);
            await StartCoreAsync(cancellationToken);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Library switch orchestration: reject new calls, cancel sessions/queue,
    /// destroy the old sidecar, and start a replacement before accepting commands.
    /// </summary>
    public async Task ReplaceForLibrarySwitchAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            await StopForLibrarySwitchCoreAsync(cancellationToken);
            await StartCoreAsync(cancellationToken);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopForLibrarySwitchAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            await StopForLibrarySwitchCoreAsync(cancellationToken);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task<Result<ShellExecuteResult>> ExecuteAsync(string sessionId, string command,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _acceptingCommands, 1, 1) != 1)
        {
            return LibraryChangedResult();
        }

        if (Status != ShellSandboxStatus.Ready)
        {
            return Result<ShellExecuteResult>.Failure(AppErrorCodes.InvalidState,
                $"Shell sandbox is not ready ({Status}).");
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Result<ShellExecuteResult>.Failure(AppErrorCodes.ValidationFailed, "session id is required.");
        }

        int generation = Volatile.Read(ref _libraryGeneration);
        long deadline = DateTimeOffset.UtcNow.Add(_limits.CommandTimeout).ToUnixTimeMilliseconds();
        Result<JsonElement> response;
        try
        {
            response = await CallAsync("shell.execute", new
            {
                session_id = sessionId,
                command,
                deadline_unix_ms = deadline
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            using CancellationTokenSource cancelCts = new(TimeSpan.FromSeconds(2));
            try
            {
                await CancelSessionAsync(sessionId, cancelCts.Token);
                await CloseSessionAsync(sessionId, cancelCts.Token);
            }
            catch (OperationCanceledException) when (cancelCts.IsCancellationRequested)
            {
            }

            throw;
        }

        if (Volatile.Read(ref _libraryGeneration) != generation)
        {
            return LibraryChangedResult();
        }

        if (response.IsFailure)
        {
            return Result<ShellExecuteResult>.Failure(response.ErrorCode!, response.ErrorMessage!);
        }

        JsonElement payload = response.Value;
        string text = payload.TryGetProperty("text", out JsonElement textElement) &&
                      textElement.ValueKind == JsonValueKind.String
            ? textElement.GetString() ?? ""
            : "";
        int exitCode = payload.TryGetProperty("exit_code", out JsonElement exitElement) &&
                       exitElement.TryGetInt32(out int code)
            ? code
            : 1;
        bool sessionReset = payload.TryGetProperty("session_reset", out JsonElement resetElement) &&
                            resetElement.ValueKind == JsonValueKind.True;
        return Result<ShellExecuteResult>.Success(new ShellExecuteResult(text, exitCode, sessionReset));
    }

    private static Result<ShellExecuteResult> LibraryChangedResult()
    {
        return Result<ShellExecuteResult>.Success(new ShellExecuteResult(
            "library changed; shell session terminated\n[exit 125]\n",
            125,
            true));
    }

    public async Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (Status != ShellSandboxStatus.Ready || string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        await CallAsync("session.close", new { session_id = sessionId }, cancellationToken);
    }

    public async Task CancelSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (Status != ShellSandboxStatus.Ready || string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        await CallAsync("cancel", new { session_id = sessionId }, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        RemoveExitHooks();
        _childLifetime.Dispose();
        _lifecycle.Dispose();
        _writeLock.Dispose();
    }

    /// <summary>Best-effort synchronous kill for process-exit hooks. Safe to call multiple times.</summary>
    public void ForceKill()
    {
        Interlocked.Exchange(ref _acceptingCommands, 0);
        FailAllPending("sidecar force-killed");

        Process? process;
        int? pid;
        lock (_processGate)
        {
            process = _process;
            pid = _processId;
            _process = null;
            _processId = null;
            _stdin = null;
        }

        if (process is not null)
        {
            try
            {
                process.Exited -= OnProcessExited;
            }
            catch (InvalidOperationException)
            {
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                    process.WaitForExit(3_000);
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                try
                {
                    process.Dispose();
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        if (pid is int remainingPid)
        {
            EnsureProcessTerminated(remainingPid);
        }

        if (Status is not (ShellSandboxStatus.Stopped or ShellSandboxStatus.Stopping))
        {
            Status = ShellSandboxStatus.Stopped;
        }
    }

    private static void EnsureProcessTerminated(int pid)
    {
        try
        {
            using Process existing = Process.GetProcessById(pid);
            if (existing.HasExited)
            {
                return;
            }

            existing.Kill(true);
            existing.WaitForExit(2_000);
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using Process? killer = Process.Start(new ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = $"/F /T /PID {pid}",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            killer?.WaitForExit(2_000);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        Status = ShellSandboxStatus.Starting;
        if (!File.Exists(_sidecarPath))
        {
            Status = ShellSandboxStatus.Faulted;
            throw new InvalidOperationException("patchouli-shell-sidecar was not found.");
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = _sidecarPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        startInfo.Environment["PATCHOULI_PARENT_PID"] = Environment.ProcessId.ToString();

        Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            Status = ShellSandboxStatus.Faulted;
            throw new InvalidOperationException("Failed to start patchouli-shell-sidecar.");
        }

        try
        {
            _childLifetime.Assign(process);
        }
        catch (Exception assignEx) when (UnexpectedExceptionReporter.ReportCatch(assignEx,
                                             "infrastructure.shell-sidecar-child-lifetime"))
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }

            process.Dispose();
            Status = ShellSandboxStatus.Faulted;
            throw new InvalidOperationException("Failed to assign sidecar process lifetime.", assignEx);
        }

        lock (_processGate)
        {
            _process = process;
            _processId = process.Id;
            _stdin = process.StandardInput.BaseStream;
        }

        process.ErrorDataReceived += static (_, _) => { };
        process.BeginErrorReadLine();
        process.Exited += OnProcessExited;

        _readerCts = new CancellationTokenSource();
        CancellationToken readerToken = _readerCts.Token;
        Channel<ShellRpcEnvelope> inbound = Channel.CreateUnbounded<ShellRpcEnvelope>();
        _readerTask = Task.Run(() => ReadLoopAsync(process.StandardOutput.BaseStream, inbound.Writer, readerToken),
            CancellationToken.None);

        try
        {
            using CancellationTokenSource handshakeCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshakeCts.CancelAfter(_limits.HandshakeTimeout);

            await WaitForHelloAndInitializeAsync(inbound.Reader, handshakeCts.Token);
            _ = Task.Run(() => DispatchInboundAsync(inbound.Reader, readerToken), CancellationToken.None);
            Interlocked.Exchange(ref _acceptingCommands, 1);
            Status = ShellSandboxStatus.Ready;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Status = ShellSandboxStatus.Faulted;
            await KillProcessAsync();
            throw new TimeoutException("Shell sidecar handshake timed out.");
        }
        catch (Exception) when (Status is not (ShellSandboxStatus.ProtocolIncompatible or ShellSandboxStatus.Faulted))
        {
            Status = ShellSandboxStatus.Faulted;
            await KillProcessAsync();
            throw;
        }
        catch (Exception)
        {
            await KillProcessAsync();
            throw;
        }
    }

    private async Task StopForLibrarySwitchCoreAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _acceptingCommands, 0);
        Interlocked.Increment(ref _libraryGeneration);
        FailAllPending("library changed; shell session terminated");
        if (Status == ShellSandboxStatus.Ready && _stdin is not null)
        {
            try
            {
                using CancellationTokenSource cancelCts =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cancelCts.CancelAfter(TimeSpan.FromSeconds(2));
                await CallAsync("shutdown", new { reason = "library_switch" }, cancelCts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex,
                                           "infrastructure.shell-sidecar-library-switch"))
            {
            }
        }

        await StopCoreAsync(cancellationToken);
    }

    private async Task WaitForHelloAndInitializeAsync(ChannelReader<ShellRpcEnvelope> reader,
        CancellationToken cancellationToken)
    {
        bool sawHello = false;
        bool sawReady = false;
        bool sawInitializeResponse = false;
        ulong initializeId = 0;

        while (!sawHello)
        {
            ShellRpcEnvelope envelope = await reader.ReadAsync(cancellationToken);
            if (!string.Equals(envelope.MessageType, "notification", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(envelope.Method, "hello", StringComparison.Ordinal))
            {
                continue;
            }

            string? helloVersion = null;
            if (envelope.Payload is { } payload &&
                payload.TryGetProperty("protocol_version", out JsonElement version) &&
                version.ValueKind == JsonValueKind.String)
            {
                helloVersion = version.GetString();
            }

            if (!string.Equals(helloVersion, ShellRpcProtocol.Version, StringComparison.Ordinal))
            {
                Status = ShellSandboxStatus.ProtocolIncompatible;
                throw new InvalidOperationException("Shell sidecar protocol is incompatible.");
            }

            sawHello = true;
        }

        initializeId = NextOddRequestId();
        TaskCompletionSource<ShellRpcEnvelope> initializeTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[initializeId] = initializeTcs;
        await WriteEnvelopeAsync(new ShellRpcEnvelope
        {
            ProtocolVersion = ShellRpcProtocol.Version,
            MessageType = "request",
            RequestId = initializeId,
            Method = "initialize",
            Payload = JsonSerializer.SerializeToElement(new
            {
                protocol_version = ShellRpcProtocol.Version,
                limits = new
                {
                    command_timeout_ms = (int)_limits.CommandTimeout.TotalMilliseconds,
                    max_terminal_output_bytes = _limits.MaxTerminalOutputBytes,
                    max_commands = _limits.MaxCommands,
                    max_loop_iterations = _limits.MaxLoopIterations,
                    max_function_depth = _limits.MaxFunctionDepth,
                    max_string_bytes = _limits.MaxStringBytes
                }
            }, ShellRpcFraming.JsonOptions)
        }, cancellationToken);

        while (!sawReady || !sawInitializeResponse)
        {
            ShellRpcEnvelope envelope = await reader.ReadAsync(cancellationToken);
            string messageType = envelope.MessageType ?? "";
            if (string.Equals(messageType, "response", StringComparison.OrdinalIgnoreCase) &&
                envelope.RequestId == initializeId)
            {
                _pending.TryRemove(initializeId, out _);
                if (envelope.Error is not null)
                {
                    if (string.Equals(envelope.Error.Code, "protocol_incompatible", StringComparison.Ordinal))
                    {
                        Status = ShellSandboxStatus.ProtocolIncompatible;
                    }

                    throw new InvalidOperationException(envelope.Error.Message);
                }

                sawInitializeResponse = true;
                initializeTcs.TrySetResult(envelope);
                continue;
            }

            if (string.Equals(messageType, "notification", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(envelope.Method, "ready", StringComparison.Ordinal))
            {
                sawReady = true;
                continue;
            }

            if (string.Equals(messageType, "request", StringComparison.OrdinalIgnoreCase))
            {
                _ = HandleReverseRequestAsync(envelope, cancellationToken);
            }
            else if (string.Equals(messageType, "response", StringComparison.OrdinalIgnoreCase) &&
                     envelope.RequestId is ulong otherId &&
                     _pending.TryRemove(otherId, out TaskCompletionSource<ShellRpcEnvelope>? tcs))
            {
                tcs.TrySetResult(envelope);
            }
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        if (_process is null && Status is ShellSandboxStatus.Stopped)
        {
            return;
        }

        Status = ShellSandboxStatus.Stopping;
        try
        {
            if (_process is { HasExited: false } && _stdin is not null)
            {
                try
                {
                    using CancellationTokenSource shutdownCts =
                        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    shutdownCts.CancelAfter(_limits.ShutdownGrace);
                    await CallAsync("shutdown", new { }, shutdownCts.Token);
                    await _process.WaitForExitAsync(shutdownCts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Fall through to kill.
                }
                catch (IOException)
                {
                }
                catch (InvalidOperationException)
                {
                }
                catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex,
                                               "infrastructure.shell-sidecar-shutdown"))
                {
                }
            }
        }
        finally
        {
            await KillProcessAsync();
            Status = ShellSandboxStatus.Stopped;
        }
    }

    private async Task KillProcessAsync()
    {
        FailAllPending("sidecar stopped");
        if (_readerCts is not null)
        {
            await _readerCts.CancelAsync();
        }

        Process? process;
        int? pid;
        lock (_processGate)
        {
            process = _process;
            pid = _processId;
            _process = null;
            _processId = null;
            _stdin = null;
        }

        if (process is not null)
        {
            process.Exited -= OnProcessExited;
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                    await process.WaitForExitAsync();
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }

            process.Dispose();
        }

        if (pid is int remainingPid)
        {
            EnsureProcessTerminated(remainingPid);
        }

        if (_readerTask is not null)
        {
            try
            {
                await _readerTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex,
                                           "infrastructure.shell-sidecar-reader-join"))
            {
            }

            _readerTask = null;
        }

        _readerCts?.Dispose();
        _readerCts = null;
    }

    private void EnsureExitHooks()
    {
        if (_exitHooksRegistered)
        {
            return;
        }

        AppDomain.CurrentDomain.ProcessExit += OnParentProcessExit;
        Console.CancelKeyPress += OnCancelKeyPress;
        _exitHooksRegistered = true;
    }

    private void RemoveExitHooks()
    {
        if (!_exitHooksRegistered)
        {
            return;
        }

        AppDomain.CurrentDomain.ProcessExit -= OnParentProcessExit;
        Console.CancelKeyPress -= OnCancelKeyPress;
        _exitHooksRegistered = false;
    }

    private void OnParentProcessExit(object? sender, EventArgs e)
    {
        ForceKill();
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        ForceKill();
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (Status is ShellSandboxStatus.Stopping or ShellSandboxStatus.Stopped
            or ShellSandboxStatus.ProtocolIncompatible)
        {
            return;
        }

        Status = ShellSandboxStatus.Faulted;
        FailAllPending("sidecar exited");
    }

    private async Task ReadLoopAsync(Stream stdout, ChannelWriter<ShellRpcEnvelope> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ShellRpcEnvelope? envelope =
                    await ShellRpcFraming.ReadFrameAsync(stdout, _limits.MaxRpcFrameBytes, cancellationToken);
                if (envelope is null)
                {
                    break;
                }

                await writer.WriteAsync(envelope, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
            if (Status is ShellSandboxStatus.Ready or ShellSandboxStatus.Starting)
            {
                Status = ShellSandboxStatus.Faulted;
            }

            FailAllPending("sidecar read failed");
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex,
                                       "infrastructure.shell-sidecar-read-loop"))
        {
            if (Status is ShellSandboxStatus.Ready or ShellSandboxStatus.Starting)
            {
                Status = ShellSandboxStatus.Faulted;
            }

            FailAllPending("sidecar read failed");
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private async Task DispatchInboundAsync(ChannelReader<ShellRpcEnvelope> reader, CancellationToken cancellationToken)
    {
        await foreach (ShellRpcEnvelope envelope in reader.ReadAllAsync(cancellationToken))
        {
            string messageType = envelope.MessageType ?? "";
            if (string.Equals(messageType, "response", StringComparison.OrdinalIgnoreCase))
            {
                if (envelope.RequestId is ulong id &&
                    _pending.TryRemove(id, out TaskCompletionSource<ShellRpcEnvelope>? tcs))
                {
                    tcs.TrySetResult(envelope);
                }

                continue;
            }

            if (string.Equals(messageType, "notification", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(messageType, "request", StringComparison.OrdinalIgnoreCase))
            {
                _ = HandleReverseRequestAsync(envelope, cancellationToken);
            }
        }
    }

    private async Task HandleReverseRequestAsync(ShellRpcEnvelope envelope, CancellationToken cancellationToken)
    {
        string method = envelope.Method ?? "";
        try
        {
            Result<JsonElement> result = await _domain.HandleAsync(method, envelope.Payload, cancellationToken);
            if (result.IsSuccess)
            {
                await WriteEnvelopeAsync(new ShellRpcEnvelope
                {
                    ProtocolVersion = ShellRpcProtocol.Version,
                    MessageType = "response",
                    RequestId = envelope.RequestId,
                    Payload = result.Value
                }, cancellationToken);
            }
            else
            {
                await WriteEnvelopeAsync(new ShellRpcEnvelope
                {
                    ProtocolVersion = ShellRpcProtocol.Version,
                    MessageType = "response",
                    RequestId = envelope.RequestId,
                    Error = new ShellRpcErrorDto(result.ErrorCode ?? "error", result.ErrorMessage ?? "error")
                }, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            await WriteEnvelopeAsync(new ShellRpcEnvelope
            {
                ProtocolVersion = ShellRpcProtocol.Version,
                MessageType = "response",
                RequestId = envelope.RequestId,
                Error = new ShellRpcErrorDto("cancelled", "request cancelled")
            }, CancellationToken.None);
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex,
                                       "infrastructure.shell-sidecar-reverse-rpc"))
        {
            await WriteEnvelopeAsync(new ShellRpcEnvelope
            {
                ProtocolVersion = ShellRpcProtocol.Version,
                MessageType = "response",
                RequestId = envelope.RequestId,
                Error = new ShellRpcErrorDto("internal_error", "domain handler failed")
            }, CancellationToken.None);
        }
    }

    private async Task<Result<JsonElement>> CallAsync(string method, object payload,
        CancellationToken cancellationToken)
    {
        if (_stdin is null || Status is not (ShellSandboxStatus.Ready or ShellSandboxStatus.Starting
                or ShellSandboxStatus.Stopping))
        {
            return Result<JsonElement>.Failure(AppErrorCodes.InvalidState, "Shell sandbox is not available.");
        }

        ulong requestId = NextOddRequestId();
        TaskCompletionSource<ShellRpcEnvelope> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = tcs;
        try
        {
            await WriteEnvelopeAsync(new ShellRpcEnvelope
            {
                ProtocolVersion = ShellRpcProtocol.Version,
                MessageType = "request",
                RequestId = requestId,
                Method = method,
                Payload = JsonSerializer.SerializeToElement(payload, ShellRpcFraming.JsonOptions)
            }, cancellationToken);

            ShellRpcEnvelope response = await tcs.Task.WaitAsync(cancellationToken);
            if (response.Error is not null)
            {
                return Result<JsonElement>.Failure(response.Error.Code, response.Error.Message);
            }

            return Result<JsonElement>.Success(response.Payload ?? default);
        }
        catch (OperationCanceledException)
        {
            _pending.TryRemove(requestId, out _);
            throw;
        }
        catch (IOException ex)
        {
            _pending.TryRemove(requestId, out _);
            Status = ShellSandboxStatus.Faulted;
            return Result<JsonElement>.Failure(AppErrorCodes.InvalidState, ex.Message);
        }
        catch (ObjectDisposedException ex)
        {
            _pending.TryRemove(requestId, out _);
            Status = ShellSandboxStatus.Faulted;
            return Result<JsonElement>.Failure(AppErrorCodes.InvalidState, ex.Message);
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex,
                                       "infrastructure.shell-sidecar-call"))
        {
            _pending.TryRemove(requestId, out _);
            Status = ShellSandboxStatus.Faulted;
            return Result<JsonElement>.Failure(AppErrorCodes.InvalidState, ex.Message);
        }
    }

    private async Task WriteEnvelopeAsync(ShellRpcEnvelope envelope, CancellationToken cancellationToken)
    {
        Stream stdin = _stdin ?? throw new InvalidOperationException("Shell sidecar stdin is not available.");
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await ShellRpcFraming.WriteFrameAsync(stdin, envelope, _limits.MaxRpcFrameBytes, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private ulong NextOddRequestId()
    {
        return (ulong)Interlocked.Add(ref _nextRequestId, 2);
    }

    private void FailAllPending(string message)
    {
        foreach (KeyValuePair<ulong, TaskCompletionSource<ShellRpcEnvelope>> pair in _pending)
        {
            if (_pending.TryRemove(pair.Key, out TaskCompletionSource<ShellRpcEnvelope>? tcs))
            {
                tcs.TrySetResult(new ShellRpcEnvelope
                {
                    ProtocolVersion = ShellRpcProtocol.Version,
                    MessageType = "response",
                    RequestId = pair.Key,
                    Error = new ShellRpcErrorDto("faulted", message)
                });
            }
        }
    }

    public static string ResolveDefaultSidecarPath()
    {
        string fileName = OperatingSystem.IsWindows() ? "patchouli-shell-sidecar.exe" : "patchouli-shell-sidecar";
        string?[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, fileName),
            Path.Combine(AppContext.BaseDirectory, "tools", "patchouli-shell-sidecar", fileName),
            FindFromRepositoryRoot(fileName)
        ];

        foreach (string? candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(AppContext.BaseDirectory, fileName);
    }

    private static string? FindFromRepositoryRoot(string fileName)
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            string release = Path.Combine(current.FullName, "tools", "patchouli-shell-sidecar", "target", "release",
                fileName);
            if (File.Exists(release))
            {
                return release;
            }

            string debug = Path.Combine(current.FullName, "tools", "patchouli-shell-sidecar", "target", "debug",
                fileName);
            if (File.Exists(debug))
            {
                return debug;
            }

            if (File.Exists(Path.Combine(current.FullName, "Patchouli.sln")))
            {
                return release;
            }

            current = current.Parent;
        }

        return null;
    }
}
