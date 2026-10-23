using Patchouli.Extensions.Contracts;
using Patchouli.Core.Translation;
using Patchouli.Core.Diagnostics;

namespace Patchouli.Infrastructure.Translation;

/// <summary>Acquires one stable translation extension generation and its run-scoped capabilities.</summary>
public sealed class TranslationExtensionRunner
{
    private readonly IExtensionManager _manager;

    private readonly Func<IExtensionLease, ExtensionConfigSnapshot, TranslationExecutionGate?, IExtensionResources>
        _resourcesFactory;

    public TranslationExtensionRunner(IExtensionManager manager, IExtensionResources resources)
        : this(manager, (lease, config, gate) => WrapWithGate(resources.ForRun(config), gate))
    {
        ArgumentNullException.ThrowIfNull(resources);
    }

    public TranslationExtensionRunner(
        IExtensionManager manager,
        Func<ExtensionConfigSnapshot, TranslationExecutionGate?, IExtensionResources> resourcesFactory)
        : this(manager, (lease, config, gate) => resourcesFactory(config, gate))
    {
        ArgumentNullException.ThrowIfNull(resourcesFactory);
    }

    public TranslationExtensionRunner(
        IExtensionManager manager,
        Func<IExtensionLease, ExtensionConfigSnapshot, TranslationExecutionGate?, IExtensionResources>
            resourcesFactory)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _resourcesFactory = resourcesFactory ?? throw new ArgumentNullException(nameof(resourcesFactory));
    }

    public static IExtensionResources WrapWithGate(
        IExtensionResources resources,
        TranslationExecutionGate? executionGate)
    {
        ArgumentNullException.ThrowIfNull(resources);
        return new GatedResources(resources, executionGate);
    }

    public async ValueTask<TranslationExtensionRun?> AcquireAsync(
        string extensionId,
        TranslationExecutionGate? executionGate = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);
        IExtensionLease? lease = await _manager.AcquireAsync(extensionId.Trim(), cancellationToken)
            .ConfigureAwait(false);
        if (lease is null)
        {
            return null;
        }

        try
        {
            if (lease.Descriptor.Kind != ExtensionKind.Translation || lease.Translation is null)
            {
                throw new InvalidOperationException(
                    $"Extension '{lease.Descriptor.Id}' is not an active translation extension.");
            }

            ExtensionConfigSnapshot resolvedConfig = ExtensionConfiguration.Resolve(
                lease.ConfigFields, lease.Config);
            IExtensionResources resources = _resourcesFactory(lease, resolvedConfig, executionGate) ??
                                            throw new InvalidOperationException(
                                                "The translation capability factory returned null.");
            return new TranslationExtensionRun(lease, resolvedConfig, resources);
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class GatedResources : IExtensionResources
    {
        private readonly IExtensionResources _inner;
        private readonly TranslationExecutionGate? _gate;

        public GatedResources(IExtensionResources inner, TranslationExecutionGate? gate)
        {
            _inner = inner;
            _gate = gate;
        }

        public ILlmCapability Llm => new GatedLlmCapability(_inner.Llm, _gate);
        public HttpClient Http => _inner.Http;

        public string ConfigurationFingerprint => _inner.ConfigurationFingerprint;

        public ILlmCapability ResolveLlm(string? defaultDomain, ExtensionModelSelection? selection)
        {
            return new GatedLlmCapability(_inner.ResolveLlm(defaultDomain, selection), _gate);
        }

        public IExtensionResources ForRun(ExtensionConfigSnapshot config)
        {
            return new GatedResources(_inner.ForRun(config), _gate);
        }

        public Task<string?> GetCredentialAsync(string reference, CancellationToken cancellationToken = default)
        {
            return _inner.GetCredentialAsync(reference, cancellationToken);
        }

        public Task WaitToProceedAsync(CancellationToken cancellationToken = default)
        {
            return _gate?.WaitAsync(cancellationToken) ?? Task.CompletedTask;
        }
    }

    private sealed class GatedLlmCapability(ILlmCapability inner, TranslationExecutionGate? gate)
        : ILlmCapability
    {
        public async Task<LlmResponse> CompleteAsync(LlmRequest request,
            CancellationToken cancellationToken = default)
        {
            if (gate is not null)
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return await inner.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>A single extension generation/configuration snapshot held for one translation run.</summary>
public sealed class TranslationExtensionRun : IAsyncDisposable
{
    private readonly IExtensionLease _lease;
    private readonly ExtensionConfigSnapshot _config;
    private readonly IExtensionResources _resources;
    private int _disposed;

    internal TranslationExtensionRun(
        IExtensionLease lease,
        ExtensionConfigSnapshot config,
        IExtensionResources resources)
    {
        _lease = lease;
        _config = config;
        _resources = resources;
    }

    public ExtensionDescriptor Descriptor => _lease.Descriptor;

    public ExtensionConfigSnapshot Config => _config;

    public string Fingerprint => _lease.Fingerprint;

    /// <summary>
    /// Runs the extension once for the whole task. The extension sees the run-pinned configuration plus the
    /// host fingerprints that identify this generation and capability scope, and a progress sink that an
    /// extension observer cannot break the run with.
    /// </summary>
    /// <param name="job">The host job that serves pages, context and commits for this run.</param>
    /// <param name="cancellationToken">Cancels the whole run.</param>
    public async Task RunAsync(ITranslationJob job, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(job);
        Dictionary<string, string> values = _config.Values.ToDictionary(
            static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        values["__host.extension-fingerprint"] = _lease.Fingerprint;
        values["__host.resources-fingerprint"] = _resources.ConfigurationFingerprint;
        ITranslationJob scopedJob = new ScopedJob(job,
            job.Info with { Config = new ExtensionConfigSnapshot(values, _config.Revision) },
            ExtensionRunProgress.Isolate(job.Progress));
        await _resources.WaitToProceedAsync(cancellationToken).ConfigureAwait(false);
        await _lease.Translation.RunAsync(scopedJob, _resources, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        return Interlocked.Exchange(ref _disposed, 1) == 0 ? _lease.DisposeAsync() : ValueTask.CompletedTask;
    }

    /// <summary>
    /// The one job identity handed to the extension: run-pinned info and isolated progress, with every page
    /// operation forwarded unchanged to the host job that owns validation and commits.
    /// </summary>
    private sealed class ScopedJob(
        ITranslationJob inner,
        TranslationJobInfo info,
        IProgress<ExtensionProgress>? progress) : ITranslationJob
    {
        public TranslationJobInfo Info { get; } = info;

        public IProgress<ExtensionProgress>? Progress { get; } = progress;

        public ValueTask<TranslationPageInput?> NextPageAsync(CancellationToken cancellationToken = default)
        {
            return inner.NextPageAsync(cancellationToken);
        }

        public ValueTask<TranslationContextSnapshot> ReadContextAsync(TranslationPageInput page, int neighborRadius,
            CancellationToken cancellationToken = default)
        {
            return inner.ReadContextAsync(page, neighborRadius, cancellationToken);
        }

        public ValueTask<TranslationContextTranslationSnapshot?> GetCommittedPageTranslationAsync(int pageIndex,
            CancellationToken cancellationToken = default)
        {
            return inner.GetCommittedPageTranslationAsync(pageIndex, cancellationToken);
        }

        public ValueTask<TranslationPageCommit> CommitPageAsync(TranslationPageResult result,
            CancellationToken cancellationToken = default)
        {
            return inner.CommitPageAsync(result, cancellationToken);
        }
    }
}

internal static class ExtensionRunProgress
{
    public static IProgress<T>? Isolate<T>(IProgress<T>? observer)
    {
        return observer is null ? null : new IsolatedProgress<T>(observer);
    }

    public static void Report<T>(IProgress<T>? observer, T value)
    {
        try
        {
            observer?.Report(value);
        }
        catch (Exception exception) when (exception is OperationCanceledException ||
                                          UnexpectedExceptionReporter.ReportCatch(exception,
                                              "translation-progress", "observer"))
        {
            // Observation is outside the execution/commit boundary and must not fail a run.
            return;
        }
    }

    private sealed class IsolatedProgress<T>(IProgress<T> observer) : IProgress<T>
    {
        public void Report(T value)
        {
            ExtensionRunProgress.Report(observer, value);
        }
    }
}
