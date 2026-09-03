using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using Patchouli.UI.Diagnostics;

namespace Patchouli.UI;

internal interface IDesktopInstanceCoordinator : IAsyncDisposable, IDisposable
{
    bool IsPrimary { get; }
    void StartListener();
    Task<bool> NotifyPrimaryAsync(CancellationToken cancellationToken = default);
    IDisposable Subscribe(Action callback);
    bool TryConsumePendingActivation();
}

internal sealed record DesktopInstanceCoordinatorOptions(
    string MutexName = DesktopInstanceCoordinator.DefaultMutexName,
    string PipeName = DesktopInstanceCoordinator.DefaultPipeName,
    TimeSpan SecondaryRetryTimeout = default,
    TimeSpan RequestTimeout = default,
    Action<string, Exception?>? LogDiagnostic = null,
    Func<string, NamedPipeServerStream>? ServerStreamFactory = null)
{
    public TimeSpan EffectiveSecondaryRetryTimeout =>
        SecondaryRetryTimeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(2) : SecondaryRetryTimeout;

    public TimeSpan EffectiveRequestTimeout =>
        RequestTimeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(2) : RequestTimeout;
}

internal sealed class DesktopInstanceCoordinator : IDesktopInstanceCoordinator
{
    public const string DefaultMutexName = "net.patchouli.app.ui.single-instance.v1";
    public const string DefaultPipeName = "net.patchouli.app.ui.control.v1";
    public const int MaxPayloadBytes = 1024;
    public const int ProtocolVersion = 1;
    public const string CommandActivateUi = "activate_ui";

    private readonly DesktopInstanceCoordinatorOptions _options;
    private readonly object _lock = new();
    private Mutex? _mutex;
    private readonly bool _isPrimary;
    private CancellationTokenSource? _listenerCts;
    private Task? _listenerTask;
    private Action? _activationCallback;
    private int _pendingActivation;
    private bool _disposed;
    private bool _listenerStarted;

    public bool IsPrimary => _isPrimary;

    public DesktopInstanceCoordinator(DesktopInstanceCoordinatorOptions? options = null)
    {
        _options = options ?? new DesktopInstanceCoordinatorOptions();

        NamedWaitHandleOptions mutexOptions = new()
        {
            CurrentUserOnly = true,
            CurrentSessionOnly = false
        };

        _mutex = new Mutex(false, _options.MutexName, mutexOptions, out bool createdNew);
        _isPrimary = createdNew;

        if (!_isPrimary)
        {
            _mutex.Dispose();
            _mutex = null;
        }
    }

    public void StartListener()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_isPrimary)
        {
            throw new InvalidOperationException("Only the primary UI instance can start the control listener.");
        }

        lock (_lock)
        {
            if (_listenerStarted)
            {
                return;
            }

            NamedPipeServerStream initialServerStream = CreateServerStream();
            if (initialServerStream is null)
            {
                throw new InvalidOperationException("Server stream factory returned null.");
            }

            CancellationTokenSource cts = new();
            try
            {
                _listenerCts = cts;
                _listenerTask = RunListenerLoopAsync(initialServerStream, cts.Token);
                _listenerStarted = true;
            }
            catch
            {
                cts.Dispose();
                initialServerStream.Dispose();
                _listenerCts = null;
                _listenerTask = null;
                _listenerStarted = false;
                throw;
            }
        }
    }

    private NamedPipeServerStream CreateServerStream()
    {
        if (_options.ServerStreamFactory != null)
        {
            return _options.ServerStreamFactory(_options.PipeName);
        }

        return new NamedPipeServerStream(
            _options.PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    public async Task<bool> NotifyPrimaryAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        TimeSpan retryTimeout = _options.EffectiveSecondaryRetryTimeout;

        while (stopwatch.Elapsed < retryTimeout && !cancellationToken.IsCancellationRequested)
        {
            TimeSpan remaining = retryTimeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            try
            {
                NamedPipeClientStream clientStream = new(
                    ".",
                    _options.PipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await using (clientStream.ConfigureAwait(false))
                {
                    int connectTimeoutMs = (int)Math.Min(remaining.TotalMilliseconds, 250);
                    if (connectTimeoutMs <= 0)
                    {
                        connectTimeoutMs = 1;
                    }

                    using CancellationTokenSource connectCts =
                        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    connectCts.CancelAfter(connectTimeoutMs);

                    try
                    {
                        await clientStream.ConnectAsync(connectCts.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
                    {
                        if (stopwatch.Elapsed >= retryTimeout || cancellationToken.IsCancellationRequested)
                        {
                            return false;
                        }

                        try
                        {
                            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            return false;
                        }

                        continue;
                    }

                    string requestId = Guid.NewGuid().ToString("D");
                    ControlRequest request = new()
                    {
                        Version = ProtocolVersion,
                        Command = CommandActivateUi,
                        RequestId = requestId
                    };

                    byte[] requestBytes = JsonSerializer.SerializeToUtf8Bytes(request,
                        ControlJsonSerializerContext.Default.ControlRequest);
                    if (requestBytes.Length > MaxPayloadBytes)
                    {
                        return false;
                    }

                    byte[] lengthHeader = new byte[4];
                    BinaryPrimitives.WriteInt32LittleEndian(lengthHeader, requestBytes.Length);

                    using CancellationTokenSource reqCts =
                        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    reqCts.CancelAfter(_options.EffectiveRequestTimeout);

                    await clientStream.WriteAsync(lengthHeader, reqCts.Token).ConfigureAwait(false);
                    await clientStream.WriteAsync(requestBytes, reqCts.Token).ConfigureAwait(false);
                    await clientStream.FlushAsync(reqCts.Token).ConfigureAwait(false);

                    byte[] respLengthHeader = new byte[4];
                    await clientStream.ReadExactlyAsync(respLengthHeader, 0, 4, reqCts.Token).ConfigureAwait(false);
                    int respLength = BinaryPrimitives.ReadInt32LittleEndian(respLengthHeader);
                    if (respLength <= 0 || respLength > MaxPayloadBytes)
                    {
                        return false;
                    }

                    byte[] respBytes = new byte[respLength];
                    await clientStream.ReadExactlyAsync(respBytes, 0, respLength, reqCts.Token).ConfigureAwait(false);

                    ControlResponse? response;
                    try
                    {
                        response = JsonSerializer.Deserialize(
                            respBytes, ControlJsonSerializerContext.Default.ControlResponse);
                    }
                    catch (Exception ex)
                    {
                        LogSanitizedDiagnostic("Control client received invalid response payload", ex);
                        return false;
                    }

                    if (response is { Version: ProtocolVersion, Ok: true } &&
                        string.Equals(response.RequestId, requestId, StringComparison.Ordinal))
                    {
                        return true;
                    }

                    return false;
                }
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (stopwatch.Elapsed >= retryTimeout || cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                try
                {
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }
        }

        return false;
    }

    public IDisposable Subscribe(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ObjectDisposedException.ThrowIf(_disposed, this);

        bool fireImmediately;
        lock (_lock)
        {
            _activationCallback = callback;
            fireImmediately = Interlocked.Exchange(ref _pendingActivation, 0) == 1;
        }

        if (fireImmediately)
        {
            try
            {
                callback();
            }
            catch (Exception ex)
            {
                LogSanitizedDiagnostic("Control activation callback threw on subscribe", ex);
            }
        }

        return new Subscription(() =>
        {
            lock (_lock)
            {
                if (_activationCallback == callback)
                {
                    _activationCallback = null;
                }
            }
        });
    }

    public bool TryConsumePendingActivation()
    {
        return Interlocked.Exchange(ref _pendingActivation, 0) == 1;
    }

    private async Task RunListenerLoopAsync(NamedPipeServerStream serverStream, CancellationToken cancellationToken)
    {
        await using (serverStream.ConfigureAwait(false))
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await serverStream.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    LogSanitizedDiagnostic("Control listener failed waiting for connection", ex);
                    break;
                }

                bool shouldTerminate = false;
                try
                {
                    await HandleConnectionAsync(serverStream, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogSanitizedDiagnostic("Control listener encountered an error handling connection", ex);
                }
                finally
                {
                    if (serverStream.IsConnected)
                    {
                        try
                        {
                            serverStream.Disconnect();
                        }
                        catch (Exception disconnectEx)
                        {
                            if (!cancellationToken.IsCancellationRequested)
                            {
                                LogSanitizedDiagnostic("Control listener failed disconnecting client connection",
                                    disconnectEx);
                            }

                            shouldTerminate = true;
                        }
                    }
                }

                if (shouldTerminate)
                {
                    break;
                }
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream stream, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeoutCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.EffectiveRequestTimeout);
        CancellationToken ct = timeoutCts.Token;

        byte[] lengthBuffer = new byte[4];
        try
        {
            await stream.ReadExactlyAsync(lengthBuffer, 0, 4, ct).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            return;
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
        if (length <= 0 || length > MaxPayloadBytes)
        {
            LogSanitizedDiagnostic(
                $"Control message rejected due to invalid length: {length} bytes (max: {MaxPayloadBytes})", null);
            return;
        }

        byte[] payloadBuffer = new byte[length];
        await stream.ReadExactlyAsync(payloadBuffer, 0, length, ct).ConfigureAwait(false);

        ControlRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(payloadBuffer, ControlJsonSerializerContext.Default.ControlRequest);
        }
        catch (Exception ex)
        {
            LogSanitizedDiagnostic("Control message rejected due to malformed JSON payload", ex);
            return;
        }

        if (request is null)
        {
            LogSanitizedDiagnostic("Control message rejected null request object", null);
            return;
        }

        if (request.Version != ProtocolVersion)
        {
            LogSanitizedDiagnostic($"Control message rejected unsupported version: {request.Version}", null);
            return;
        }

        if (!string.Equals(request.Command, CommandActivateUi, StringComparison.Ordinal))
        {
            LogSanitizedDiagnostic("Control message rejected unrecognized command", null);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.RequestId) ||
            !Guid.TryParseExact(request.RequestId, "D", out _))
        {
            LogSanitizedDiagnostic("Control message rejected invalid request_id", null);
            return;
        }

        TriggerActivation();

        ControlResponse response = new()
        {
            Version = ProtocolVersion,
            RequestId = request.RequestId,
            Ok = true
        };

        byte[] responseBytes =
            JsonSerializer.SerializeToUtf8Bytes(response, ControlJsonSerializerContext.Default.ControlResponse);
        byte[] respLengthBuffer = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(respLengthBuffer, responseBytes.Length);

        await stream.WriteAsync(respLengthBuffer, ct).ConfigureAwait(false);
        await stream.WriteAsync(responseBytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private void TriggerActivation()
    {
        Action? callback;
        lock (_lock)
        {
            if (_activationCallback != null)
            {
                callback = _activationCallback;
            }
            else
            {
                _pendingActivation = 1;
                callback = null;
            }
        }

        if (callback != null)
        {
            try
            {
                callback();
            }
            catch (Exception ex)
            {
                LogSanitizedDiagnostic("Control activation callback threw", ex);
            }
        }
    }

    private void LogSanitizedDiagnostic(string message, Exception? exception)
    {
        if (_options.LogDiagnostic != null)
        {
            _options.LogDiagnostic(message, exception);
            return;
        }

        UnexpectedExceptions.Sink.Report(
            exception ?? new InvalidOperationException(message),
            "desktop-instance-coordinator",
            "control-listener");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_listenerCts != null)
        {
            _listenerCts.Cancel();
            if (_listenerTask != null)
            {
                try
                {
                    await _listenerTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    LogSanitizedDiagnostic("Control listener task threw during shutdown", ex);
                }
            }

            _listenerCts.Dispose();
            _listenerCts = null;
        }

        if (_mutex != null)
        {
            _mutex.Dispose();
            _mutex = null;
        }

        lock (_lock)
        {
            _activationCallback = null;
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed class Subscription : IDisposable
    {
        private Action? _unsubscribe;

        public Subscription(Action unsubscribe)
        {
            _unsubscribe = unsubscribe;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
        }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ControlRequest
{
    [JsonPropertyName("version")] public int Version { get; set; }

    [JsonPropertyName("command")] public string? Command { get; set; }

    [JsonPropertyName("request_id")] public string? RequestId { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ControlResponse
{
    [JsonPropertyName("version")] public int Version { get; set; }

    [JsonPropertyName("request_id")] public string? RequestId { get; set; }

    [JsonPropertyName("ok")] public bool Ok { get; set; }
}

[JsonSerializable(typeof(ControlRequest))]
[JsonSerializable(typeof(ControlResponse))]
internal sealed partial class ControlJsonSerializerContext : JsonSerializerContext;
