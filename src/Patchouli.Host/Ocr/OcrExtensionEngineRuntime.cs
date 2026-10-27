using System.Collections.Concurrent;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Extensions;
using Patchouli.Host.PdfText;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Ocr;

namespace Patchouli.Host.Ocr;

/// <summary>
/// The product OCR engine runtime (issue #43): declarative engine metadata comes from the
/// compiled-package registries (native/vision support assemblies), and every non-MinerU engine
/// executes through a pinned extension generation — one <see cref="IOcrJob"/> invocation per
/// accepted task. Acquisition freezes the preset's extension selection, effective configuration
/// and independent text-layer provider from the frozen <see cref="OcrPresetVersion"/> envelope;
/// no host product switches remain.
/// </summary>
public sealed class OcrExtensionEngineRuntime : IOcrEngineRuntime
{
    /// <summary>Built-in engine id → package id; registration extends this at runtime.</summary>
    private readonly ConcurrentDictionary<string, string> _enginePackages =
        new(BuiltInOcrExtensions.Definitions.ToDictionary(pair => pair.Value.EngineId,
            pair => pair.Key, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, string> _providerPackages =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [PdfTextLayerProviderIds.NativeXyCut] = PdfTextLayerPackageIds.NativeXyCut,
            [PdfTextLayerProviderIds.NativeGapTree] = PdfTextLayerPackageIds.NativeGapTree,
            [PdfTextLayerProviderIds.VisualGuided] = PdfTextLayerPackageIds.VisualGuided
        };

    private readonly TranslationExtensionCatalog _catalog;
    private readonly PdfTextLayerInputSource _textLayerInputs;
    private readonly ExtensionManagedFileService? _managedFiles;
    private readonly ConcurrentDictionary<string, OcrEngineDefinition> _definitions;

    private readonly ConcurrentDictionary<string, PdfTextLayerDefinition>
        _providerDefinitions;

    private readonly Func<OcrJobPage, OcrJobInput, OcrPresetVersion, CancellationToken,
        Task<Result<IReadOnlyList<OcrExtensionBoxCandidate>>>>? _builtInRegionRunner;

    public OcrExtensionEngineRuntime(TranslationExtensionCatalog catalog,
        PdfTextLayerInputSource textLayerInputs,
        Func<OcrJobPage, OcrJobInput, OcrPresetVersion, CancellationToken,
            Task<Result<IReadOnlyList<OcrExtensionBoxCandidate>>>>? builtInRegionRunner = null,
        ExtensionManagedFileService? managedFiles = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(textLayerInputs);
        _catalog = catalog;
        _textLayerInputs = textLayerInputs;
        _managedFiles = managedFiles;
        _builtInRegionRunner = builtInRegionRunner;
        _providerDefinitions = new ConcurrentDictionary<string, PdfTextLayerDefinition>(BuiltInPdfTextLayerExtensions
                .Definitions.Values
                .ToDictionary(definition => definition.ProviderId, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        _definitions = new ConcurrentDictionary<string, OcrEngineDefinition>(BuiltInOcrExtensions.Definitions.Values
                .Concat(
                [
                    new OcrEngineDefinition(
                        OcrEngineIds.MinerU, "MinerU", OcrAdapterKind.CloudApi, OcrModelIds.MinerUDefault,
                        ["pdf_page", "page_image", "image_file", "region_image"],
                        true, SupportsVerticalText: true,
                        SupportsTableDetection: true,
                        Notes: "Cloud layout pipeline; built-in protocol, not an extension package."),
                    new OcrEngineDefinition(
                        OcrEngineIds.Mock, "Mock OCR", OcrAdapterKind.Mock, OcrModelIds.MockBasic,
                        ["page_image", "image_file", "region_image"],
                        Notes: "Deterministic development engine.", IsDevelopmentOnly: true),
                    new OcrEngineDefinition(
                        OcrEngineIds.LocalPlaceholder, "Local placeholder", "local_process",
                        "local-placeholder", ["page_image"],
                        Notes: "Built-in placeholder that reports OCR as unavailable.", IsDevelopmentOnly: true)
                ])
                .GroupBy(definition => definition.EngineId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<OcrEngineDefinition> ListDefinitions()
    {
        // Declarative metadata only — never activates a package or opens a model session.
        return EngineDefinitions().Values.Where(definition => !definition.IsDevelopmentOnly).ToArray();
    }

    public string? GetExtensionId(string engineId)
    {
        if (_enginePackages.TryGetValue(engineId, out string? registered))
        {
            return registered;
        }

        if (_definitions.ContainsKey(engineId))
        {
            return null; // Reserved built-ins are not replaced by a similarly named package.
        }

        string[] matches = _catalog.ActiveOcrDefinitions.Where(entry =>
                string.Equals(entry.Value.EngineId, engineId, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Key).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public string? GetTextLayerExtensionId(string providerId)
    {
        if (_providerPackages.TryGetValue(providerId, out string? registered))
        {
            return registered;
        }

        string[] matches = _catalog.ActiveTextLayerDefinitions.Where(entry =>
                string.Equals(entry.Value.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Key).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<PdfTextLayerDefinition> ListTextLayerDefinitions()
    {
        // Declarative metadata only — never activates a package or opens a session.
        Dictionary<string, PdfTextLayerDefinition> definitions = new(_providerDefinitions,
            StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, KeyValuePair<string, PdfTextLayerDefinition>> group in
                 _catalog.ActiveTextLayerDefinitions.GroupBy(entry => entry.Value.ProviderId,
                     StringComparer.OrdinalIgnoreCase))
        {
            string? packageId = GetTextLayerExtensionId(group.Key);
            if (packageId is not null && group.FirstOrDefault(entry => entry.Key == packageId).Value is { } active)
            {
                definitions[group.Key] = active;
            }
        }

        return definitions.Values.ToArray();
    }

    public Task<Result<OcrEngineDefinition>> GetDefinitionAsync(string engineId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            EngineDefinitions().TryGetValue(engineId, out OcrEngineDefinition? definition)
                ? Result<OcrEngineDefinition>.Success(definition)
                : Result<OcrEngineDefinition>.Failure(AppErrorCodes.NotFound,
                    $"OCR engine '{engineId}' has no registered definition."));
    }

    private Dictionary<string, OcrEngineDefinition> EngineDefinitions()
    {
        Dictionary<string, OcrEngineDefinition> definitions = new(_definitions, StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, OcrEngineDefinition> active = _catalog.ActiveOcrDefinitions;
        foreach ((string engineId, string packageId) in _enginePackages)
        {
            if (active.TryGetValue(packageId, out OcrEngineDefinition? actual))
            {
                if (string.Equals(engineId, actual.EngineId, StringComparison.OrdinalIgnoreCase))
                {
                    definitions[engineId] = actual;
                }
                else
                {
                    definitions.Remove(engineId);
                }
            }
        }

        foreach (IGrouping<string, KeyValuePair<string, OcrEngineDefinition>> group in
                 active.GroupBy(entry => entry.Value.EngineId, StringComparer.OrdinalIgnoreCase))
        {
            // Ambiguous third-party ids never silently replace another implementation.
            if (!definitions.ContainsKey(group.Key) && group.Count() == 1)
            {
                definitions[group.Key] = group.Single().Value;
            }
        }

        return definitions;
    }

    /// <summary>
    /// Pins the engine generation (and the independent text-layer provider generation when the
    /// frozen preset selects one). Fail-closed: a failed activation, missing package or malformed
    /// envelope rejects the acquisition — there is no adapter fallback.
    /// </summary>
    public async Task<Result<IOcrEngineLease>> AcquireAsync(OcrEngineLeaseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        OcrPresetVersion version = request.PresetVersion;

        Result<OcrEngineDefinition> definition =
            await GetDefinitionAsync(version.EngineId, cancellationToken);
        if (definition.IsFailure)
        {
            return Result<IOcrEngineLease>.Failure(definition.ErrorCode!, definition.ErrorMessage!);
        }

        Result<OcrPresetConfiguration> envelope = OcrPresetConfiguration.TryRead(version);
        if (envelope.IsFailure)
        {
            return Result<IOcrEngineLease>.Failure(envelope.ErrorCode!, envelope.ErrorMessage!);
        }

        if (!envelope.Value.IsValid)
        {
            return Result<IOcrEngineLease>.Failure(AppErrorCodes.ValidationFailed,
                envelope.Value.ParseError ?? "The preset configuration envelope is malformed.");
        }

        // Engine and independent text-layer provider selections are orthogonal: a built-in
        // engine (MinerU) still honors a selected provider — pure-native MinerU pages never
        // touch MinerU credentials, cloud calls or image resources.
        string? providerId = request.TextLayerProviderId ?? envelope.Value.TextLayerProviderId;
        bool providerSelected = providerId is { Length: > 0 } &&
                                !string.Equals(providerId, PdfTextLayerProviderIds.Disabled,
                                    StringComparison.OrdinalIgnoreCase);
        // Composition-level policy for provider-declared "no usable native text" pages; the
        // envelope default (page-ocr) is already the effective value.
        string missingTextPolicy = request.TextLayerMissingTextPolicy ??
                                   envelope.Value.TextLayerMissingTextPolicy;

        bool builtIn = string.Equals(version.EngineId, OcrEngineIds.MinerU,
                           StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(version.EngineId, OcrEngineIds.Mock, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(version.EngineId, OcrEngineIds.LocalPlaceholder,
                           StringComparison.OrdinalIgnoreCase);
        if (builtIn && !providerSelected)
        {
            return Result<IOcrEngineLease>.Success(new OcrEngineLease(definition.Value));
        }

        if (builtIn)
        {
            // Provider-only pin: no image engine activation, no MinerU session or credential —
            // a pure-native page never requires an image source.
            Result<ProviderPin> providerOnly =
                await AcquireProviderPinAsync(providerId!, version, request, cancellationToken);
            if (providerOnly.IsFailure)
            {
                return Result<IOcrEngineLease>.Failure(providerOnly.ErrorCode!,
                    providerOnly.ErrorMessage!);
            }

            ProviderPin pin = providerOnly.Value;
            return Result<IOcrEngineLease>.Success(new OcrEngineLease(definition.Value,
                providerId, pin.PackageId, pin.Fingerprint, pin.Definition?.ConfigurationVersion,
                pin.Config, pin.Resources, pin.Provider, pin.Lease, _textLayerInputs,
                true, _builtInRegionRunner, _managedFiles)
            {
                TextLayerMissingTextPolicy = missingTextPolicy
            });
        }

        string? extensionId = request.EngineExtensionId ?? envelope.Value.ExtensionId;
        if (extensionId is null)
        {
            extensionId = GetExtensionId(version.EngineId);
        }

        if (extensionId is null)
        {
            return Result<IOcrEngineLease>.Failure(AppErrorCodes.NotFound,
                $"OCR engine '{version.EngineId}' selects no extension package.");
        }

        Result<ExtensionConfigSnapshot> engineConfig = request.EngineConfig is not null
            ? Result<ExtensionConfigSnapshot>.Success(request.EngineConfig)
            : OcrPresetConfiguration.TryResolveImageConfig(version, definition.Value);
        if (engineConfig.IsFailure)
        {
            return Result<IOcrEngineLease>.Failure(engineConfig.ErrorCode!, engineConfig.ErrorMessage!);
        }

        Result<ExtensionDomainRun> engine = await AcquireEngineAsync(extensionId, engineConfig.Value,
            cancellationToken);
        if (engine.IsFailure)
        {
            return Result<IOcrEngineLease>.Failure(engine.ErrorCode!, engine.ErrorMessage!);
        }

        ProviderPin? provider = null;
        try
        {
            IOcrExtension? ocr = engine.Value.OcrExtension;
            if (ocr is null)
            {
                await engine.Value.DisposeAsync();
                return Result<IOcrEngineLease>.Failure(AppErrorCodes.UnsupportedOperation,
                    $"Extension '{extensionId}' is not an OCR engine.");
            }

            if (providerSelected)
            {
                Result<ProviderPin> pin = await AcquireProviderPinAsync(providerId!, version,
                    request, cancellationToken);
                if (pin.IsFailure)
                {
                    await engine.Value.DisposeAsync();
                    return Result<IOcrEngineLease>.Failure(pin.ErrorCode!, pin.ErrorMessage!);
                }

                provider = pin.Value;
            }

            OcrEngineDefinition effective = ocr.EngineDefinition.EngineId is "unknown"
                ? definition.Value
                : ocr.EngineDefinition;
            if (!string.Equals(effective.EngineId, version.EngineId, StringComparison.OrdinalIgnoreCase))
            {
                if (provider is not null)
                {
                    await provider.Lease.DisposeAsync().ConfigureAwait(false);
                }

                await engine.Value.DisposeAsync().ConfigureAwait(false);
                return Result<IOcrEngineLease>.Failure(AppErrorCodes.Conflict,
                    "The selected package exports a different OCR engine identity; reselect its preset component.");
            }

            OcrModelObservation models = await OcrModelObserver.ObserveAsync(effective,
                engine.Value.ConfigFields, engine.Value.Config, request.Scope, cancellationToken).ConfigureAwait(false);
            return Result<IOcrEngineLease>.Success(new OcrEngineLease(
                effective,
                engine.Value,
                engine.Value.Config,
                extensionId,
                version.ModelId,
                models, request.Scope,
                provider?.ProviderId ?? providerId,
                provider?.PackageId,
                provider?.Fingerprint,
                provider?.Definition?.ConfigurationVersion,
                provider?.Config,
                provider?.Resources,
                provider?.Provider,
                provider?.Lease,
                _textLayerInputs, _managedFiles)
            {
                TextLayerMissingTextPolicy = missingTextPolicy
            });
        }
        catch
        {
            try
            {
                if (provider is not null)
                {
                    await provider.Lease.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                await engine.Value.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>
    /// Registers a custom OCR engine package without a host switch: preset/UI callers bind the
    /// engine id to an installed extension package. Unknown engines with no registration and no
    /// matching definition stay unknown — they never fall back to an old adapter.
    /// </summary>
    public void RegisterEngine(string engineId, string packageId,
        OcrEngineDefinition? definition = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engineId);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        _enginePackages[engineId] = packageId;
        if (definition is not null)
        {
            _definitions[engineId] = definition;
        }
    }

    /// <summary>Registers a text-layer provider id → package binding for custom providers.</summary>
    public void RegisterTextProvider(string providerId, string packageId,
        PdfTextLayerDefinition? definition = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        _providerPackages[providerId] = packageId;
        if (definition is not null)
        {
            _providerDefinitions[providerId] = definition;
        }
    }

    /// <summary>
    /// Acquires the pinned text-layer provider generation: package id from the provider
    /// registry (explicit request id may override to a directly installed package), effective
    /// configuration from the frozen preset version, and run resources bound to the
    /// provider's own snapshot.
    /// </summary>
    private async Task<Result<ProviderPin>> AcquireProviderPinAsync(string providerId,
        OcrPresetVersion version, OcrEngineLeaseRequest request,
        CancellationToken cancellationToken)
    {
        string? packageId = request.TextLayerExtensionId;
        if (packageId is null)
        {
            packageId = GetTextLayerExtensionId(providerId);
        }

        if (packageId is null)
        {
            return Result<ProviderPin>.Failure(AppErrorCodes.NotFound,
                $"PDF text-layer provider '{providerId}' selects no extension package.");
        }

        Result<(IExtensionLease Lease, IPdfTextLayerExtension Provider)> acquired =
            await AcquireProviderAsync(packageId, cancellationToken);
        if (acquired.IsFailure)
        {
            return Result<ProviderPin>.Failure(acquired.ErrorCode!, acquired.ErrorMessage!);
        }

        IExtensionLease lease = acquired.Value.Lease;
        IPdfTextLayerExtension provider = acquired.Value.Provider;
        try
        {
            if (!string.Equals(providerId, provider.ProviderDefinition.ProviderId, StringComparison.OrdinalIgnoreCase))
            {
                await lease.DisposeAsync().ConfigureAwait(false);
                return Result<ProviderPin>.Failure(AppErrorCodes.Conflict,
                    "The selected package exports a different text-layer provider identity; reselect its preset component.");
            }

            Result<ExtensionConfigSnapshot> resolved = request.TextLayerConfig is not null
                ? Result<ExtensionConfigSnapshot>.Success(request.TextLayerConfig)
                : OcrPresetConfiguration.TryResolveTextLayerConfig(version,
                    provider.ProviderDefinition);
            if (resolved.IsFailure)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
                return Result<ProviderPin>.Failure(resolved.ErrorCode!, resolved.ErrorMessage!);
            }

            ExtensionConfigSnapshot effective = ResolveComponentConfig(packageId, lease.ConfigFields, resolved.Value);
            IExtensionResources resources;
            if (_catalog.Resources is HostExtensionResources host)
            {
                (resources, effective) = host.ForDomain("pdftext")
                    .ForRunWithModelDefaults(lease.ConfigFields, effective);
            }
            else
            {
                resources = _catalog.Resources.ForRun(effective);
            }

            return Result<ProviderPin>.Success(new ProviderPin(providerId, packageId, lease,
                provider, provider.ProviderDefinition, effective, resources,
                lease.Fingerprint));
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }
    }

    private async Task<Result<ExtensionDomainRun>> AcquireEngineAsync(string extensionId,
        ExtensionConfigSnapshot config, CancellationToken cancellationToken)
    {
        Result activate = await EnsureActiveAsync(extensionId, cancellationToken);
        if (activate.IsFailure)
        {
            return Result<ExtensionDomainRun>.Failure(activate.ErrorCode!, activate.ErrorMessage!);
        }

        IExtensionLease? lease =
            await _catalog.Manager.AcquireAsync(extensionId, cancellationToken).ConfigureAwait(false);
        if (lease?.Ocr is null)
        {
            if (lease is not null)
            {
                await lease.DisposeAsync();
            }

            return Result<ExtensionDomainRun>.Failure(AppErrorCodes.NotFound,
                $"The OCR extension '{extensionId}' is not active.");
        }

        try
        {
            config = ResolveComponentConfig(extensionId, lease.ConfigFields, config);
            IExtensionResources runResources;
            if (_catalog.Resources is HostExtensionResources host)
            {
                (runResources, config) = host.ForDomain("ocr")
                    .ForRunWithModelDefaults(lease.ConfigFields, config);
            }
            else
            {
                runResources = _catalog.Resources.ForRun(config);
            }

            return Result<ExtensionDomainRun>.Success(new ExtensionDomainRun(lease, runResources, config));
        }
        catch (ArgumentException exception)
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            return Result<ExtensionDomainRun>.Failure(AppErrorCodes.ValidationFailed, exception.Message);
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }
    }

    private ExtensionConfigSnapshot ResolveComponentConfig(string extensionId,
        IReadOnlyList<ConfigFieldDescriptor> fields, ExtensionConfigSnapshot config)
    {
        ExtensionConfigSnapshot paths = _managedFiles?.ResolveDefaults(extensionId, fields, config) ?? config;
        return ExtensionConfiguration.Resolve(fields, paths);
    }

    private async Task<Result<(IExtensionLease, IPdfTextLayerExtension)>> AcquireProviderAsync(
        string packageId, CancellationToken cancellationToken)
    {
        Result activate = await EnsureActiveAsync(packageId, cancellationToken);
        if (activate.IsFailure)
        {
            return Result<(IExtensionLease, IPdfTextLayerExtension)>.Failure(activate.ErrorCode!,
                activate.ErrorMessage!);
        }

        IExtensionLease? lease =
            await _catalog.Manager.AcquireAsync(packageId, cancellationToken).ConfigureAwait(false);
        if (lease?.PdfTextLayer is not { } provider)
        {
            if (lease is not null)
            {
                await lease.DisposeAsync();
            }

            return Result<(IExtensionLease, IPdfTextLayerExtension)>.Failure(AppErrorCodes.NotFound,
                $"The PDF text-layer extension '{packageId}' is not active.");
        }

        return Result<(IExtensionLease, IPdfTextLayerExtension)>.Success((lease, provider));
    }

    /// <summary>
    /// Ensures a package is active: bundled snapshots activate on demand; third-party ids must
    /// already be installed (the manager activates on acquire for active generations).
    /// </summary>
    private async Task<Result> EnsureActiveAsync(string packageId,
        CancellationToken cancellationToken)
    {
        await _catalog.EnsureExtensionAsync(packageId, cancellationToken).ConfigureAwait(false);
        ExtensionGenerationInfo? current = _catalog.Manager.List()
            .FirstOrDefault(info => string.Equals(info.Id, packageId, StringComparison.Ordinal));
        if (current is { IsActive: true })
        {
            return Result.Success();
        }

        ExtensionPackageSnapshot? package = _catalog.BundledPackageSnapshots
            .FirstOrDefault(item => string.Equals(item.Id, packageId, StringComparison.Ordinal));
        if (package is null)
        {
            return current is null
                ? Result.Failure(AppErrorCodes.NotFound,
                    $"The extension package '{packageId}' is not installed.")
                : Result.Success();
        }

        ExtensionCheckResult check = await _catalog.ActivateAsync(package, cancellationToken)
            .ConfigureAwait(false);
        return check.Succeeded
            ? Result.Success()
            : Result.Failure(AppErrorCodes.InvalidState,
                $"The extension package '{packageId}' failed its trusted-source check.");
    }

    private sealed record ProviderPin(
        string ProviderId,
        string PackageId,
        IExtensionLease Lease,
        IPdfTextLayerExtension Provider,
        PdfTextLayerDefinition? Definition,
        ExtensionConfigSnapshot Config,
        IExtensionResources Resources,
        string? Fingerprint);

    /// <summary>
    /// The pinned generation pair held for one accepted task. Engine invocations go through the
    /// retained <see cref="ExtensionDomainRun"/> — every public invocation enters tracked
    /// ownership so disposal drains real in-flight calls (including cancellation-ignoring
    /// provider parses and context-callback work) before the generation leases release.
    /// </summary>
    private sealed class OcrEngineLease : IOcrEngineLease
    {
        private readonly ExtensionDomainRun? _run;
        private readonly ExtensionConfigSnapshot? _engineConfig;
        private readonly IPdfTextLayerExtension? _provider;
        private readonly IExtensionLease? _providerLease;
        private readonly PdfTextLayerInputSource? _textLayerInputs;
        private readonly ExtensionManagedFileService? _managedFiles;
        private readonly OcrModelObservation _acceptedModels = OcrModelObservation.Empty;
        private OcrModelObservation? _executionModels;
        private readonly string? _acceptedScope;
        private string? _executionScope;

        private readonly Func<OcrJobPage, OcrJobInput, OcrPresetVersion, CancellationToken,
            Task<Result<IReadOnlyList<OcrExtensionBoxCandidate>>>>? _regionRunner;

        private readonly TaskCompletionSource _drain =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _disposeCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _active;
        private int _disposed;

        /// <summary>False when the lease is disposed or draining — work never starts after.</summary>
        private bool TryEnterInvocation()
        {
            while (true)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return false;
                }

                int active = Volatile.Read(ref _active);
                if (Interlocked.CompareExchange(ref _active, active + 1, active) == active)
                {
                    // Re-check: disposal may have raced between the read and the increment.
                    if (Volatile.Read(ref _disposed) != 0)
                    {
                        ExitInvocation();
                        return false;
                    }

                    return true;
                }
            }
        }

        private void ExitInvocation()
        {
            if (Interlocked.Decrement(ref _active) == 0 && Volatile.Read(ref _disposed) != 0)
            {
                _drain.TrySetResult();
            }
        }

        /// <summary>RAII scope: keeps the tracked call alive until the real Task completes.</summary>
        private readonly struct InvocationGuard(OcrEngineLease? lease) : IDisposable
        {
            public void Dispose()
            {
                lease?.ExitInvocation();
            }
        }

        internal OcrEngineLease(OcrEngineDefinition definition)
        {
            Definition = definition;
            EngineId = definition.EngineId;
            IsBuiltIn = true;
        }

        /// <summary>Built-in engine + pinned independent text-layer provider (no image run).</summary>
        internal OcrEngineLease(OcrEngineDefinition definition, string? textLayerProviderId,
            string? textLayerExtensionId, string? textLayerFingerprint,
            int? textLayerConfigurationVersion, ExtensionConfigSnapshot? textLayerConfig,
            IExtensionResources? textLayerResources, IPdfTextLayerExtension? provider,
            IExtensionLease? providerLease, PdfTextLayerInputSource textLayerInputs,
            bool isBuiltIn,
            Func<OcrJobPage, OcrJobInput, OcrPresetVersion, CancellationToken,
                Task<Result<IReadOnlyList<OcrExtensionBoxCandidate>>>>? builtInRegionRunner = null,
            ExtensionManagedFileService? managedFiles = null)
        {
            Definition = definition;
            EngineId = definition.EngineId;
            IsBuiltIn = isBuiltIn;
            TextLayerProviderId = textLayerProviderId;
            TextLayerExtensionId = textLayerExtensionId;
            TextLayerFingerprint = textLayerFingerprint;
            TextLayerConfigurationVersion = textLayerConfigurationVersion;
            TextLayerConfig = textLayerConfig;
            _textLayerResources = textLayerResources;
            _provider = provider;
            _providerLease = providerLease;
            _textLayerInputs = textLayerInputs;
            _regionRunner = builtInRegionRunner;
            _managedFiles = managedFiles;
        }

        internal OcrEngineLease(OcrEngineDefinition definition, ExtensionDomainRun run,
            ExtensionConfigSnapshot engineConfig, string? engineExtensionId, string? modelIdentity,
            OcrModelObservation models, string? scope, string? textLayerProviderId, string? textLayerExtensionId,
            string? textLayerFingerprint, int? textLayerConfigurationVersion,
            ExtensionConfigSnapshot? textLayerConfig, IExtensionResources? textLayerResources,
            IPdfTextLayerExtension? provider, IExtensionLease? providerLease,
            PdfTextLayerInputSource textLayerInputs, ExtensionManagedFileService? managedFiles = null)
        {
            Definition = definition;
            EngineId = definition.EngineId;
            _run = run;
            _engineConfig = engineConfig;
            EngineExtensionId = engineExtensionId;
            ModelIdentity = modelIdentity;
            _acceptedModels = models;
            _acceptedScope = scope;
            ModelDigest = models.IsComplete ? models.Digest : null;
            TextLayerProviderId = textLayerProviderId;
            TextLayerExtensionId = textLayerExtensionId;
            TextLayerFingerprint = textLayerFingerprint;
            TextLayerConfigurationVersion = textLayerConfigurationVersion;
            TextLayerConfig = textLayerConfig;
            _textLayerResources = textLayerResources;
            _provider = provider;
            _providerLease = providerLease;
            _textLayerInputs = textLayerInputs;
            _managedFiles = managedFiles;
        }

        public OcrEngineDefinition Definition { get; }
        public string EngineId { get; }

        public string? ProviderId
        {
            get
            {
                ConfigFieldDescriptor? model = _run?.ConfigFields.FirstOrDefault(descriptor =>
                    descriptor.Kind == ConfigValueKind.Model && descriptor.DefaultDomain == "ocr");
                if (model is null || _engineConfig is null ||
                    !_engineConfig.Values.TryGetValue(model.Key, out string? raw) || string.IsNullOrWhiteSpace(raw))
                {
                    return null;
                }

                ExtensionModelSelection? selection =
                    System.Text.Json.JsonSerializer.Deserialize<ExtensionModelSelection>(
                        raw, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return string.IsNullOrWhiteSpace(selection?.ProviderId) ? null : selection.ProviderId;
            }
        }

        public bool IsBuiltIn { get; }
        public string? EngineExtensionId { get; }
        public string? EngineFingerprint => _run?.Fingerprint;

        public int? EngineConfigurationVersion => _engineConfig is null
            ? null
            : (int)Math.Min(int.MaxValue, _engineConfig.Revision);

        public string? ModelIdentity { get; }
        public string? ModelDigest { get; private set; }
        public ExtensionConfigSnapshot? EngineConfig => _engineConfig;
        public string? TextLayerProviderId { get; }
        public string? TextLayerExtensionId { get; }
        public string? TextLayerFingerprint { get; }
        public int? TextLayerConfigurationVersion { get; }
        public ExtensionConfigSnapshot? TextLayerConfig { get; }

        /// <summary>The composition-level missing-text policy; see <see cref="IOcrEngineLease"/>.</summary>
        public string TextLayerMissingTextPolicy { get; init; } =
            TextLayerMissingTextPolicies.PageOcr;

        public Func<OcrJobPage, OcrJobInput, OcrPresetVersion, CancellationToken,
            Task<Result<IReadOnlyList<OcrExtensionBoxCandidate>>>>? BuiltInRegionRunner =>
            _regionRunner;

        public async Task<Result> EnsureManagedFilesAsync(string scope, CancellationToken cancellationToken = default)
        {
            Volatile.Write(ref _executionScope, scope);
            if (_managedFiles is null || _run is null || EngineConfig is null)
            {
                return Result.Success();
            }

            if (!TryEnterInvocation())
            {
                return Result.Failure(AppErrorCodes.InvalidState, "The pinned OCR generation was released.");
            }

            try
            {
                return await _managedFiles.EnsureAsync(_run.PackageId, _run.ConfigFields,
                    EngineConfig, scope, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ExitInvocation();
            }
        }

        public async Task<Result> CheckReadinessAsync(CancellationToken cancellationToken = default)
        {
            if (IsBuiltIn || _run is null || _engineConfig is null)
            {
                return Result.Failure(AppErrorCodes.UnsupportedOperation,
                    "Built-in engines do not probe through the extension lease.");
            }

            if (!TryEnterInvocation())
            {
                return Result.Failure(AppErrorCodes.InvalidState,
                    "The pinned OCR engine generation was released.");
            }

            try
            {
                Result ready = await _run.CheckOcrReadinessAsync(cancellationToken).ConfigureAwait(false);
                if (ready.IsFailure)
                {
                    return ready;
                }

                return await ValidateModelsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return Result.Failure(AppErrorCodes.InvalidState,
                    "The pinned OCR engine generation was released.");
            }
            finally
            {
                ExitInvocation();
            }
        }

        private async Task<Result> ValidateModelsAsync(CancellationToken cancellationToken)
        {
            OcrModelObservation current = await OcrModelObserver.ObserveAsync(Definition,
                _run!.ConfigFields, _engineConfig!, Volatile.Read(ref _executionScope) ?? _acceptedScope,
                cancellationToken).ConfigureAwait(false);
            bool managed = _run.ConfigFields.Any(field => field.ManagedFiles.Count > 0);
            if (managed && !current.IsComplete)
            {
                return Result.Failure("model_identity_unavailable",
                    "Required managed model files could not be verified for the selected input scope.");
            }

            OcrModelObservation? execution = Volatile.Read(ref _executionModels);
            if (managed && ((_acceptedModels.IsComplete && _acceptedModels.Digest is { } accepted &&
                             current.AssetSet == _acceptedModels.AssetSet && current.Digest != accepted) ||
                            (execution is { IsComplete: true, Digest: not null } &&
                             current.AssetSet == execution.AssetSet && current.Digest != execution.Digest)))
            {
                return Result.Failure("model_content_changed",
                    "Model files changed after this task was accepted or during recognition; start a new task to use the new files.");
            }

            Interlocked.CompareExchange(ref _executionModels, current, null);
            ModelDigest = current.IsComplete ? current.Digest : null;
            return Result.Success();
        }

        public async Task<Result<OcrDomainRunResult>> RunOcrJobAsync(OcrDomainJobRequest request,
            CancellationToken cancellationToken = default)
        {
            if (IsBuiltIn || _run is null)
            {
                return Result<OcrDomainRunResult>.Failure(AppErrorCodes.UnsupportedOperation,
                    "Built-in engines do not run through the extension job path.");
            }

            if (!TryEnterInvocation())
            {
                return Result<OcrDomainRunResult>.Failure(AppErrorCodes.InvalidState,
                    "The pinned OCR engine generation was released.");
            }

            try
            {
                Result before = await ValidateModelsAsync(cancellationToken).ConfigureAwait(false);
                if (before.IsFailure)
                {
                    return Result<OcrDomainRunResult>.Failure(before.ErrorCode!, before.ErrorMessage!);
                }

                Result<OcrDomainRunResult> result =
                    await _run.RunOcrAsync(request, cancellationToken).ConfigureAwait(false);
                Result after = await ValidateModelsAsync(cancellationToken).ConfigureAwait(false);
                return after.IsFailure
                    ? Result<OcrDomainRunResult>.Failure(after.ErrorCode!, after.ErrorMessage!)
                    : result;
            }
            catch (ObjectDisposedException)
            {
                return Result<OcrDomainRunResult>.Failure(AppErrorCodes.InvalidState,
                    "The pinned OCR engine generation was released.");
            }
            finally
            {
                ExitInvocation();
            }
        }

        public async Task<Result<PdfTextLayerResult>> ParseTextLayerAsync(
            DocumentInstanceId documentId, PageId pageId, int pageIndex,
            DocumentTreeRevisionId revisionId, NormalizedBBox? region,
            CancellationToken cancellationToken = default)
        {
            return await ParseTextLayerAsync(documentId, pageId, pageIndex, revisionId, region,
                region is null ? OcrRunExecutionScope.Pages : OcrRunExecutionScope.Region,
                cancellationToken).ConfigureAwait(false);
        }

        public async Task<Result<PdfTextLayerResult>> ParseTextLayerAsync(
            DocumentInstanceId documentId, PageId pageId, int pageIndex,
            DocumentTreeRevisionId revisionId, NormalizedBBox? region, string scope,
            CancellationToken cancellationToken = default)
        {
            if (_provider is null || _providerLease is null || TextLayerConfig is null ||
                _textLayerInputs is null)
            {
                return Result<PdfTextLayerResult>.Failure(AppErrorCodes.UnsupportedOperation,
                    "No PDF text-layer provider is pinned for this task.");
            }

            // Enter before ANY real work — context creation invokes render/glyph callbacks on the
            // pinned generation too, and disposal must drain the whole invocation.
            if (!TryEnterInvocation())
            {
                return Result<PdfTextLayerResult>.Failure(AppErrorCodes.InvalidState,
                    "The pinned PDF text-layer generation was released.");
            }

            try
            {
                return await ParseTextLayerCoreAsync(documentId, pageId, pageIndex, revisionId,
                    region, scope, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ExitInvocation();
            }
        }

        private async Task<Result<PdfTextLayerResult>> ParseTextLayerCoreAsync(
            DocumentInstanceId documentId, PageId pageId, int pageIndex,
            DocumentTreeRevisionId revisionId, NormalizedBBox? region,
            string scope, CancellationToken cancellationToken)
        {
            PdfTextLayerDefinition providerDefinition = _provider!.ProviderDefinition;
            if (_managedFiles is not null)
            {
                Result files = await _managedFiles.EnsureAsync(_providerLease!.Descriptor.Id,
                    _providerLease.ConfigFields, TextLayerConfig!,
                    scope,
                    cancellationToken).ConfigureAwait(false);
                if (files.IsFailure)
                {
                    return Result<PdfTextLayerResult>.Failure(files.ErrorCode!, files.ErrorMessage!);
                }
            }

            Result<PdfTextLayerContext> context = await _textLayerInputs!.CreateContextAsync(
                new PdfTextLayerContextRequest(documentId, pageId, revisionId, pageIndex,
                    TextLayerConfig!, region,
                    providerDefinition.RequiresPageImage,
                    providerDefinition.RequiresLayoutModel),
                cancellationToken).ConfigureAwait(false);
            if (context.IsFailure)
            {
                return Result<PdfTextLayerResult>.Failure(context.ErrorCode!, context.ErrorMessage!);
            }

            try
            {
                return await _provider.ParseAsync(context.Value,
                    _textLayerResources ?? throw new ObjectDisposedException(nameof(OcrEngineLease)),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return Result<PdfTextLayerResult>.Failure(AppErrorCodes.InvalidState,
                    "The pinned PDF text-layer generation was released.");
            }
        }

        private readonly IExtensionResources? _textLayerResources;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                // Drained calls and released generation resources are different milestones.
                // Every caller must observe completion (or failure) of the actual disposal.
                await _disposeCompletion.Task.ConfigureAwait(false);
                return;
            }

            try
            {
                if (Volatile.Read(ref _active) == 0)
                {
                    _drain.TrySetResult();
                }

                // Cancellation only stops demand; it does not retire native/provider work.
                await _drain.Task.ConfigureAwait(false);

                try
                {
                    if (_run is not null)
                    {
                        await _run.DisposeAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    if (_providerLease is not null)
                    {
                        await _providerLease.DisposeAsync().ConfigureAwait(false);
                    }
                }

                _disposeCompletion.TrySetResult();
            }
            catch (Exception exception)
            {
                _disposeCompletion.TrySetException(exception);
            }

            await _disposeCompletion.Task.ConfigureAwait(false);
        }
    }
}
