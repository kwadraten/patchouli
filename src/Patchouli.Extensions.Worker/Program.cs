using System.Collections.Concurrent;
using System.Text.Json;
using System.IO.Pipes;
using System.Threading.Channels;
using Patchouli.Core.Bibliography.MetadataLookup;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;

namespace Patchouli.Extensions.Worker;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 2 || args[0] != "--pipe")
        {
            return 2;
        }

        using NamedPipeClientStream pipe = new(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
        using CancellationTokenSource connect = new(TimeSpan.FromSeconds(30));
        await pipe.ConnectAsync(connect.Token).ConfigureAwait(false);
        WorkerServer server = new();
        await using RpcPeer peer = new(pipe, server.DispatchAsync);
        server.Peer = peer;
        await Task.WhenAny(peer.Completion, server.Shutdown.Task).ConfigureAwait(false);
        return 0;
    }
}

internal sealed class WorkerServer
{
    internal RpcPeer Peer { get; set; } = null!;
    internal TaskCompletionSource Shutdown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IExtensionGeneration? _generation;
    private IExtensionDefinition? _definition;
    private InitializeRequest? _initialization;

    internal async Task<JsonElement> DispatchAsync(string method, JsonElement payload, CancellationToken token)
    {
        if (method == "initialize")
        {
            if (_generation is not null)
            {
                throw new InvalidOperationException("Worker generation is already initialized.");
            }

            InitializeRequest request = WireJson.Read<InitializeRequest>(payload);
            _initialization = request;
            ExtensionPackageSnapshot package = new(request.ExtensionId, request.Sources);
            if (package.Fingerprint != request.Fingerprint)
            {
                throw new InvalidDataException("Worker package fingerprint mismatch.");
            }

            _generation = await ((IExtensionGenerationLoader)new FsiExtensionGenerationLoader(request.RuntimeDirectory))
                .LoadAsync(package, token).ConfigureAwait(false);
            _definition = _generation is IExtensionGenerationDefinition typed
                ? typed.Definition
                : _generation.Translation;
            return WireJson.Pack(new InitializeResult(_definition.Descriptor, _definition.ConfigFields,
                _definition is IMetadataExtension metadata
                    ? MetadataSourceWire.From(metadata.SourceDefinition)
                    : null, _definition is IExtensionDerivedState));
        }

        if (method == "shutdown")
        {
            try
            {
                if (_generation is not null)
                {
                    await _generation.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                Shutdown.TrySetResult();
            }

            return WireJson.Pack(0);
        }

        InvocationRequest invocation = WireJson.Read<InvocationRequest>(payload);
        Validate(invocation.Identity);
        RemoteCapabilities capabilities = new(Peer, invocation.Identity, invocation.CapabilitiesFingerprint);
        switch (method)
        {
            case "translate":
                TranslationExtensionContext context = WireJson.Read<TranslationExtensionContext>(invocation.Context);
                RemoteProgress progress = new(Peer, invocation.Identity);
                context = context with { Progress = progress };
                try
                {
                    TranslationExtensionResult translated = await ((ITranslationExtension)_definition!)
                        .TranslateAsync(context, capabilities, token).ConfigureAwait(false);
                    await progress.FlushAsync().ConfigureAwait(false);
                    return WireJson.Pack(translated);
                }
                finally
                {
                    await progress.FlushAsync().ConfigureAwait(false);
                }
            case "metadata":
                Result<MetadataCandidate> result = await ((IMetadataExtension)_definition!).LookupAsync(
                        WireJson.Read<NormalizedIdentifier>(invocation.Context), invocation.Config, capabilities, token)
                    .ConfigureAwait(false);
                return WireJson.Pack(result.IsSuccess
                    ? new MetadataResultWire(result.Value, null, null)
                    : new MetadataResultWire(null, result.ErrorCode, result.ErrorMessage));
            case "ocr":
                return WireJson.Pack(await ((IOcrExtension)_definition!).RecognizeAsync(
                    WireJson.Read<OcrExtensionContext>(invocation.Context), capabilities, token).ConfigureAwait(false));
            case "retrieval":
                return WireJson.Pack(await ((IRetrievalExtension)_definition!).RankAsync(
                        WireJson.Read<RetrievalExtensionContext>(invocation.Context), capabilities, token)
                    .ConfigureAwait(false));
            case "clear":
                if (_definition is IExtensionDerivedState derived)
                {
                    await derived.ClearDerivedStateAsync(token).ConfigureAwait(false);
                }

                return WireJson.Pack(0);
            default: throw new InvalidDataException("Unknown extension operation.");
        }
    }

    private void Validate(InvocationIdentity identity)
    {
        if (_initialization is null || identity.WorkerInstanceId != _initialization.WorkerInstanceId ||
            identity.Generation != _initialization.Generation || string.IsNullOrWhiteSpace(identity.RunId) ||
            string.IsNullOrWhiteSpace(identity.InvocationId))
        {
            throw new InvalidDataException("Invalid worker invocation identity.");
        }
    }
}

internal sealed class RemoteProgress : IProgress<ExtensionProgress>
{
    private readonly Channel<ExtensionProgress> _events = Channel.CreateBounded<ExtensionProgress>(
        new BoundedChannelOptions(256)
            { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    private readonly Task _pump;

    internal RemoteProgress(RpcPeer peer, InvocationIdentity identity)
    {
        _pump = PumpAsync(peer, identity);
    }

    public void Report(ExtensionProgress value)
    {
        if (_events.Writer.TryWrite(value))
        {
            return;
        }

        if (value.Status is ExtensionProgressStatus.Running or ExtensionProgressStatus.Started)
        {
            return;
        }

        try
        {
            _events.Writer.WriteAsync(value).AsTask().GetAwaiter().GetResult();
        }
        catch (ChannelClosedException)
        {
            /* The completed invocation no longer accepts observer events. */
        }
    }

    internal Task FlushAsync()
    {
        _events.Writer.TryComplete();
        return _pump;
    }

    private async Task PumpAsync(RpcPeer peer, InvocationIdentity identity)
    {
        try
        {
            await foreach (ExtensionProgress value in _events.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await peer.CallAsync("progress", new ProgressCall(identity, value), CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ObjectDisposedException
                                          or OperationCanceledException)
        {
            System.Diagnostics.Trace.WriteLine("Worker progress observer disconnected.");
        }
        finally
        {
            _events.Writer.TryComplete();
        }
    }
}

internal sealed class RemoteCapabilities(RpcPeer peer, InvocationIdentity identity, string fingerprint)
    : IExtensionCapabilities
{
    public ILlmCapability Llm => new RemoteLlm(this, "");
    public IHttpCapability Http => new RemoteHttp(this, "");

    public IExtensionCapabilities ForRun(ExtensionConfigSnapshot value)
    {
        return this;
    }

    public ILlmCapability ResolveLlm(string? domain, ExtensionModelSelection? selection)
    {
        return Llm;
    }

    public IHttpCapability ResolveHttp(string? domain, ExtensionConfigSnapshot value)
    {
        return Http;
    }

    public ILlmCapability ResolveLlmSlot(string key, string? domain, ExtensionModelSelection? selection)
    {
        return new RemoteLlm(this, key);
    }

    public IHttpCapability ResolveHttpSlot(string key, string? domain, ExtensionConfigSnapshot value)
    {
        return new RemoteHttp(this, key);
    }

    public string ConfigurationFingerprint => fingerprint;

    public async Task WaitToProceedAsync(CancellationToken token = default)
    {
        await CallAsync<int>("", "wait", 0, token).ConfigureAwait(false);
    }

    private async Task<T> CallAsync<T>(string slot, string operation, object request, CancellationToken token)
    {
        JsonElement reply = await peer
            .CallAsync("capability", new CapabilityCall(identity, slot, operation, WireJson.Pack(request)), token)
            .ConfigureAwait(false);
        return WireJson.Read<T>(reply);
    }

    private sealed class RemoteLlm(RemoteCapabilities owner, string slot) : ILlmCapability
    {
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            return owner.CallAsync<LlmResponse>(slot, "llm", request, cancellationToken);
        }
    }

    private sealed class RemoteHttp(RemoteCapabilities owner, string slot) : IHttpCapability
    {
        public Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken cancellationToken = default)
        {
            return owner.CallAsync<HttpResponse>(slot, "http", request, cancellationToken);
        }
    }
}
