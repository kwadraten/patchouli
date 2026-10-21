using Patchouli.Extensions.Contracts;
using Patchouli.Core.Translation;
using Patchouli.Core.Diagnostics;

namespace Patchouli.Infrastructure.Translation;

/// <summary>Acquires one stable translation extension generation and its run-scoped capabilities.</summary>
public sealed class TranslationExtensionRunner
{
    private readonly IExtensionManager _manager;

    private readonly Func<IExtensionLease, ExtensionConfigSnapshot, TranslationExecutionGate?, IExtensionCapabilities>
        _capabilitiesFactory;

    public TranslationExtensionRunner(IExtensionManager manager, IExtensionCapabilities capabilities)
        : this(manager, (lease, config, gate) => WrapWithGate(capabilities.ForRun(config), gate))
    {
        ArgumentNullException.ThrowIfNull(capabilities);
    }

    public TranslationExtensionRunner(
        IExtensionManager manager,
        Func<ExtensionConfigSnapshot, TranslationExecutionGate?, IExtensionCapabilities> capabilitiesFactory)
        : this(manager, (lease, config, gate) => capabilitiesFactory(config, gate))
    {
        ArgumentNullException.ThrowIfNull(capabilitiesFactory);
    }

    public TranslationExtensionRunner(
        IExtensionManager manager,
        Func<IExtensionLease, ExtensionConfigSnapshot, TranslationExecutionGate?, IExtensionCapabilities>
            capabilitiesFactory)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _capabilitiesFactory = capabilitiesFactory ?? throw new ArgumentNullException(nameof(capabilitiesFactory));
    }

    public static IExtensionCapabilities WrapWithGate(
        IExtensionCapabilities capabilities,
        TranslationExecutionGate? executionGate)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return new GatedCapabilities(capabilities, executionGate);
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
            IExtensionCapabilities capabilities = _capabilitiesFactory(lease, resolvedConfig, executionGate) ??
                                                  throw new InvalidOperationException(
                                                      "The translation capability factory returned null.");
            return new TranslationExtensionRun(lease, resolvedConfig, capabilities);
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class GatedCapabilities : IExtensionCapabilities
    {
        private readonly IExtensionCapabilities _inner;
        private readonly TranslationExecutionGate? _gate;

        public GatedCapabilities(IExtensionCapabilities inner, TranslationExecutionGate? gate)
        {
            _inner = inner;
            _gate = gate;
        }

        public ILlmCapability Llm => new GatedLlmCapability(_inner.Llm, _gate);

        public IHttpCapability Http => new GatedHttpCapability(_inner.Http, _gate);

        public string ConfigurationFingerprint => _inner.ConfigurationFingerprint;

        public ILlmCapability ResolveLlm(string? defaultDomain, ExtensionModelSelection? selection)
        {
            return new GatedLlmCapability(_inner.ResolveLlm(defaultDomain, selection), _gate);
        }

        public IHttpCapability ResolveHttp(string? defaultDomain, ExtensionConfigSnapshot config)
        {
            return new GatedHttpCapability(_inner.ResolveHttp(defaultDomain, config), _gate);
        }

        public ILlmCapability ResolveLlmSlot(string key, string? defaultDomain, ExtensionModelSelection? selection)
        {
            return new GatedLlmCapability(_inner.ResolveLlmSlot(key, defaultDomain, selection), _gate);
        }

        public IHttpCapability ResolveHttpSlot(string key, string? defaultDomain, ExtensionConfigSnapshot config)
        {
            return new GatedHttpCapability(_inner.ResolveHttpSlot(key, defaultDomain, config), _gate);
        }

        public IExtensionCapabilities ForRun(ExtensionConfigSnapshot config)
        {
            return new GatedCapabilities(_inner.ForRun(config), _gate);
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

    private sealed class GatedHttpCapability(IHttpCapability inner, TranslationExecutionGate? gate)
        : IHttpCapability
    {
        public async Task<HttpResponse> SendAsync(HttpRequest request,
            CancellationToken cancellationToken = default)
        {
            if (gate is not null)
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return await inner.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>A single extension generation/configuration snapshot held for one translation run.</summary>
public sealed class TranslationExtensionRun : IAsyncDisposable
{
    private readonly IExtensionLease _lease;
    private readonly ExtensionConfigSnapshot _config;
    private readonly IExtensionCapabilities _capabilities;
    private int _disposed;

    internal TranslationExtensionRun(
        IExtensionLease lease,
        ExtensionConfigSnapshot config,
        IExtensionCapabilities capabilities)
    {
        _lease = lease;
        _config = config;
        _capabilities = capabilities;
    }

    public ExtensionDescriptor Descriptor => _lease.Descriptor;

    public ExtensionConfigSnapshot Config => _config;

    public string Fingerprint => _lease.Fingerprint;

    public async Task<TranslationExtensionResult> TranslateAsync(
        TranslationExtensionContext context,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(context);
        Dictionary<string, string> values = _config.Values.ToDictionary(
            static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        values["__host.extension-fingerprint"] = _lease.Fingerprint;
        values["__host.capabilities-fingerprint"] = _capabilities.ConfigurationFingerprint;
        TranslationExtensionContext scopedContext = context with
        {
            Config = new ExtensionConfigSnapshot(values, _config.Revision),
            Progress = ExtensionRunProgress.Isolate(context.Progress)
        };
        await _capabilities.WaitToProceedAsync(cancellationToken).ConfigureAwait(false);
        return await _lease.Translation.TranslateAsync(scopedContext, _capabilities, cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        return Interlocked.Exchange(ref _disposed, 1) == 0 ? _lease.DisposeAsync() : ValueTask.CompletedTask;
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
