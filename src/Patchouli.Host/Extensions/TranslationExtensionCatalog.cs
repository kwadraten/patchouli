using Patchouli.Extensions.Worker;
using System.Collections.ObjectModel;
using Patchouli.Extensions;
using Patchouli.Extensions.Contracts;
using Patchouli.Infrastructure.Translation;
using Patchouli.Llm;
using Patchouli.Core.Ids;
using System.Threading.Channels;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Bibliography.MetadataLookup;
using Patchouli.Infrastructure.Bibliography.MetadataLookup;

namespace Patchouli.Host.Extensions;

/// <summary>Shared desktop/headless extension composition; never loads directly from an editor draft.</summary>
public sealed class TranslationExtensionCatalog : IAsyncDisposable
{
    private readonly SemaphoreSlim _initialization = new(1, 1);
    private readonly string _root;
    private readonly Func<LlmAppSettings> _settings;
    private bool _initialized;
    private int _disposed;
    private readonly CancellationTokenSource _watchLifetime = new();

    private readonly Channel<string> _sourceChanges = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly Dictionary<string, string> _lastAttempt = new(StringComparer.Ordinal);
    private FileSystemWatcher? _watcher;
    private Task _watchWork = Task.CompletedTask;
    private IReadOnlyList<IMetadataSource> _metadataSources = [];

    private static readonly IReadOnlyList<ExtensionPackageSnapshot> BundledPackages = Array.AsReadOnly(
        BuiltInTranslationExtensions.Packages.Concat(BuiltInMetadataExtensions.Packages).ToArray());

    public TranslationExtensionCatalog(string runtimeDirectory, Func<LlmAppSettings> settings,
        IExtensionCapabilities capabilities)
    {
        _root = runtimeDirectory;
        _settings = settings;
        Capabilities = capabilities;
        Authorization = new ExtensionAuthorizationService(runtimeDirectory,
            BundledPackages.Select(package => package.Id));
        Manager = new ExtensionManager(runtimeDirectory, capabilities, new FileExtensionStore(runtimeDirectory),
            new WorkerGenerationLoader(runtimeDirectory));
        Runner = new TranslationExtensionRunner(Manager, (lease, config, gate) =>
            TranslationExtensionRunner.WrapWithGate(Authorization.Freeze(
                capabilities is HostExtensionCapabilities host ? host.WithDispatchGate(gate) : capabilities,
                lease.Descriptor, config), gate));
        _metadataSources =
            [CreateMetadataSource("metadata.crossref", BuiltInMetadataExtensions.CrossrefSourceDefinition)];
    }

    public IExtensionManager Manager { get; }
    public IExtensionCapabilities Capabilities { get; }
    public ExtensionAuthorizationService Authorization { get; }
    public TranslationExtensionRunner Runner { get; }
    public IReadOnlyList<IMetadataSource> MetadataSources => Volatile.Read(ref _metadataSources);

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
            IExtensionCapabilities capabilities = Authorization.Freeze(Capabilities, lease.Descriptor, config);
            return new ExtensionDomainRun(lease, capabilities, config);
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private IMetadataSource CreateMetadataSource(string id, MetadataSourceDefinition definition)
    {
        return new ExtensionMetadataSource(Manager, id, definition,
            (lease, config) => Authorization.Freeze(Capabilities, lease.Descriptor, config), InitializeAsync);
    }

    private async Task RefreshMetadataSourcesAsync(CancellationToken cancellationToken)
    {
        List<IMetadataSource> sources = [];
        foreach (ExtensionGenerationInfo info in Manager.List().Where(item => item.IsActive))
        {
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
        await Manager.SaveSourceAsync(draft.Id, draft.Sources, cancellationToken).ConfigureAwait(false);
    }

    public IReadOnlyList<string> ListWorkingPackages()
    {
        string directory = Path.Combine(_root, "extensions");
        return Directory.Exists(directory)
            ? Directory.EnumerateDirectories(directory).Select(Path.GetFileName)
                .OfType<string>().Where(id => !id.StartsWith('.')).Order(StringComparer.Ordinal).ToArray()
            : [];
    }

    public async Task<TranslationExtensionResult> TestDraftAsync(ExtensionPackageSnapshot draft,
        ExtensionConfigSnapshot configuration, string text, string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        string testRoot = Path.Combine(_root, ".contract-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using IExtensionManager staging = new ExtensionManager(testRoot, Capabilities,
                new FileExtensionStore(testRoot), new WorkerGenerationLoader(testRoot));
            ExtensionCheckResult check = await staging.ActivateAsync(draft.Id, draft, cancellationToken)
                .ConfigureAwait(false);
            if (!check.Succeeded)
            {
                throw new InvalidOperationException(string.Join("\n", check.Diagnostics.Select(item =>
                    $"{item.Code} {item.Location.FileName}:{item.Location.Line}: {item.Message}")));
            }

            await staging.SaveConfigAsync(draft.Id, configuration, cancellationToken).ConfigureAwait(false);
            TranslationExtensionRunner runner = new(staging, (lease, config, gate) =>
                Authorization.Freeze(Capabilities, lease.Descriptor, config));
            await using TranslationExtensionRun run = await runner.AcquireAsync(draft.Id,
                                                          cancellationToken: cancellationToken).ConfigureAwait(false)
                                                      ?? throw new InvalidOperationException(
                                                          "The staged translation contract could not be acquired.");
            if (run.Config.Values.TryGetValue("target-language", out string? configuredTarget) &&
                !string.IsNullOrWhiteSpace(configuredTarget))
            {
                targetLanguage = configuredTarget;
            }

            DocumentBoxId boxId = DocumentBoxId.New();
            TranslationExtensionContext context = new(new ExtensionPageIdentity(DocumentInstanceId.New(),
                    PageId.New(), DocumentTreeRevisionId.New(), 1), "auto", targetLanguage,
                [new ExtensionBox(boxId, null, "text", null, text, "{}")], run.Config);
            TranslationExtensionResult result =
                await run.TranslateAsync(context, cancellationToken).ConfigureAwait(false);
            if (result.Outcomes.Count != 1 || result.Outcomes[0].BoxId != boxId ||
                !Enum.IsDefined(result.Outcomes[0].Kind))
            {
                throw new InvalidOperationException(
                    "The sample contract requires exactly one outcome for its input Box.");
            }

            return result;
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
                System.Diagnostics.Trace.WriteLine(
                    $"Extension contract test cleanup deferred: {exception.GetType().Name}");
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_initialized)
            {
                return;
            }

            foreach (ExtensionPackageSnapshot builtin in BundledPackages)
            {
                if (await Manager.RestoreAsync(builtin.Id, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                ExtensionPackageSnapshot candidate;
                try
                {
                    candidate = await Manager.ReadSourceAsync(builtin.Id, cancellationToken).ConfigureAwait(false);
                    if (!candidate.Sources.ContainsKey("main.fsx"))
                    {
                        candidate = builtin;
                    }
                }
                catch (DirectoryNotFoundException)
                {
                    candidate = builtin;
                }
                catch (FileNotFoundException)
                {
                    candidate = builtin;
                }

                string draftMarker = Path.Combine(_root, ".drafts", builtin.Id);
                bool preserveDraft = File.Exists(draftMarker) &&
                                     await File.ReadAllTextAsync(draftMarker, cancellationToken)
                                         .ConfigureAwait(false) == candidate.Fingerprint;
                if (preserveDraft)
                {
                    candidate = builtin;
                }

                ExtensionCheckResult result = await Manager.ActivateAsync(candidate.Id, candidate, cancellationToken)
                    .ConfigureAwait(false);
                if (!result.Succeeded && candidate.Fingerprint != builtin.Fingerprint)
                {
                    result = await Manager.ActivateAsync(builtin.Id, builtin, cancellationToken).ConfigureAwait(false);
                }

                if (!result.Succeeded)
                {
                    throw new InvalidOperationException(string.Join("\n", result.Diagnostics.Select(diagnostic =>
                        $"{diagnostic.Code} {diagnostic.Location.FileName}:{diagnostic.Location.Line}: {diagnostic.Message}")));
                }

                if (candidate.Fingerprint == builtin.Fingerprint && !preserveDraft)
                {
                    await Manager.SaveSourceAsync(builtin.Id, builtin.Sources, cancellationToken).ConfigureAwait(false);
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
                        await Manager.RestoreAsync(id, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            ExtensionConfigSnapshot existing = await Manager.ReadConfigAsync("translation.llm", cancellationToken)
                .ConfigureAwait(false);
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

                await Manager.SaveConfigAsync("translation.llm", new ExtensionConfigSnapshot(
                    new ReadOnlyDictionary<string, string>(values), 1), cancellationToken).ConfigureAwait(false);
            }

            await RefreshMetadataSourcesAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
            StartWatching();
        }
        finally
        {
            _initialization.Release();
        }
    }

    public async Task<TranslationExtensionResult> TestAsync(string id, string text, string targetLanguage,
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
        TranslationExtensionContext context = new(new ExtensionPageIdentity(DocumentInstanceId.New(), PageId.New(),
                DocumentTreeRevisionId.New(), 1),
            sourceLanguage, targetLanguage, [new ExtensionBox(DocumentBoxId.New(), null, "text", null, text, "{}")],
            run.Config);
        return await run.TranslateAsync(context, cancellationToken).ConfigureAwait(false);
    }

    private void StartWatching()
    {
        string root = Path.Combine(_root, "extensions");
        Directory.CreateDirectory(root);
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
            System.Diagnostics.Trace.WriteLine($"Extension source discovery deferred: {exception.GetType().Name}");
        }
        catch (UnauthorizedAccessException exception)
        {
            System.Diagnostics.Trace.WriteLine($"Extension source discovery denied: {exception.GetType().Name}");
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
                        System.Diagnostics.Trace.WriteLine(
                            $"Extension source scan deferred: {exception.GetType().Name}");
                    }
                    catch (UnauthorizedAccessException exception)
                    {
                        System.Diagnostics.Trace.WriteLine(
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
            _watcher?.Dispose();
            _watchLifetime.Cancel();
            _sourceChanges.Writer.TryComplete();
            await _watchWork.ConfigureAwait(false);
            await Manager.DisposeAsync().ConfigureAwait(false);
            _watchLifetime.Dispose();
        }
        finally
        {
            _initialization.Release();
        }
    }
}
