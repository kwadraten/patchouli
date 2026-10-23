using System.Collections.Concurrent;
using System.Diagnostics;
using System.Collections.ObjectModel;
using Patchouli.Extensions;
using Patchouli.Extensions.Contracts;
using Patchouli.Infrastructure.Translation;
using Patchouli.Llm;
using Patchouli.Core.Ids;
using Patchouli.Core.Translation;
using System.Threading.Channels;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Bibliography.MetadataLookup;
using Patchouli.Infrastructure.Bibliography.MetadataLookup;

namespace Patchouli.Host.Extensions;

/// <summary>Shared desktop/headless extension composition; never loads directly from an editor draft.</summary>
public sealed class TranslationExtensionCatalog : IAsyncDisposable
{
    private readonly SemaphoreSlim _initialization = new(1, 1);
    private readonly SemaphoreSlim _baselineGate = new(1, 1);
    private bool _baselineInitialized;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _engineGates =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _root;
    private readonly Func<LlmAppSettings> _settings;
    private bool _initialized;
    private int _disposed;
    private readonly CancellationTokenSource _watchLifetime = new();

    private readonly Channel<string> _sourceChanges = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly Dictionary<string, string> _lastAttempt = new(StringComparer.Ordinal);
    private readonly object _watchGate = new();
    private FileSystemWatcher? _watcher;
    private Task _watchWork = Task.CompletedTask;
    private IReadOnlyList<IMetadataSource> _metadataSources;

    private static readonly IReadOnlyList<ExtensionPackageSnapshot> BundledPackages = Array.AsReadOnly(
        BuiltInTranslationExtensions.Packages.Concat(BuiltInMetadataExtensions.Packages).ToArray());

    public TranslationExtensionCatalog(string runtimeDirectory, Func<LlmAppSettings> settings,
        IExtensionResources resources)
    {
        _root = runtimeDirectory;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Resources = resources;
        Manager = new ExtensionManager(runtimeDirectory, resources, new FileExtensionStore(runtimeDirectory),
            new CompiledExtensionGenerationLoader(runtimeDirectory));
        Runner = new TranslationExtensionRunner(Manager, (lease, config, gate) =>
            TranslationExtensionRunner.WrapWithGate(
                (resources is HostExtensionResources host ? host.WithDispatchGate(gate) : resources).ForRun(config),
                gate));
        _metadataSources = BuiltInMetadataExtensions.SourceDefinitions
            .Select(entry => CreateMetadataSource(entry.ExtensionId, entry.Definition))
            .ToArray();
    }

    /// <summary>
    /// Opt-in, off-by-default phase diagnostics for catalog activation paths. Spans exist only while
    /// an ActivityListener subscribes to <see cref="SourceName"/>; without one each phase is a single
    /// static listener check. Tags carry engine identity and outcomes only, never source text,
    /// document content or machine-local paths.
    /// </summary>
    private static class CatalogDiagnostics
    {
        internal const string SourceName = "Patchouli.Host.Extensions";
        private static readonly ActivitySource Source = new(SourceName);

        private static Activity? Start(string name, string? extensionId)
        {
            Activity? activity = Source.StartActivity(name, ActivityKind.Internal);
            if (activity is not null && !string.IsNullOrEmpty(extensionId))
            {
                activity.SetTag("extension.package", extensionId);
            }

            return activity;
        }

        private static void Finish(Activity? activity, string outcome, Exception? error = null)
        {
            if (activity is null)
            {
                return;
            }

            activity.SetTag("extension.outcome", outcome);
            if (error is not null)
            {
                activity.SetTag("extension.error.type", error.GetType().Name);
            }
        }

        internal static async Task<T> SegmentAsync<T>(string name, string? extensionId,
            Func<Task<T>> operation)
        {
            if (!Source.HasListeners())
            {
                return await operation().ConfigureAwait(false);
            }

            using Activity? activity = Start(name, extensionId);
            try
            {
                T result = await operation().ConfigureAwait(false);
                Finish(activity, "ok");
                return result;
            }
            catch (Exception error)
            {
                Finish(activity, error is OperationCanceledException ? "cancelled" : "failed", error);
                throw;
            }
        }

        internal static Task SegmentAsync(string name, string? extensionId, Func<Task> operation)
        {
            return SegmentAsync<int>(name, extensionId, async () =>
            {
                await operation().ConfigureAwait(false);
                return 0;
            });
        }
    }

    public IExtensionManager Manager { get; }
    public IExtensionResources Resources { get; }
    public TranslationExtensionRunner Runner { get; }
    public IReadOnlyList<IMetadataSource> MetadataSources => Volatile.Read(ref _metadataSources);

    /// <summary>Bundled package snapshots (read-only); used to list never-activated built-ins.</summary>
    public IReadOnlyList<ExtensionPackageSnapshot> BundledPackageSnapshots => BundledPackages;

    /// <summary>Reads metadata and trusted schema without initializing or checking an extension.</summary>
    public async Task<ExtensionConfigurationDescription> GetConfigurationDescriptionAsync(string id,
        CancellationToken cancellationToken = default)
    {
        await EnsureBaselineInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await ExtensionConfigurationDescriptionService.ReadAsync(Manager, BundledPackages, id,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExtensionDomainRun> AcquireDomainAsync(string id,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        IExtensionLease lease = await Manager.AcquireAsync(id, cancellationToken).ConfigureAwait(false)
                                ?? throw new InvalidOperationException("The selected extension is not active.");
        try
        {
            if (lease.Descriptor.Kind is not (ExtensionKind.Ocr or ExtensionKind.Retrieval))
            {
                throw new InvalidOperationException("Select an OCR or retrieval extension.");
            }

            ExtensionConfigSnapshot config = ExtensionConfiguration.Resolve(lease.ConfigFields, lease.Config);
            IExtensionResources runResources = Resources.ForRun(config);
            return new ExtensionDomainRun(lease, runResources, config);
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private IMetadataSource CreateMetadataSource(string id, MetadataSourceDefinition definition)
    {
        // Lazy per-source activation: a lookup only starts the package that declares the requested
        // identifier scheme (see EnsureExtensionAsync) instead of initializing every bundled source.
        return new ExtensionMetadataSource(Manager, id, definition,
            (lease, config) => (Resources is HostExtensionResources host
                ? host.ForDomain("metadata")
                : Resources).ForRun(config),
            cancellationToken => EnsureExtensionAsync(id, cancellationToken));
    }

    private async Task RefreshMetadataSourcesAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, ExtensionGenerationInfo> active = Manager.List()
            .Where(item => item.IsActive)
            .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        List<IMetadataSource> sources = [];
        HashSet<string> bundledMetadataIds = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string extensionId, MetadataSourceDefinition declared) in
                 BuiltInMetadataExtensions.SourceDefinitions)
        {
            bundledMetadataIds.Add(extensionId);
            MetadataSourceDefinition definition = declared;
            if (active.ContainsKey(extensionId))
            {
                await using IExtensionLease? lease =
                    await Manager.AcquireAsync(extensionId, cancellationToken).ConfigureAwait(false);
                if (lease?.Metadata is { } metadata)
                {
                    definition = metadata.SourceDefinition;
                }
            }

            sources.Add(CreateMetadataSource(extensionId, definition));
        }

        // Third-party metadata extensions that are already active join the list alongside the
        // declarative built-in definitions, which stay discoverable before first activation.
        foreach (ExtensionGenerationInfo info in active.Values)
        {
            if (bundledMetadataIds.Contains(info.Id))
            {
                continue;
            }

            await using IExtensionLease? lease =
                await Manager.AcquireAsync(info.Id, cancellationToken).ConfigureAwait(false);
            if (lease?.Metadata is { } metadata)
            {
                sources.Add(CreateMetadataSource(info.Id, metadata.SourceDefinition));
            }
        }

        Volatile.Write(ref _metadataSources, sources.AsReadOnly());
    }

    public async Task<ExtensionCheckResult> ActivateAsync(ExtensionPackageSnapshot package,
        CancellationToken cancellationToken = default)
    {
        ExtensionCheckResult check =
            await Manager.ActivateAsync(package.Id, package, cancellationToken).ConfigureAwait(false);
        if (check.Succeeded)
        {
            await RefreshMetadataSourcesAsync(cancellationToken).ConfigureAwait(false);
        }

        return check;
    }

    public async Task<bool> RollbackAsync(string id, CancellationToken cancellationToken = default)
    {
        bool restored = await Manager.RollbackAsync(id, cancellationToken).ConfigureAwait(false);
        if (restored)
        {
            await RefreshMetadataSourcesAsync(cancellationToken).ConfigureAwait(false);
        }

        return restored;
    }

    public async Task<bool> ClearDerivedStateAsync(string id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using IExtensionLease? lease = await Manager.AcquireAsync(id, cancellationToken).ConfigureAwait(false);
        if (lease?.Definition is not IExtensionDerivedState derived)
        {
            return false;
        }

        await derived.ClearDerivedStateAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task SaveDraftAsync(ExtensionPackageSnapshot draft,
        CancellationToken cancellationToken = default)
    {
        if (draft.Id.Length == 0 || draft.Id.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_')) ||
            draft.Id is "." or "..")
        {
            throw new ArgumentException("Invalid extension ID.", nameof(draft));
        }

        string directory = Path.Combine(_root, ".drafts");
        Directory.CreateDirectory(directory);
        // Persist before writing source: watcher events may arrive during the source save.
        await File.WriteAllTextAsync(Path.Combine(directory, draft.Id), draft.Fingerprint, cancellationToken)
            .ConfigureAwait(false);
        await Manager.SavePackageAsync(draft, cancellationToken).ConfigureAwait(false);
    }

    public IReadOnlyList<string> ListWorkingPackages()
    {
        string directory = Path.Combine(_root, "extensions");
        return Directory.Exists(directory)
            ? Directory.EnumerateDirectories(directory).Select(Path.GetFileName)
                .OfType<string>().Where(id => !id.StartsWith('.')).Order(StringComparer.Ordinal).ToArray()
            : [];
    }

    public async Task<TranslationPageResult> TestDraftAsync(ExtensionPackageSnapshot draft,
        ExtensionConfigSnapshot configuration, string text, string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        string testRoot = Path.Combine(_root, ".contract-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using IExtensionManager staging = new ExtensionManager(testRoot, Resources,
                new FileExtensionStore(testRoot), new CompiledExtensionGenerationLoader(testRoot));
            ExtensionCheckResult check = await staging.ActivateAsync(draft.Id, draft, cancellationToken)
                .ConfigureAwait(false);
            if (!check.Succeeded)
            {
                throw new InvalidOperationException(string.Join("\n", check.Diagnostics.Select(item =>
                    $"{item.Code} {item.Location.FileName}:{item.Location.Line}: {item.Message}")));
            }

            await staging.SaveConfigAsync(draft.Id, configuration, cancellationToken).ConfigureAwait(false);
            TranslationExtensionRunner runner = new(staging, (lease, config, gate) => Resources.ForRun(config));
            await using TranslationExtensionRun run = await runner.AcquireAsync(draft.Id,
                                                          cancellationToken: cancellationToken).ConfigureAwait(false)
                                                      ?? throw new InvalidOperationException(
                                                          "The staged translation contract could not be acquired.");
            if (run.Config.Values.TryGetValue("target-language", out string? configuredTarget) &&
                !string.IsNullOrWhiteSpace(configuredTarget))
            {
                targetLanguage = configuredTarget;
            }

            return await RunSampleAsync(run, text, targetLanguage, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (Directory.Exists(testRoot))
                {
                    Directory.Delete(testRoot, true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Trace.WriteLine(
                    $"Extension contract test cleanup deferred: {exception.GetType().Name}");
            }
        }
    }

    private async Task EnsureBaselineInitializedAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _baselineInitialized))
        {
            return;
        }

        await CatalogDiagnostics.SegmentAsync("extension.catalog.baseline.gate", null,
            () => _baselineGate.WaitAsync(cancellationToken)).ConfigureAwait(false);
        try
        {
            if (_baselineInitialized)
            {
                return;
            }

            // Install only artifacts whose identity is the immutable bundled source fingerprint.
            // The generation loader can now activate normal bundled packages without FCS; edited
            // working copies have distinct fingerprints and are never mapped to this artifact.
            foreach (ExtensionPackageSnapshot package in BundledPackages)
            {
                await BuiltInExtensionArtifacts.InstallAsync(_root, package, cancellationToken)
                    .ConfigureAwait(false);
            }

            ExtensionConfigSnapshot existing = await CatalogDiagnostics.SegmentAsync(
                "extension.catalog.seed", "translation.llm",
                () => Manager.ReadConfigAsync("translation.llm", cancellationToken)).ConfigureAwait(false);
            if (existing.Revision == 0 && existing.Values.Count == 0)
            {
                // One-time compatibility seed. Credentials remain exclusively in the credential store.
                TranslationAppSettings legacy = _settings().Translation;
                Dictionary<string, string> values = new(StringComparer.Ordinal)
                {
                    ["target-language"] = legacy.TargetLanguage,
                    ["batch-size"] = legacy.BatchSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["concurrency"] = legacy.Concurrency.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["max-batch-characters"] =
                        legacy.MaxBatchCharacters.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["max-batch-tokens"] =
                        (legacy.MaxBatchTokens ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["source-language"] = legacy.SourceLanguage,
                    ["request-interval"] =
                        legacy.RequestIntervalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["request-timeout"] =
                        legacy.RequestTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["max-retries"] = legacy.MaxRetries.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["window-radius"] = legacy.WindowRadius.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["backfill"] = legacy.BackfillPreviousWindowTranslation ? "true" : "false"
                };
                if (!string.IsNullOrWhiteSpace(legacy.ProviderId) || !string.IsNullOrWhiteSpace(legacy.Model))
                {
                    values["llm.primary"] = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        providerId = legacy.ProviderId ?? "", model = legacy.Model ?? ""
                    });
                }

                await CatalogDiagnostics.SegmentAsync("extension.catalog.seed", "translation.llm",
                        () => Manager.SaveConfigAsync("translation.llm", new ExtensionConfigSnapshot(
                            new ReadOnlyDictionary<string, string>(values), 1), cancellationToken))
                    .ConfigureAwait(false);
            }

            StartWatching();
            Volatile.Write(ref _baselineInitialized, true);
        }
        finally
        {
            _baselineGate.Release();
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await CatalogDiagnostics.SegmentAsync("extension.catalog.init.gate", null,
            () => _initialization.WaitAsync(cancellationToken)).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_initialized)
            {
                return;
            }

            await CatalogDiagnostics.SegmentAsync("extension.catalog.baseline", null,
                () => EnsureBaselineInitializedAsync(cancellationToken)).ConfigureAwait(false);

            foreach (ExtensionPackageSnapshot builtin in BundledPackages)
            {
                // Metadata sources activate lazily for the identifier scheme being looked up and are
                // listed from their declarative definitions, so eager activation would start up to 16
                // workers that the current lookup does not need.
                if (BuiltInMetadataExtensions.Packages.Any(package =>
                        string.Equals(package.Id, builtin.Id, StringComparison.Ordinal)))
                {
                    continue;
                }

                try
                {
                    await EnsureExtensionAsync(builtin.Id, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(
                                                      exception, "host.extensions.initialize"))
                {
                    // One broken bundled package must not make the whole fleet (and its settings pages)
                    // unusable: record the failure and continue; callers surface it per extension.
                }
            }

            string sourcesRoot = Path.Combine(_root, "extensions");
            if (Directory.Exists(sourcesRoot))
            {
                HashSet<string> builtinIds = BundledPackages.Select(package => package.Id)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (string directory in Directory.EnumerateDirectories(sourcesRoot))
                {
                    string id = Path.GetFileName(directory);
                    if (!id.StartsWith('.') && !builtinIds.Contains(id))
                    {
                        await CatalogDiagnostics.SegmentAsync("extension.catalog.restore", id,
                            () => Manager.RestoreAsync(id, cancellationToken)).ConfigureAwait(false);
                    }
                }
            }

            await RefreshMetadataSourcesAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initialization.Release();
        }
    }

    /// <summary>
    ///     Activates exactly one bundled package on demand with single-flight protection per engine.
    ///     Unrelated engines are not activated and their errors do not block this extension.
    /// </summary>
    public async Task EnsureExtensionAsync(string extensionId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await CatalogDiagnostics.SegmentAsync("extension.catalog.baseline", extensionId,
            () => EnsureBaselineInitializedAsync(cancellationToken)).ConfigureAwait(false);

        if (Manager.List().Any(item =>
                string.Equals(item.Id, extensionId, StringComparison.Ordinal) && item.IsActive))
        {
            return;
        }

        SemaphoreSlim gate = _engineGates.GetOrAdd(extensionId, static _ => new SemaphoreSlim(1, 1));
        await CatalogDiagnostics.SegmentAsync("extension.catalog.engine.gate", extensionId,
            () => gate.WaitAsync(cancellationToken)).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (Manager.List().Any(item =>
                    string.Equals(item.Id, extensionId, StringComparison.Ordinal) && item.IsActive))
            {
                return;
            }

            ExtensionPackageSnapshot? builtin = BundledPackages.FirstOrDefault(package =>
                string.Equals(package.Id, extensionId, StringComparison.Ordinal));
            if (builtin is null)
            {
                await CatalogDiagnostics.SegmentAsync("extension.catalog.restore", extensionId,
                    () => Manager.RestoreAsync(extensionId, cancellationToken)).ConfigureAwait(false);
                return;
            }

            if (await CatalogDiagnostics.SegmentAsync("extension.catalog.restore", builtin.Id,
                    () => Manager.RestoreAsync(builtin.Id, cancellationToken)).ConfigureAwait(false))
            {
                return;
            }

            ExtensionCheckResult check = await CatalogDiagnostics.SegmentAsync(
                "extension.catalog.activate", builtin.Id,
                () => ActivateBundledAsync(builtin, cancellationToken)).ConfigureAwait(false);
            if (!check.Succeeded)
            {
                string errors = string.Join("; ",
                    check.Diagnostics.Select(diagnostic =>
                        $"{diagnostic.Code} {diagnostic.Location.FileName}:{diagnostic.Location.Line}: {diagnostic.Message}"));
                throw new InvalidOperationException(
                    $"Extension '{builtin.Id}' failed to activate: {errors}");
            }

            await RefreshMetadataSourcesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Activates one bundled package, preserving drafts and never throwing on failure.</summary>
    private async Task<ExtensionCheckResult> ActivateBundledAsync(ExtensionPackageSnapshot builtin,
        CancellationToken cancellationToken)
    {
        ExtensionPackageSnapshot candidate;
        bool hadWorkingCopy = true;
        try
        {
            candidate = await Manager.ReadSourceAsync(builtin.Id, cancellationToken).ConfigureAwait(false);
            if (!candidate.Sources.ContainsKey("main.fsx"))
            {
                candidate = builtin;
                hadWorkingCopy = false;
            }
        }
        catch (DirectoryNotFoundException)
        {
            candidate = builtin;
            hadWorkingCopy = false;
        }
        catch (FileNotFoundException)
        {
            candidate = builtin;
            hadWorkingCopy = false;
        }

        string draftMarker = Path.Combine(_root, ".drafts", builtin.Id);
        bool preserveDraft = File.Exists(draftMarker) &&
                             await File.ReadAllTextAsync(draftMarker, cancellationToken)
                                 .ConfigureAwait(false) == candidate.Fingerprint;
        if (preserveDraft)
        {
            candidate = builtin;
        }

        if (candidate.Fingerprint == builtin.Fingerprint)
        {
            await BuiltInExtensionArtifacts.InstallAsync(_root, builtin, cancellationToken).ConfigureAwait(false);
        }

        // Persist the editable source before checking it so a failing built-in still appears in the
        // extension editor with its diagnostics instead of disappearing from the list.
        if (!hadWorkingCopy && !preserveDraft)
        {
            await Manager.SaveSourceAsync(builtin.Id, builtin.Sources, cancellationToken).ConfigureAwait(false);
        }

        ExtensionCheckResult result = await Manager.ActivateAsync(candidate.Id, candidate, cancellationToken)
            .ConfigureAwait(false);
        if (result.Diagnostics.Any(diagnostic => diagnostic.Code == "EXT0014"))
        {
            // An old package format needs an explicit source migration, not a silent switch to
            // bundled code that could discard the user's extension implementation choices.
            return result;
        }

        if (!result.Succeeded && candidate.Fingerprint != builtin.Fingerprint)
        {
            result = await Manager.ActivateAsync(builtin.Id, builtin, cancellationToken).ConfigureAwait(false);
        }

        if (!result.Succeeded)
        {
            Trace.WriteLine(
                $"Built-in extension '{builtin.Id}' failed to activate: {string.Join(" | ", result.Diagnostics.Select(diagnostic => $"{diagnostic.Code} {diagnostic.Location.FileName}:{diagnostic.Location.Line}: {diagnostic.Message}"))}");
            return result;
        }

        if (candidate.Fingerprint == builtin.Fingerprint && !preserveDraft)
        {
            await Manager.SaveSourceAsync(builtin.Id, builtin.Sources, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    public async Task<TranslationPageResult> TestAsync(string id, string text, string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using TranslationExtensionRun? run = await Runner.AcquireAsync(id, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (run is null)
        {
            throw new InvalidOperationException("The selected extension is not active.");
        }

        if (run.Config.Values.TryGetValue("target-language", out string? configuredTarget) &&
            !string.IsNullOrWhiteSpace(configuredTarget))
        {
            targetLanguage = configuredTarget;
        }

        string sourceLanguage = run.Config.Values.GetValueOrDefault("source-language", "auto");
        return await RunSampleAsync(run, text, targetLanguage, cancellationToken, sourceLanguage)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one real extension invocation over a single synthetic text box through the task-level contract.
    /// The extension must take the one page, answer exactly that box and commit it; anything else is a
    /// contract violation the settings dialog reports instead of showing a result.
    /// </summary>
    private static async Task<TranslationPageResult> RunSampleAsync(TranslationExtensionRun run, string text,
        string targetLanguage, CancellationToken cancellationToken, string sourceLanguage = "auto")
    {
        string effectiveLanguage = string.IsNullOrWhiteSpace(targetLanguage) ? "en" : targetLanguage.Trim();
        DocumentBoxId boxId = DocumentBoxId.New();
        TranslationExtensionOptions options = new(
            1,
            Math.Max(TranslationSettings.MinMaxBatchCharacters, text.Length * 2),
            Concurrency: 1,
            MaxRetries: 0,
            DocumentTitle: null,
            ProviderId: null,
            Model: null);
        SinglePageTranslationJob job = new(
            DocumentInstanceId.New(),
            PageId.New(),
            DocumentTreeRevisionId.New(),
            boxId,
            text,
            sourceLanguage,
            effectiveLanguage,
            options,
            run.Config);
        await run.RunAsync(job, cancellationToken).ConfigureAwait(false);
        TranslationPageResult result = job.Committed ??
                                       throw new InvalidOperationException(
                                           "The sample contract requires the extension to commit its page.");
        if (result.Outcomes.Count != 1 || result.Outcomes[0].BoxId != boxId ||
            !Enum.IsDefined(result.Outcomes[0].Kind))
        {
            throw new InvalidOperationException(
                "The sample contract requires exactly one outcome for its input Box.");
        }

        return result;
    }

    private void StartWatching()
    {
        string root = Path.Combine(_root, "extensions");
        Directory.CreateDirectory(root);
        lock (_watchGate)
        {
            // _disposed is set before DisposeAsync takes this gate, so a watcher published here is
            // always seen by DisposeAsync; without the gate an EnsureExtensionAsync racing shutdown
            // could leave an enabled, undisposed watcher behind.
            if (_watcher is not null || Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            _watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            _watcher.Changed += (_, args) => QueueSourceChange(root, args.FullPath);
            _watcher.Created += (_, args) => QueueSourceChange(root, args.FullPath);
            _watcher.Deleted += (_, args) => QueueSourceChange(root, args.FullPath);
            _watcher.Renamed += (_, args) =>
            {
                QueueSourceChange(root, args.OldFullPath);
                QueueSourceChange(root, args.FullPath);
            };
            _watcher.Error += (_, _) => QueueAllSources(root);
            _watchWork = WatchSourcesAsync(_watchLifetime.Token);
            _watcher.EnableRaisingEvents = true;
        }

        QueueAllSources(root);
    }

    private void QueueSourceChange(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        string id = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        if (!id.StartsWith('.') && relative != id &&
            !relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part.StartsWith('.')))
        {
            // Config-only edits are handled by SaveConfigAsync and must not create a new source generation.
            if (Path.GetFileName(path) is not ("config.json" or "extension-state.json"))
            {
                _sourceChanges.Writer.TryWrite(id);
            }
        }
    }

    private void QueueAllSources(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        try
        {
            foreach (string path in Directory.EnumerateDirectories(root))
            {
                string id = Path.GetFileName(path);
                if (!id.StartsWith('.'))
                {
                    _sourceChanges.Writer.TryWrite(id);
                }
            }
        }
        catch (IOException exception)
        {
            Trace.WriteLine($"Extension source discovery deferred: {exception.GetType().Name}");
        }
        catch (UnauthorizedAccessException exception)
        {
            Trace.WriteLine($"Extension source discovery denied: {exception.GetType().Name}");
        }
    }

    private async Task WatchSourcesAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _sourceChanges.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                HashSet<string> pending = new(StringComparer.Ordinal);
                while (_sourceChanges.Reader.TryRead(out string? id))
                {
                    pending.Add(id);
                }

                await Task.Delay(350, cancellationToken).ConfigureAwait(false);
                while (_sourceChanges.Reader.TryRead(out string? id))
                {
                    pending.Add(id);
                }

                foreach (string id in pending)
                {
                    try
                    {
                        ExtensionPackageSnapshot before = await Manager.ReadSourceAsync(id, cancellationToken)
                            .ConfigureAwait(false);
                        await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                        ExtensionPackageSnapshot after = await Manager.ReadSourceAsync(id, cancellationToken)
                            .ConfigureAwait(false);
                        if (before.Fingerprint != after.Fingerprint)
                        {
                            _sourceChanges.Writer.TryWrite(id);
                            continue;
                        }

                        string draftMarker = Path.Combine(_root, ".drafts", id);
                        if ((File.Exists(draftMarker) &&
                             await File.ReadAllTextAsync(draftMarker, cancellationToken).ConfigureAwait(false) ==
                             after.Fingerprint) ||
                            _lastAttempt.GetValueOrDefault(id) == after.Fingerprint ||
                            Manager.List().Any(item => item.Id == id && item.Fingerprint == after.Fingerprint))
                        {
                            continue;
                        }

                        _lastAttempt[id] = after.Fingerprint;
                        await ActivateAsync(after, cancellationToken).ConfigureAwait(false);
                    }
                    catch (IOException exception)
                    {
                        Trace.WriteLine(
                            $"Extension source scan deferred: {exception.GetType().Name}");
                    }
                    catch (UnauthorizedAccessException exception)
                    {
                        Trace.WriteLine(
                            $"Extension source access failed: {exception.GetType().Name}");
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException &&
                                                      UnexpectedExceptionReporter.ReportCatch(exception,
                                                          "host.extensions.reload"))
                    {
                        // A bad package must not stop watching other independent extensions.
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The host waits for this worker before disposing the manager.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _initialization.WaitAsync().ConfigureAwait(false);
        try
        {
            Task watchWork;
            lock (_watchGate)
            {
                _watcher?.Dispose();
                watchWork = _watchWork;
            }

            _watchLifetime.Cancel();
            _sourceChanges.Writer.TryComplete();
            await watchWork.ConfigureAwait(false);
            await Manager.DisposeAsync().ConfigureAwait(false);
            _watchLifetime.Dispose();
        }
        finally
        {
            _initialization.Release();
        }
    }
}
