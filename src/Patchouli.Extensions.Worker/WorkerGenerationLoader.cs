using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Patchouli.Core.Bibliography.MetadataLookup;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;

namespace Patchouli.Extensions.Worker;

public sealed record WorkerSnapshot(
    string WorkerInstanceId,
    string Generation,
    string ExtensionId,
    int ProcessId,
    bool HasExited);

/// <summary>Starts one reusable process per checked logical generation. This is process isolation, not a security sandbox.</summary>
public sealed class WorkerGenerationLoader(string runtimeDirectory)
    : IExtensionGenerationLoader, ICheckedExtensionGenerationLoader, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, WorkerGeneration> _generations = new();
    private readonly ConcurrentQueue<WorkerSnapshot> _exited = new();
    private readonly object _ownership = new();
    private bool _disposed;

    public IReadOnlyList<WorkerSnapshot> GetWorkers()
    {
        return _generations.Values.SelectMany(value => value.GetWorkers()).Concat(_exited).ToArray();
    }

    public async Task RestartAsync(string workerInstanceId, CancellationToken cancellationToken = default)
    {
        foreach (WorkerGeneration generation in _generations.Values)
        {
            if (await generation.RestartAsync(workerInstanceId, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }

        throw new InvalidOperationException("The extension worker instance is no longer active.");
    }

    public async Task<IExtensionGeneration> LoadAsync(ExtensionPackageSnapshot package,
        CancellationToken cancellationToken)
    {
        lock (_ownership)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        IExtensionSourceChecker checker = new ExtensionCompiler(Path.Combine(runtimeDirectory, ".worker-check"));
        ExtensionCheckResult check = await checker.CheckAsync(package, cancellationToken).ConfigureAwait(false);
        if (!check.Succeeded)
        {
            throw new InvalidOperationException(
                "The extension must pass static checking before worker initialization.");
        }

        return await LoadCheckedAsync(package, check, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IExtensionGeneration> LoadCheckedAsync(ExtensionPackageSnapshot package,
        ExtensionCheckResult check, CancellationToken cancellationToken)
    {
        if (!check.Succeeded)
        {
            throw new InvalidOperationException("The extension must pass host static checking before initialization.");
        }

        WorkerGeneration generation = new(package, runtimeDirectory, check.CapabilityRequests ?? [], Retire);
        lock (_ownership)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _generations[generation.Id] = generation;
        }

        try
        {
            await generation.InitializeAsync(cancellationToken).ConfigureAwait(false);
            lock (_ownership)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
            }

            return generation;
        }
        catch
        {
            await generation.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        WorkerGeneration[] owned;
        lock (_ownership)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            owned = _generations.Values.ToArray();
        }

        await Task.WhenAll(owned.Select(generation => generation.DisposeAsync().AsTask())).ConfigureAwait(false);
    }

    private void Retire(WorkerGeneration generation, WorkerSnapshot? snapshot, bool disposed)
    {
        if (snapshot is not null)
        {
            _exited.Enqueue(snapshot with { HasExited = true });
            while (_exited.Count > 128)
            {
                _exited.TryDequeue(out _);
            }
        }

        if (disposed)
        {
            _generations.TryRemove(generation.Id, out _);
        }
    }
}

internal sealed class WorkerGeneration : IExtensionGeneration, IExtensionGenerationDefinition
{
    internal string Id { get; } = Guid.NewGuid().ToString("N");
    private readonly ExtensionPackageSnapshot _package;
    private readonly string _root;
    private readonly IReadOnlyList<ExtensionCapabilityRequest> _requests;
    private readonly Action<WorkerGeneration, WorkerSnapshot?, bool> _retire;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly ConcurrentDictionary<string, HostInvocation> _invocations = new();
    private readonly ConditionalWeakTable<IExtensionCapabilities, RunIdentity> _runs = new();
    private WorkerProcess? _process;
    private InitializeResult? _description;
    private IExtensionDefinition? _proxy;
    private bool _disposed;

    internal WorkerGeneration(ExtensionPackageSnapshot package, string root,
        IReadOnlyList<ExtensionCapabilityRequest> requests,
        Action<WorkerGeneration, WorkerSnapshot?, bool> retire)
    {
        _package = package;
        _root = root;
        _requests = requests.ToArray();
        _retire = retire;
    }

    public IExtensionDefinition Definition =>
        _proxy ?? throw new InvalidOperationException("Worker initialization has not completed.");

    public ITranslationExtension Translation => Definition as ITranslationExtension ??
                                                throw new InvalidOperationException(
                                                    "This generation is not a translation extension.");

    internal IEnumerable<WorkerSnapshot> GetWorkers()
    {
        WorkerProcess? process = Volatile.Read(ref _process);
        return process is null ? [] : [process.Snapshot];
    }

    internal async Task InitializeAsync(CancellationToken token)
    {
        WorkerProcess process = await GetProcessAsync(token).ConfigureAwait(false);
        InitializeResult description = _description!;
        _proxy = description.Descriptor.Kind switch
        {
            ExtensionKind.Translation => new TranslationProxy(this),
            ExtensionKind.Metadata => description.HasDerivedState
                ? new MetadataDerivedProxy(this)
                : new MetadataProxy(this),
            ExtensionKind.Ocr => description.HasDerivedState ? new OcrDerivedProxy(this) : new OcrProxy(this),
            ExtensionKind.Retrieval => description.HasDerivedState
                ? new RetrievalDerivedProxy(this)
                : new RetrievalProxy(this),
            _ => throw new InvalidDataException("Unsupported extension domain.")
        };
    }

    private async Task<WorkerProcess> GetProcessAsync(CancellationToken token)
    {
        await _lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_process is { IsAlive: true })
            {
                return _process;
            }

            if (_process is not null)
            {
                WorkerSnapshot previous = _process.Snapshot;
                await _process.StopAsync(false, CancellationToken.None).ConfigureAwait(false);
                _retire(this, previous, false);
                _process = null;
            }

            WorkerProcess process = await WorkerProcess.StartAsync(_package.Id, Id, DispatchReverseAsync, token)
                .ConfigureAwait(false);
            using CancellationTokenSource startup = CancellationTokenSource.CreateLinkedTokenSource(token);
            startup.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                JsonElement reply = await process.Peer.CallAsync("initialize", new InitializeRequest(process.InstanceId,
                    Id, Path.Combine(_root, "extensions", ".workers", Id), _package.Id, _package.Sources,
                    _package.Fingerprint), startup.Token).ConfigureAwait(false);
                InitializeResult description = WireJson.Read<InitializeResult>(reply);
                if (_description is not null &&
                    WireJson.Pack(description).GetRawText() != WireJson.Pack(_description).GetRawText())
                {
                    throw new InvalidDataException("The replacement worker changed the extension descriptor.");
                }

                _description = description;
                _process = process;
                return process;
            }
            catch (Exception error)
            {
                await process.StopAsync(false, CancellationToken.None).ConfigureAwait(false);
                _retire(this, process.Snapshot, false);
                if (error is OperationCanceledException && startup.IsCancellationRequested &&
                    !token.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        "Extension worker initialization exceeded the 30-second startup deadline.", error);
                }

                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    internal async Task<bool> RestartAsync(string instance, CancellationToken token)
    {
        await _lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_process is null || _process.InstanceId != instance)
            {
                return false;
            }

            WorkerProcess old = _process;
            // Keep the handle visible until exit has been confirmed; callers cannot race a replacement start.
            await old.StopAsync(false, token).ConfigureAwait(false);
            _process = null;
            _retire(this, old.Snapshot, false);
            return true;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task<T> InvokeAsync<T>(string method, object context, ExtensionConfigSnapshot config,
        IExtensionCapabilities? capabilities, IProgress<ExtensionProgress>? progress, CancellationToken token)
    {
        WorkerProcess process = await GetProcessAsync(token).ConfigureAwait(false);
        string runId = capabilities is null
            ? Guid.NewGuid().ToString("N")
            : _runs.GetValue(capabilities, _ => new RunIdentity()).Id;
        InvocationIdentity identity = new(process.InstanceId, Id, runId, Guid.NewGuid().ToString("N"));
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        TranslationExtensionOptions? options = (context as TranslationExtensionContext)?.Options;
        ExtensionModelSelection? fallback = options is not null &&
                                            (!string.IsNullOrWhiteSpace(options.ProviderId) ||
                                             !string.IsNullOrWhiteSpace(options.Model))
            ? new ExtensionModelSelection(options.ProviderId ?? "", options.Model ?? "")
            : null;
        Dictionary<string, HostLlmSlot> llmSlots = new(StringComparer.Ordinal);
        Dictionary<string, IHttpCapability> httpSlots = new(StringComparer.Ordinal);
        if (capabilities is not null)
        {
            foreach (ExtensionCapabilityRequest request in _requests)
            {
                if (request.Kind == ExtensionCapabilityKind.Llm)
                {
                    ExtensionModelSelection? selection =
                        config.Values.TryGetValue(request.Key, out string? value) && !string.IsNullOrWhiteSpace(value)
                            ? JsonSerializer.Deserialize<ExtensionModelSelection>(value, WireJson.Options)
                            : null;
                    if (selection is not null && string.IsNullOrWhiteSpace(selection.ProviderId) &&
                        string.IsNullOrWhiteSpace(selection.Model))
                    {
                        selection = null;
                    }

                    if (selection is null && request.DefaultDomain == "translation")
                    {
                        selection = fallback;
                    }

                    llmSlots.Add(request.Key,
                        new HostLlmSlot(capabilities.ResolveLlmSlot(request.Key, request.DefaultDomain, selection),
                            selection));
                }
                else if (request.Kind == ExtensionCapabilityKind.Http)
                {
                    httpSlots.Add(request.Key,
                        capabilities.ResolveHttpSlot(request.Key, request.DefaultDomain, config));
                }
            }
        }

        HostInvocation invocation = new(identity, config, capabilities, progress, lifetime.Token, llmSlots, httpSlots);
        if (!_invocations.TryAdd(identity.InvocationId, invocation))
        {
            throw new InvalidOperationException("Duplicate invocation identity.");
        }

        try
        {
            JsonElement result = await process.Peer.CallAsync(method,
                new InvocationRequest(identity, WireJson.Pack(context), config,
                    capabilities?.ConfigurationFingerprint ?? ""), token).ConfigureAwait(false);
            return WireJson.Read<T>(result);
        }
        finally
        {
            _invocations.TryRemove(identity.InvocationId, out _);
            await lifetime.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task<JsonElement> DispatchReverseAsync(string method, JsonElement payload,
        CancellationToken transportToken)
    {
        InvocationIdentity identity = method switch
        {
            "capability" => WireJson.Read<CapabilityCall>(payload).Identity,
            "progress" => WireJson.Read<ProgressCall>(payload).Identity,
            _ => throw new InvalidDataException("Unknown worker callback.")
        };
        if (!_invocations.TryGetValue(identity.InvocationId, out HostInvocation? invocation) ||
            identity != invocation.Identity)
        {
            throw new InvalidDataException("Worker callback has no registered host invocation.");
        }

        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(transportToken, invocation.Token);
        CancellationToken token = linked.Token;
        token.ThrowIfCancellationRequested();
        if (method == "progress")
        {
            try
            {
                invocation.Progress?.Report(WireJson.Read<ProgressCall>(payload).Progress);
            }
            catch (Exception error)
            {
                Trace.WriteLine($"Extension progress observer failed: {error.GetType().Name}");
            }

            return WireJson.Pack(0);
        }

        CapabilityCall call = WireJson.Read<CapabilityCall>(payload);
        IExtensionCapabilities capabilities = invocation.Capabilities ??
                                              throw new InvalidOperationException(
                                                  "This operation has no capability scope.");
        await capabilities.WaitToProceedAsync(token).ConfigureAwait(false);
        if (call.Operation == "wait")
        {
            return WireJson.Pack(0);
        }

        ExtensionCapabilityKind kind = call.Operation switch
        {
            "llm" => ExtensionCapabilityKind.Llm, "http" => ExtensionCapabilityKind.Http,
            _ => throw new InvalidDataException("Unknown worker capability operation.")
        };
        ExtensionCapabilityRequest[] matching = _requests.Where(value => value.Kind == kind &&
                                                                         (value.Key == call.Slot ||
                                                                          (call.Slot.Length == 0 &&
                                                                           _requests.Count(value =>
                                                                               value.Kind == kind) == 1))).ToArray();
        if (matching.Length != 1)
        {
            throw new InvalidDataException("Worker capability slot was not statically declared or is ambiguous.");
        }

        ExtensionCapabilityRequest request = matching[0];
        if (kind == ExtensionCapabilityKind.Llm)
        {
            HostLlmSlot slot = invocation.LlmSlots[request.Key];
            LlmRequest input = WireJson.Read<LlmRequest>(call.Request);
            // Only the host's pre-resolved slot may select a model/provider. Worker request overrides are discarded.
            input = input with { ProviderId = slot.Selection?.ProviderId, Model = slot.Selection?.Model };
            return WireJson.Pack(await slot.Capability.CompleteAsync(input, token).ConfigureAwait(false));
        }

        IHttpCapability http = invocation.HttpSlots[request.Key];
        return WireJson.Pack(
            await http.SendAsync(WireJson.Read<HttpRequest>(call.Request), token).ConfigureAwait(false));
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            WorkerSnapshot? snapshot = _process?.Snapshot;
            if (_process is not null)
            {
                await _process.StopAsync(true, CancellationToken.None).ConfigureAwait(false);
            }

            _process = null;
            string workersRoot = Path.GetFullPath(Path.Combine(_root, "extensions", ".workers")) +
                                 Path.DirectorySeparatorChar;
            string sources = Path.GetFullPath(Path.Combine(workersRoot, Id));
            if (!sources.StartsWith(workersRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Worker source cleanup escaped its owned directory.");
            }

            try
            {
                // No extension process can retain file handles after StopAsync has confirmed exit.
                if (Directory.Exists(sources))
                {
                    Directory.Delete(sources, true);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Trace.WriteLine($"Extension worker source cleanup deferred: {error.GetType().Name}");
            }

            _retire(this, snapshot, true);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task ClearDerivedStateAsync(CancellationToken cancellationToken)
    {
        await InvokeAsync<int>("clear", 0, new ExtensionConfigSnapshot(new Dictionary<string, string>(), 0), null, null,
            cancellationToken).ConfigureAwait(false);
    }

    private sealed class RunIdentity
    {
        internal string Id { get; } = Guid.NewGuid().ToString("N");
    }

    private sealed record HostLlmSlot(ILlmCapability Capability, ExtensionModelSelection? Selection);

    private sealed record HostInvocation(
        InvocationIdentity Identity,
        ExtensionConfigSnapshot Config,
        IExtensionCapabilities? Capabilities,
        IProgress<ExtensionProgress>? Progress,
        CancellationToken Token,
        IReadOnlyDictionary<string, HostLlmSlot> LlmSlots,
        IReadOnlyDictionary<string, IHttpCapability> HttpSlots);

    private abstract class Proxy(WorkerGeneration owner) : IExtensionDefinition
    {
        protected WorkerGeneration Owner => owner;
        public ExtensionDescriptor Descriptor => owner._description!.Descriptor;
        public IReadOnlyList<ConfigFieldDescriptor> ConfigFields => owner._description!.ConfigFields;
    }

    private sealed class TranslationProxy(WorkerGeneration owner) : Proxy(owner), ITranslationExtension
    {
        public Task<TranslationExtensionResult> TranslateAsync(TranslationExtensionContext context,
            IExtensionCapabilities capabilities, CancellationToken cancellationToken = default)
        {
            return Owner.InvokeAsync<TranslationExtensionResult>("translate", context with { Progress = null },
                context.Config, capabilities, context.Progress, cancellationToken);
        }

        public async Task ClearDerivedStateAsync(CancellationToken cancellationToken = default)
        {
            await Owner.InvokeAsync<int>("clear", 0, new ExtensionConfigSnapshot(new Dictionary<string, string>(), 0),
                null, null, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class MetadataDerivedProxy(WorkerGeneration owner) : MetadataProxy(owner), IExtensionDerivedState
    {
        public Task ClearDerivedStateAsync(CancellationToken cancellationToken = default)
        {
            return Owner.ClearDerivedStateAsync(cancellationToken);
        }
    }

    private sealed class OcrDerivedProxy(WorkerGeneration owner) : OcrProxy(owner), IExtensionDerivedState
    {
        public Task ClearDerivedStateAsync(CancellationToken cancellationToken = default)
        {
            return Owner.ClearDerivedStateAsync(cancellationToken);
        }
    }

    private sealed class RetrievalDerivedProxy(WorkerGeneration owner) : RetrievalProxy(owner), IExtensionDerivedState
    {
        public Task ClearDerivedStateAsync(CancellationToken cancellationToken = default)
        {
            return Owner.ClearDerivedStateAsync(cancellationToken);
        }
    }

    private class MetadataProxy(WorkerGeneration owner) : Proxy(owner), IMetadataExtension
    {
        public MetadataSourceDefinition SourceDefinition => Owner._description!.MetadataSource!.ToDefinition();

        public async Task<Result<MetadataCandidate>> LookupAsync(NormalizedIdentifier identifier,
            ExtensionConfigSnapshot config, IExtensionCapabilities capabilities,
            CancellationToken cancellationToken = default)
        {
            MetadataResultWire result = await Owner
                .InvokeAsync<MetadataResultWire>("metadata", identifier, config, capabilities, null, cancellationToken)
                .ConfigureAwait(false);
            return result.Candidate is not null
                ? Result<MetadataCandidate>.Success(result.Candidate)
                : Result<MetadataCandidate>.Failure(result.ErrorCode!, result.ErrorMessage!);
        }
    }

    private class OcrProxy(WorkerGeneration owner) : Proxy(owner), IOcrExtension
    {
        public Task<OcrExtensionCandidate> RecognizeAsync(OcrExtensionContext context,
            IExtensionCapabilities capabilities, CancellationToken cancellationToken = default)
        {
            return Owner.InvokeAsync<OcrExtensionCandidate>("ocr", context, context.Config, capabilities, null,
                cancellationToken);
        }
    }

    private class RetrievalProxy(WorkerGeneration owner) : Proxy(owner), IRetrievalExtension
    {
        public Task<RetrievalExtensionResult> RankAsync(RetrievalExtensionContext context,
            IExtensionCapabilities capabilities, CancellationToken cancellationToken = default)
        {
            return Owner.InvokeAsync<RetrievalExtensionResult>("retrieval", context, context.Config, capabilities, null,
                cancellationToken);
        }
    }
}

internal sealed class WorkerProcess
{
    private readonly Process _process;
    private readonly WorkerJob _job;
    private readonly Task _stdout;
    private readonly Task _stderr;
    internal string InstanceId { get; }
    internal RpcPeer Peer { get; }
    private readonly string _extension;
    private readonly string _generation;
    private bool _stopped;
    private readonly object _state = new();
    private readonly int _pid;

    internal bool IsAlive
    {
        get
        {
            lock (_state)
            {
                return !_stopped && !_process.HasExited && !Peer.Completion.IsCompleted;
            }
        }
    }

    internal WorkerSnapshot Snapshot
    {
        get
        {
            lock (_state)
            {
                return new WorkerSnapshot(InstanceId, _generation, _extension, _pid, _stopped || _process.HasExited);
            }
        }
    }

    private WorkerProcess(Process process, WorkerJob job, RpcPeer peer, string instance, string extension,
        string generation)
    {
        _process = process;
        _pid = process.Id;
        _job = job;
        Peer = peer;
        InstanceId = instance;
        _extension = extension;
        _generation = generation;
        _stdout = DrainLogAsync(process.StandardOutput);
        _stderr = DrainLogAsync(process.StandardError);
    }

    private static async Task DrainLogAsync(StreamReader stream)
    {
        char[] buffer = new char[2048];
        while (await stream.ReadAsync(buffer).ConfigureAwait(false) > 0)
        {
            /* Output never enters protocol or body diagnostics. */
        }
    }

    internal static async Task<WorkerProcess> StartAsync(string extension, string generation,
        Func<string, JsonElement, CancellationToken, Task<JsonElement>> dispatch, CancellationToken token)
    {
        string instance = Guid.NewGuid().ToString("N");
        string pipeName = "patchouli-extension-" + instance;
        NamedPipeServerStream pipe = new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        string assembly = typeof(WorkerGenerationLoader).Assembly.Location;
        string directory = Path.GetDirectoryName(assembly)!;
        string appHostName = OperatingSystem.IsWindows()
            ? "Patchouli.Extensions.Worker.exe"
            : "Patchouli.Extensions.Worker";
        string bundle = Path.Combine(directory, "extension-worker");
        string appHost = Path.Combine(bundle, appHostName);
        if (File.Exists(appHost))
        {
            assembly = Path.Combine(bundle, "Patchouli.Extensions.Worker.dll");
        }
        else
        {
            appHost = Path.Combine(directory, appHostName);
        }

        bool useAppHost = File.Exists(appHost);
        if (!useAppHost && (File.Exists(Path.Combine(directory, "hostfxr.dll")) ||
                            File.Exists(Path.Combine(directory, "libhostfxr.so")) ||
                            File.Exists(Path.Combine(directory, "libhostfxr.dylib"))))
        {
            pipe.Dispose();
            throw new FileNotFoundException("The published extension worker bundle is missing.", appHost);
        }

        string runtime = Path.ChangeExtension(assembly, ".runtimeconfig.json");
        if (!File.Exists(runtime))
        {
            pipe.Dispose();
            throw new FileNotFoundException("Extension worker runtime configuration is missing.", runtime);
        }

        ProcessStartInfo start = new(useAppHost ? appHost : "dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (!useAppHost)
        {
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add("--runtimeconfig");
            start.ArgumentList.Add(runtime);
            start.ArgumentList.Add(assembly);
        }

        start.ArgumentList.Add("--pipe");
        start.ArgumentList.Add(pipeName);
        Process? process = null;
        WorkerJob? job = null;
        try
        {
            process = Process.Start(start) ?? throw new IOException("Failed to start extension worker.");
            job = new WorkerJob(process);
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            await pipe.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);
            return new WorkerProcess(process, job, new RpcPeer(pipe, dispatch), instance, extension, generation);
        }
        catch
        {
            pipe.Dispose();
            job?.Dispose();
            if (process is not null)
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }

                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                process.Dispose();
            }

            throw;
        }
    }

    internal async Task StopAsync(bool graceful, CancellationToken token)
    {
        if (_stopped)
        {
            return;
        }

        try
        {
            if (graceful && !_process.HasExited)
            {
                using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(3));
                try
                {
                    await Peer.CallAsync("shutdown", 0, deadline.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or InvalidOperationException
                                                  or OperationCanceledException)
                {
                    Trace.WriteLine("Extension worker shutdown deadline reached or connection closed.");
                }
            }
        }
        finally
        {
            _job.Dispose();
            if (!_process.HasExited)
            {
                _process.Kill(true);
            }

            // Caller cancellation cannot skip reaping an owned process.
            await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Peer.DisposeAsync().ConfigureAwait(false);
            await Task.WhenAll(_stdout, _stderr).ConfigureAwait(false);
            lock (_state)
            {
                _stopped = true;
                _process.Dispose();
            }
        }
    }
}
