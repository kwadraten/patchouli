using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Patchouli.Core.Documents;

namespace Patchouli.Extensions.Worker;

internal static class WireJson
{
    internal static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        DefaultJsonTypeInfoResolver resolver = new();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type != typeof(DocumentBoxPayload))
            {
                return;
            }

            info.PolymorphismOptions = new JsonPolymorphismOptions { TypeDiscriminatorPropertyName = "$payload" };
            Type[] payloads =
            [
                typeof(TextBoxPayload), typeof(EquationBoxPayload), typeof(ListBoxPayload),
                typeof(TableBoxPayload), typeof(AlgorithmBoxPayload), typeof(CodeBoxPayload), typeof(MediaBoxPayload)
            ];
            foreach (Type payload in payloads)
            {
                info.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(payload, payload.Name));
            }
        });
        return new JsonSerializerOptions { PropertyNameCaseInsensitive = true, TypeInfoResolver = resolver };
    }

    internal static JsonElement Pack<T>(T value)
    {
        return JsonSerializer.SerializeToElement(value, Options);
    }

    internal static T Read<T>(JsonElement value)
    {
        return value.Deserialize<T>(Options) ?? throw new InvalidDataException("Missing RPC payload.");
    }
}

internal sealed record RpcFrame(
    string Id,
    string Method,
    JsonElement Payload,
    bool Reply = false,
    RpcError? Error = null,
    bool Cancelled = false);

internal sealed record RpcError(string Kind, string Message, int? StatusCode = null)
{
    internal static RpcError From(Exception error)
    {
        return error switch
        {
            HttpRequestException http => new RpcError("http", "The managed HTTP request failed.",
                http.StatusCode is { } status ? (int)status : null),
            TimeoutException => new RpcError("timeout", "The managed request timed out."),
            _ => new RpcError("extension", error.Message)
        };
    }

    internal Exception ToException()
    {
        return Kind switch
        {
            "http" => new HttpRequestException(Message, null,
                StatusCode is { } status ? (System.Net.HttpStatusCode)status : null),
            "timeout" => new TimeoutException(Message),
            _ => new InvalidOperationException(Message)
        };
    }
}

/// <summary>A bounded, duplex transport whose reader never waits for a callback dispatch.</summary>
internal sealed class RpcPeer : IAsyncDisposable
{
    private const int MaximumFrameBytes = 64 * 1024 * 1024;
    private readonly Stream _stream;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _incoming = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, Task> _dispatches = new();
    private readonly Func<string, JsonElement, CancellationToken, Task<JsonElement>> _dispatch;
    private readonly Task _reader;

    internal RpcPeer(Stream stream, Func<string, JsonElement, CancellationToken, Task<JsonElement>> dispatch)
    {
        _stream = stream;
        _dispatch = dispatch;
        _reader = ReadAsync();
    }

    internal Task Completion => _reader;

    internal async Task<JsonElement> CallAsync<T>(string method, T argument, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        string id = Guid.NewGuid().ToString("N");
        TaskCompletionSource<JsonElement> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException("Duplicate RPC identity.");
        }

        try
        {
            linked.Token.ThrowIfCancellationRequested();
            await WriteAsync(new RpcFrame(id, method, WireJson.Pack(argument)), linked.Token).ConfigureAwait(false);
            using CancellationTokenRegistration registration = token.Register(() => _ = SendCancellationAsync(id));
            return await completion.Task.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && _lifetime.IsCancellationRequested)
        {
            throw new IOException("Extension worker connection closed; the request was not retried.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task SendCancellationAsync(string id)
    {
        try
        {
            await WriteAsync(new RpcFrame(id, "$cancel", WireJson.Pack(0)), _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        {
            System.Diagnostics.Trace.WriteLine("Worker cancellation transport closed.");
        }
    }

    private async Task WriteAsync(RpcFrame frame, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(frame, WireJson.Options);
        if (bytes.Length > MaximumFrameBytes)
        {
            throw new InvalidDataException("Worker RPC frame exceeds the size limit.");
        }

        byte[] length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        await _write.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // Cancellation must not split a frame and corrupt concurrent requests.
            await _stream.WriteAsync(length, _lifetime.Token).ConfigureAwait(false);
            await _stream.WriteAsync(bytes, _lifetime.Token).ConfigureAwait(false);
            await _stream.FlushAsync(_lifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task ReadAsync()
    {
        Exception closed = new IOException("Extension worker connection closed; unfinished requests were not retried.");
        try
        {
            byte[] header = new byte[4];
            while (!_lifetime.IsCancellationRequested)
            {
                await _stream.ReadExactlyAsync(header, _lifetime.Token).ConfigureAwait(false);
                int length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length < 1 || length > MaximumFrameBytes)
                {
                    throw new InvalidDataException("Invalid worker frame size.");
                }

                byte[] data = new byte[length];
                await _stream.ReadExactlyAsync(data, _lifetime.Token).ConfigureAwait(false);
                RpcFrame frame = JsonSerializer.Deserialize<RpcFrame>(data, WireJson.Options) ??
                                 throw new InvalidDataException("Empty RPC frame.");
                if (string.IsNullOrWhiteSpace(frame.Id) || string.IsNullOrWhiteSpace(frame.Method) ||
                    frame.Payload.ValueKind == JsonValueKind.Undefined)
                {
                    throw new InvalidDataException("Worker RPC frame is missing required fields.");
                }

                if (frame.Reply)
                {
                    if (_pending.TryRemove(frame.Id, out TaskCompletionSource<JsonElement>? pending))
                    {
                        if (frame.Cancelled)
                        {
                            pending.TrySetCanceled();
                        }
                        else if (frame.Error is not null)
                        {
                            pending.TrySetException(frame.Error.ToException());
                        }
                        else
                        {
                            pending.TrySetResult(frame.Payload);
                        }
                    }
                }
                else if (frame.Method == "$cancel")
                {
                    if (_incoming.TryGetValue(frame.Id, out CancellationTokenSource? cancellation))
                    {
                        try
                        {
                            cancellation.Cancel();
                        }
                        catch (ObjectDisposedException)
                        {
                            /* Completion won the cancellation race. */
                        }
                        catch (AggregateException)
                        {
                            System.Diagnostics.Trace.WriteLine("An extension cancellation callback failed.");
                        }
                    }
                }
                else
                {
                    CancellationTokenSource cancellation =
                        CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    if (!_incoming.TryAdd(frame.Id, cancellation))
                    {
                        throw new InvalidDataException("Duplicate worker request identity.");
                    }

                    Task dispatch = Task.Run(() => DispatchAsync(frame, cancellation));
                    _dispatches[frame.Id] = dispatch;
                    _ = dispatch.ContinueWith(completed =>
                    {
                        _dispatches.TryRemove(frame.Id, out _);
                        if (completed.Exception is not null)
                        {
                            System.Diagnostics.Trace.WriteLine("Extension worker callback dispatch failed.");
                        }
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException
                                          or JsonException or InvalidDataException)
        {
            closed = new IOException("Extension worker transport failed; unfinished requests were not retried.", error);
        }
        finally
        {
            try
            {
                _lifetime.Cancel();
            }
            catch (AggregateException)
            {
                System.Diagnostics.Trace.WriteLine(
                    "An extension cancellation callback failed during transport shutdown.");
            }

            foreach (TaskCompletionSource<JsonElement> pending in _pending.Values)
            {
                pending.TrySetException(closed);
            }

            _pending.Clear();
        }
    }

    private async Task DispatchAsync(RpcFrame frame, CancellationTokenSource cancellation)
    {
        RpcFrame reply;
        try
        {
            reply = new RpcFrame(frame.Id, frame.Method,
                await _dispatch(frame.Method, frame.Payload, cancellation.Token).ConfigureAwait(false), true);
        }
        catch (OperationCanceledException)
        {
            reply = new RpcFrame(frame.Id, frame.Method, WireJson.Pack(0), true, Cancelled: true);
        }
        catch (Exception error)
        {
            reply = new RpcFrame(frame.Id, frame.Method, WireJson.Pack(0), true, RpcError.From(error));
        }

        try
        {
            try
            {
                await WriteAsync(reply, _lifetime.Token).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                await WriteAsync(new RpcFrame(frame.Id, frame.Method, WireJson.Pack(0), true,
                        new RpcError("protocol", "The extension worker result exceeds the RPC frame size limit.")),
                    _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        {
            System.Diagnostics.Trace.WriteLine("Worker reply transport closed.");
        }
        finally
        {
            _incoming.TryRemove(frame.Id, out _);
            cancellation.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (AggregateException)
        {
            System.Diagnostics.Trace.WriteLine("An extension cancellation callback failed during transport disposal.");
        }

        await _stream.DisposeAsync().ConfigureAwait(false);
        await _reader.ConfigureAwait(false);
        Task[] dispatches = _dispatches.Values.ToArray();
        try
        {
            await Task.WhenAll(dispatches).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            System.Diagnostics.Trace.WriteLine(
                "Extension worker callbacks did not settle within the shutdown deadline.");
        }
        // Dispatch owns each linked token source, including callbacks still settling after the deadline.
    }
}
