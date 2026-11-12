using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Patchouli.Core.Results;

namespace Patchouli.Host.Plugins;

public sealed record PluginClientOptions
{
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public int MaxLineBytes { get; init; } = 1024 * 1024;
    public int MaxStderrBytes { get; init; } = 16 * 1024;
}

public enum PluginClientState
{
    NotStarted,
    Ready,
    Failed,
    Disposed
}

/// <summary>
/// One supervised stdio MCP session to a plugin process: newline-delimited JSON-RPC 2.0 on
/// stdout (never Content-Length framing), stderr drained for diagnostics, bounded lines,
/// correlated concurrent request ids, per-request timeout/cancel with a cancelled
/// notification, and a single start attempt (a failed handshake never retries silently).
/// </summary>
public sealed class PluginStdioClient : IAsyncDisposable
{
    public static readonly string[] RequiredTools =
        ["patchouli.plugin.find", "patchouli.plugin.fetch", "patchouli.plugin.send"];

    private const string ProtocolVersion = "2025-06-18";

    private readonly PluginProcessSpec _spec;
    private readonly IPluginProcessLauncher _launcher;
    private readonly PluginClientOptions _options;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly StringBuilder _stderr = new();
    private readonly object _stderrGate = new();

    // Outstanding host → plugin calls made while answering a reverse host-verb call,
    // keyed by the reverse depth that caused them with a per-depth call count. A
    // plugin-initiated call inherits the deepest outstanding depth plus one, so a
    // plugin → host → plugin → host chain is bounded even though the depth cannot
    // cross the process boundary in an AsyncLocal (ADR 0058).
    private readonly object _reverseDepthGate = new();
    private readonly Dictionary<int, int> _outstandingReverseDepths = new();

    private IPluginChildProcess? _process;
    private Task? _readerTask;
    private Task? _stderrTask;
    private long _nextId;
    private bool _disposed;
    private string? _failure;

    public PluginStdioClient(PluginProcessSpec spec, IPluginProcessLauncher? launcher = null,
        PluginClientOptions? options = null)
    {
        _spec = spec;
        _launcher = launcher ?? new PluginProcessLauncher();
        _options = options ?? new PluginClientOptions();
    }

    /// <summary>
    /// The reverse host-verb dispatcher for this mount, attached by the mount owner
    /// before the process starts. Null means reverse calls are ignored, as before.
    /// </summary>
    internal PluginReverseHostDispatcher? ReverseHost { get; set; }

    /// <summary>Deepest reverse depth among outbound calls currently outstanding, or -1.</summary>
    internal int CurrentReverseDepth
    {
        get
        {
            lock (_reverseDepthGate)
            {
                return _outstandingReverseDepths.Count == 0 ? -1 : _outstandingReverseDepths.Keys.Max();
            }
        }
    }

    internal IDisposable RegisterOutboundReverseCall(int depth)
    {
        lock (_reverseDepthGate)
        {
            _outstandingReverseDepths[depth] = _outstandingReverseDepths.TryGetValue(depth, out int count)
                ? count + 1
                : 1;
        }

        return new ReverseDepthRegistration(this, depth);
    }

    private sealed class ReverseDepthRegistration(PluginStdioClient client, int depth) : IDisposable
    {
        public void Dispose()
        {
            lock (client._reverseDepthGate)
            {
                if (client._outstandingReverseDepths.TryGetValue(depth, out int count) && count > 1)
                {
                    client._outstandingReverseDepths[depth] = count - 1;
                }
                else
                {
                    client._outstandingReverseDepths.Remove(depth);
                }
            }
        }
    }

    public PluginClientState State { get; private set; } = PluginClientState.NotStarted;
    public string? Failure => _failure;

    public string StderrTail
    {
        get
        {
            lock (_stderrGate)
            {
                return _stderr.ToString();
            }
        }
    }

    private Result<T> Fail<T>(string code, string message)
    {
        string detail = StderrTail.Length > 0 ? $"{message} (stderr: {StderrTail.Trim()})" : message;
        return Result<T>.Failure(code, detail);
    }

    /// <summary>
    /// Starts the process and performs initialize + notifications/initialized + tools/list
    /// with the required-tool check. The result of the single start attempt is cached: a
    /// failed start never retries in the background.
    /// </summary>
    public async Task<Result> StartAsync(CancellationToken cancellationToken = default)
    {
        Task<Result> start;
        lock (_gate)
        {
            start = _startTask ??= StartCoreAsync();
        }

        return await start.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task<Result>? _startTask;

    private async Task<Result> StartCoreAsync()
    {
        try
        {
            _process = _launcher.Start(_spec);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException
                                              or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return MarkFailed($"Plugin process '{_spec.FileName}' could not be started: {exception.Message}");
        }

        _readerTask = Task.Run(ReaderLoopAsync);
        _stderrTask = Task.Run(StderrLoopAsync);
        try
        {
            JsonElement init = await RequestAsync("initialize", new
            {
                protocolVersion = ProtocolVersion,
                capabilities = new { },
                clientInfo = new { name = Core.BuildInfo.AppName, version = Core.BuildInfo.Version }
            }, _options.HandshakeTimeout, CancellationToken.None).ConfigureAwait(false);
            if (init.ValueKind != JsonValueKind.Object)
            {
                return MarkFailed("Plugin returned a malformed initialize result.");
            }

            await NotifyAsync("notifications/initialized", new { }).ConfigureAwait(false);
            JsonElement listed = await RequestAsync("tools/list", new { },
                _options.HandshakeTimeout, CancellationToken.None).ConfigureAwait(false);
            string[] missing = RequiredTools
                .Except(ListToolNames(listed), StringComparer.Ordinal)
                .ToArray();
            if (missing.Length > 0)
            {
                return MarkFailed(
                    $"Plugin does not expose the required tools: {string.Join(", ", missing)}.");
            }

            State = PluginClientState.Ready;
            return Result.Success();
        }
        catch (PluginCallException exception)
        {
            return MarkFailed($"Plugin handshake failed: {exception.Message}");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or JsonException)
        {
            return MarkFailed($"Plugin handshake failed: {exception.Message}");
        }
    }

    private static IEnumerable<string> ListToolNames(JsonElement toolsList)
    {
        if (toolsList.ValueKind == JsonValueKind.Object &&
            toolsList.TryGetProperty("tools", out JsonElement tools) &&
            tools.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement tool in tools.EnumerateArray())
            {
                if (tool.ValueKind == JsonValueKind.Object &&
                    tool.TryGetProperty("name", out JsonElement name) &&
                    name.ValueKind == JsonValueKind.String)
                {
                    yield return name.GetString()!;
                }
            }
        }
    }

    private Result MarkFailed(string message)
    {
        _failure = message;
        State = PluginClientState.Failed;
        FailAllPending("unavailable", message);
        return Result.Failure("unavailable", message);
    }

    /// <summary>Calls one plugin tool and returns its validated structuredContent.</summary>
    public async Task<Result<JsonElement>> CallToolAsync(string tool, object arguments,
        TimeSpan? timeout, CancellationToken cancellationToken)
    {
        if (State != PluginClientState.Ready)
        {
            return Result<JsonElement>.Failure("unavailable",
                _failure ?? "The plugin process is not running.");
        }

        JsonElement result;
        try
        {
            // Tag an outbound call made while answering a reverse host-verb call with
            // that call's depth, so the plugin's next reverse call inherits depth + 1.
            // Forward (agent-initiated) calls carry no depth and register nothing.
            int? reverseDepth = PluginReverseCallDepth.Current.Value;
            using IDisposable? registration = reverseDepth.HasValue
                ? RegisterOutboundReverseCall(reverseDepth.Value)
                : null;
            result = await RequestAsync("tools/call", new { name = tool, arguments },
                timeout ?? _options.RequestTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (PluginCallException exception)
        {
            return Result<JsonElement>.Failure(exception.Code, exception.Message);
        }

        return ParseToolResult(result);
    }

    private Result<JsonElement> ParseToolResult(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object)
        {
            return Fail<JsonElement>("unavailable", "Plugin returned a malformed tool result.");
        }

        bool isError = result.TryGetProperty("isError", out JsonElement flag) &&
                       flag.ValueKind == JsonValueKind.True;
        result.TryGetProperty("structuredContent", out JsonElement structured);
        if (isError)
        {
            string code = "internal";
            string message = "The plugin reported an error.";
            if (structured.ValueKind == JsonValueKind.Object &&
                structured.TryGetProperty("error", out JsonElement error) &&
                error.ValueKind == JsonValueKind.Object)
            {
                if (error.TryGetProperty("code", out JsonElement codeValue) &&
                    codeValue.ValueKind == JsonValueKind.String)
                {
                    code = NormalizeErrorCode(codeValue.GetString()!);
                }

                if (error.TryGetProperty("message", out JsonElement messageValue) &&
                    messageValue.ValueKind == JsonValueKind.String)
                {
                    message = messageValue.GetString()!;
                }
            }

            return Result<JsonElement>.Failure(code, message);
        }

        if (structured.ValueKind != JsonValueKind.Object)
        {
            return Fail<JsonElement>("unavailable",
                "Plugin tool result is missing the required structuredContent object.");
        }

        return Result<JsonElement>.Success(structured.Clone());
    }

    private static string NormalizeErrorCode(string code)
    {
        return code switch
        {
            "INVALID_ARGUMENT" => "invalid_argument",
            "NOT_FOUND" => "not_found",
            "UNAVAILABLE" => "unavailable",
            "REVISION_CONFLICT" => "revision_conflict",
            "UNSUPPORTED_OPERATION" => "unsupported_operation",
            "CANCELLED" => "cancelled",
            "PERMISSION_DENIED" => "permission_denied",
            "DEADLINE_EXCEEDED" => "deadline_exceeded",
            _ => "internal"
        };
    }

    private async Task<JsonElement> RequestAsync(string method, object parameters, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        long id = Interlocked.Increment(ref _nextId);
        TaskCompletionSource<JsonElement> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
        {
            throw new PluginCallException("unavailable", "Duplicate request id.");
        }

        try
        {
            await WriteMessageAsync(new
            {
                jsonrpc = "2.0", id, method,
                @params = parameters
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }

        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        try
        {
            return await completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _pending.TryRemove(id, out _);
            await TryCancelRemoteAsync(id).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                throw new PluginCallException("cancelled", "The plugin request was cancelled.");
            }

            throw new PluginCallException("deadline_exceeded",
                $"The plugin did not answer '{method}' within {timeout.TotalSeconds:0}s.");
        }
    }

    private async Task TryCancelRemoteAsync(long id)
    {
        try
        {
            await NotifyAsync("notifications/cancelled", new { requestId = id }).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort cancellation signal; the request is already abandoned locally.
        }
    }

    private Task NotifyAsync(string method, object parameters)
    {
        return WriteMessageAsync(new { jsonrpc = "2.0", method, @params = parameters },
            CancellationToken.None);
    }

    private async Task WriteMessageAsync(object message, CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is null || _process.HasExited)
            {
                throw new PluginCallException("unavailable", "The plugin process is not running.");
            }

            await _process.StandardInput.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException
                                              or ObjectDisposedException)
        {
            throw new PluginCallException("unavailable", $"Cannot write to the plugin process: {exception.Message}");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReaderLoopAsync()
    {
        BoundedLineReader reader = new(_options.MaxLineBytes);
        try
        {
            while (await reader.ReadLineAsync(_process!.StandardOutput, CancellationToken.None)
                       .ConfigureAwait(false) is { } line)
            {
                DispatchLine(line);
            }

            FailAllPending("unavailable",
                $"The plugin process exited{ExitDescription()} before answering all requests.");
        }
        catch (PluginProtocolException exception)
        {
            FailAllPending("unavailable", $"Plugin protocol violation: {exception.Message}");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException
                                              or ObjectDisposedException)
        {
            FailAllPending("unavailable", $"Lost the plugin process stream: {exception.Message}");
        }
    }

    private string ExitDescription()
    {
        try
        {
            return _process is { HasExited: true } ? $" (exit {_process.ExitCode})" : "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    private void DispatchLine(string line)
    {
        JsonElement root;
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new PluginProtocolException("A non-JSON line arrived on plugin stdout.");
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new PluginProtocolException("A non-object JSON-RPC message arrived on plugin stdout.");
        }

        if (root.TryGetProperty("id", out JsonElement idElement) &&
            idElement.ValueKind == JsonValueKind.Number && idElement.TryGetInt64(out long id) &&
            _pending.TryRemove(id, out TaskCompletionSource<JsonElement>? completion))
        {
            if (root.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.Object)
            {
                string message = error.TryGetProperty("message", out JsonElement m) &&
                                 m.ValueKind == JsonValueKind.String
                    ? m.GetString()!
                    : "The plugin rejected the request.";
                int rpcCode = error.TryGetProperty("code", out JsonElement c) &&
                              c.ValueKind == JsonValueKind.Number
                    ? c.GetInt32()
                    : -32603;
                completion.TrySetException(new PluginCallException("unavailable",
                    $"JSON-RPC error {rpcCode}: {message}"));
            }
            else if (root.TryGetProperty("result", out JsonElement result))
            {
                completion.TrySetResult(result);
            }
            else
            {
                completion.TrySetException(new PluginCallException("unavailable",
                    "The plugin response carried neither result nor error."));
            }

            return;
        }

        // A plugin-initiated request (method + an id no host request owns) is a reverse
        // host-verb call. The reader loop never awaits it: awaiting would block reading
        // the plugin's answers to outstanding host calls, which is exactly the
        // reentrancy the reverse dispatcher guards against. Notifications (no id)
        // still get no answer.
        if (ReverseHost is not null &&
            root.TryGetProperty("id", out JsonElement requestId) &&
            root.TryGetProperty("method", out JsonElement methodElement) &&
            methodElement.ValueKind == JsonValueKind.String)
        {
            _ = HandleReverseRequestAsync(requestId.Clone(), methodElement.GetString()!,
                root, CancellationToken.None);
        }
        // Anything else (unknown ids, notifications) is ignored.
    }

    private async Task HandleReverseRequestAsync(JsonElement id, string method, JsonElement root,
        CancellationToken cancellationToken)
    {
        try
        {
            PluginReverseHostDispatcher? dispatcher = ReverseHost;
            if (dispatcher is null)
            {
                return;
            }

            JsonElement parameters = root.TryGetProperty("params", out JsonElement paramsElement)
                ? paramsElement.Clone()
                : default;
            ReverseHostReply reply = await dispatcher.DispatchAsync(method, parameters, cancellationToken)
                .ConfigureAwait(false);
            if (!reply.Handled)
            {
                await WriteMessageAsync(new
                {
                    jsonrpc = "2.0",
                    id,
                    error = new { code = -32601, message = $"Method '{method}' is not supported by this host." }
                }, cancellationToken).ConfigureAwait(false);
                return;
            }

            await WriteMessageAsync(new { jsonrpc = "2.0", id, result = reply.Result },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A failed reverse reply must never break the reader loop or leak an
            // unobserved task failure; the plugin treats the missing answer as a timeout.
        }
    }

    private async Task StderrLoopAsync()
    {
        BoundedLineReader reader = new(_options.MaxLineBytes);
        try
        {
            while (await reader.ReadLineAsync(_process!.StandardError, CancellationToken.None)
                       .ConfigureAwait(false) is { } line)
            {
                lock (_stderrGate)
                {
                    if (_stderr.Length < _options.MaxStderrBytes)
                    {
                        _stderr.Append(line).Append('\n');
                    }
                }
            }
        }
        catch (Exception)
        {
            // stderr is diagnostic-only; a broken diagnostic stream never fails requests.
        }
    }

    private void FailAllPending(string code, string message)
    {
        foreach (KeyValuePair<long, TaskCompletionSource<JsonElement>> entry in _pending)
        {
            if (_pending.TryRemove(entry.Key, out TaskCompletionSource<JsonElement>? completion))
            {
                completion.TrySetException(new PluginCallException(code, message));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        State = PluginClientState.Disposed;
        IPluginChildProcess? process = _process;
        if (process is not null)
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (Exception)
            {
                // The pipe may already be broken; teardown continues regardless.
            }

            try
            {
                await process.WaitForExitAsync(
                    new CancellationTokenSource(_options.ShutdownTimeout).Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Timeouts and teardown races fall through to the process-tree kill.
            }

            process.KillTree();
        }

        FailAllPending("cancelled", "The plugin host was shut down.");
        if (_readerTask is not null)
        {
            try
            {
                await _readerTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Reader teardown must never block host shutdown.
            }
        }

        process?.Dispose();
        _writeGate.Dispose();
    }

    private sealed class PluginCallException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    private sealed class PluginProtocolException(string message) : Exception(message);

    /// <summary>Newline-delimited reader with a hard byte cap per protocol line.</summary>
    private sealed class BoundedLineReader(int maxLineBytes)
    {
        private readonly List<byte> _pending = new();
        private readonly byte[] _scratch = new byte[8192];

        public async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
        {
            while (true)
            {
                int newline = _pending.IndexOf((byte)'\n');
                if (newline >= 0)
                {
                    string line = Encoding.UTF8.GetString(_pending.GetRange(0, newline).ToArray())
                        .TrimEnd('\r');
                    _pending.RemoveRange(0, newline + 1);
                    return line;
                }

                int read = await stream.ReadAsync(_scratch, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    if (_pending.Count > 0)
                    {
                        string tail = Encoding.UTF8.GetString(_pending.ToArray()).TrimEnd('\r');
                        _pending.Clear();
                        return tail;
                    }

                    return null;
                }

                if (_pending.Count + read > maxLineBytes)
                {
                    throw new PluginProtocolException(
                        $"A protocol line exceeded the {maxLineBytes}-byte limit.");
                }

                _pending.AddRange(_scratch.AsSpan(0, read).ToArray());
            }
        }
    }
}
