using System.Text;
using System.Text.Json;
using Patchouli.Core.Downloads;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Downloads;

namespace Patchouli.Host.Plugins;

public sealed class PluginStore : IPluginStore, IDisposable
{
    private const int MaxIndexBytes = 2 * 1024 * 1024;
    private const int MaxSignatureBytes = 16 * 1024;
    private readonly HttpClient _catalogClient;
    private readonly HttpClient _downloadClient;
    private readonly PluginStoreOptions _options;
    private readonly IPluginProcessLauncher _launcher;
    private readonly PluginClientOptions _clientOptions;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _pluginsRoot;
    private readonly string _registryPath;
    private readonly string _indexCachePath;
    private readonly string _signatureCachePath;
    private readonly Func<string, Task>? _registryChanged;
    private readonly DownloadTaskService? _downloads;

    private readonly Func<ExtensionPackageSnapshot, CancellationToken, Task<ExtensionCheckResult>>?
        _extensionActivator;

    private readonly Func<CancellationToken, Task<IReadOnlyDictionary<string, string>>>?
        _extensionInstalledVersions;

    private PluginCatalogIndex? _lastIndex;

    public PluginStore(HttpClient catalogClient, HttpClient downloadClient, PluginStoreOptions options,
        IPluginProcessLauncher? launcher = null, PluginClientOptions? clientOptions = null,
        Func<string, Task>? registryChanged = null, DownloadTaskService? downloads = null,
        Func<ExtensionPackageSnapshot, CancellationToken, Task<ExtensionCheckResult>>? extensionActivator =
            null,
        Func<CancellationToken, Task<IReadOnlyDictionary<string, string>>>? extensionInstalledVersions =
            null)
    {
        _catalogClient = catalogClient;
        _downloadClient = downloadClient;
        _options = options;
        _launcher = launcher ?? new PluginProcessLauncher();
        _clientOptions = clientOptions ?? new PluginClientOptions();
        _registryChanged = registryChanged;
        _downloads = downloads;
        _extensionActivator = extensionActivator;
        _extensionInstalledVersions = extensionInstalledVersions;
        _pluginsRoot = Path.GetFullPath(Path.Combine(options.DataDirectory, "plugins"));
        _registryPath = PluginRegistry.RegistryPath(options.DataDirectory);
        _indexCachePath = Path.Combine(_pluginsRoot, "catalog", "index.json");
        _signatureCachePath = _indexCachePath + ".minisig";
    }

    public async Task<Result<PluginStoreSnapshot>> RefreshAsync(bool force,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                return await RefreshCoreAsync(force, cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return Failure<PluginStoreSnapshot>("cancelled", "Refreshing the plugin catalog was cancelled.");
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return Failure<PluginStoreSnapshot>("unavailable", exception.Message);
        }
    }

    private async Task<Result<PluginStoreSnapshot>> RefreshCoreAsync(bool force,
        CancellationToken cancellationToken)
    {
        if (!force)
        {
            Result<PluginCatalogIndex> cached = LoadCachedIndex();
            if (cached.IsSuccess)
            {
                _lastIndex = cached.Value;
                return Result<PluginStoreSnapshot>.Success(
                    await SnapshotAsync(cached.Value, true, null, cancellationToken));
            }
        }

        Result<PluginCatalogIndex> remote = await FetchIndexAsync(cancellationToken);
        if (remote.IsSuccess)
        {
            _lastIndex = remote.Value;
            return Result<PluginStoreSnapshot>.Success(
                await SnapshotAsync(remote.Value, false, null, cancellationToken));
        }

        Result<PluginCatalogIndex> fallback = LoadCachedIndex();
        if (fallback.IsSuccess)
        {
            _lastIndex = fallback.Value;
            return Result<PluginStoreSnapshot>.Success(await SnapshotAsync(fallback.Value, true,
                $"The online index could not be refreshed; the last verified cache is shown. {remote.ErrorMessage}",
                cancellationToken));
        }

        return Failure<PluginStoreSnapshot>(remote.ErrorCode ?? AppErrorCodes.ValidationFailed,
            $"{remote.ErrorMessage} No verified cached index is available.");
    }

    public Task<Result<PluginDefinition>> InstallAsync(string pluginId,
        CancellationToken cancellationToken = default)
    {
        if (_downloads is not { } downloads)
        {
            return InstallCoreAsync(pluginId, null, cancellationToken);
        }

        Result<PluginDefinition>? captured = null;
        Task<Result> completion = downloads.Start($"安装插件 {pluginId}", async (progress, token) =>
        {
            captured = await InstallCoreAsync(pluginId, progress, token)
                .ConfigureAwait(false);
            if (captured.IsSuccess)
            {
                return Result.Success();
            }

            return captured.ErrorCode == "cancelled" || token.IsCancellationRequested
                ? Result.Failure(DownloadTaskService.CancelledErrorCode, captured.ErrorMessage!)
                : Result.Failure(captured.ErrorCode!, captured.ErrorMessage!);
        }, cancellationToken, canRetry: false).Completion;
        return AwaitInstall(completion);

        async Task<Result<PluginDefinition>> AwaitInstall(Task<Result> pending)
        {
            Result outcome = await pending.ConfigureAwait(false);
            return outcome.IsSuccess && captured is { } done
                ? done
                : captured ?? Result<PluginDefinition>.Failure(
                    outcome.ErrorCode == DownloadTaskService.CancelledErrorCode
                        ? "cancelled"
                        : outcome.ErrorCode ?? "install_failed",
                    outcome.ErrorMessage ?? "The plugin installation failed.");
        }
    }

    private async Task<Result<PluginDefinition>> InstallCoreAsync(string pluginId,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        string temporaryRoot = Path.Combine(_pluginsRoot, ".staging", Guid.NewGuid().ToString("N"));
        string workDirectory = Path.Combine(temporaryRoot, "package");
        string? finalDirectory = null;
        bool installed = false;
        try
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (_lastIndex is null)
                {
                    Result<PluginStoreSnapshot> loaded = await RefreshCoreAsync(false, cancellationToken);
                    if (loaded.IsFailure)
                    {
                        return Failure<PluginDefinition>(loaded.ErrorCode!, loaded.ErrorMessage!);
                    }
                }

                PluginCatalogEntry? entry = _lastIndex!.Plugins.FirstOrDefault(plugin => plugin.Id == pluginId);
                if (entry is null)
                {
                    return Failure<PluginDefinition>(AppErrorCodes.NotFound,
                        $"Plugin '{pluginId}' is absent from the verified community index.");
                }

                PluginCatalogArtifact? artifact = entry.ArtifactFor(_options.CurrentPlatform);
                if (artifact is null)
                {
                    return Failure<PluginDefinition>(AppErrorCodes.UnsupportedOperation,
                        $"Plugin '{pluginId}' has no {_options.CurrentPlatform} release artifact.");
                }

                PluginRegistrySnapshot registry = PluginRegistry.Load(_registryPath, true);
                PluginRegistryEntry? current = FindRecord(registry.Entries, pluginId);
                if (current is not null && !IsManagedRecord(current, pluginId))
                {
                    return Failure<PluginDefinition>(AppErrorCodes.InvalidArgument,
                        $"Plugin '{pluginId}' is already registered from a local development package.");
                }

                finalDirectory = Path.Combine(_pluginsRoot, pluginId, entry.Version);
                if (Directory.Exists(finalDirectory))
                {
                    return Failure<PluginDefinition>(AppErrorCodes.InvalidArgument,
                        $"Plugin version '{entry.Version}' is already installed. Use rollback to activate it.");
                }

                Directory.CreateDirectory(temporaryRoot);
                Result<PluginDefinition> prepared = await PluginPackageInstaller.PrepareAsync(_downloadClient,
                    entry, artifact, workDirectory, Path.Combine(temporaryRoot, "package.zip"),
                    cancellationToken, progress);
                if (prepared.IsFailure)
                {
                    return prepared;
                }

                Result handshake = await VerifyHandshakeAsync(prepared.Value, cancellationToken);
                if (handshake.IsFailure)
                {
                    return Failure<PluginDefinition>(handshake.ErrorCode!,
                        $"Plugin handshake failed; nothing was activated. {handshake.ErrorMessage}");
                }

                WriteInstallMetadata(workDirectory, entry);
                Directory.CreateDirectory(Path.GetDirectoryName(finalDirectory)!);
                Directory.Move(workDirectory, finalDirectory);
                installed = true;
                List<PluginRegistryEntry> records = registry.Entries
                    .Where(record => !ReferenceEquals(record, current)).ToList();
                records.Add(new PluginRegistryEntry(
                    Path.Combine(finalDirectory, PluginManifestLoader.ManifestFileName), true,
                    prepared.Value.DeclaredVerbs, "community", entry.Repository, entry.SourceRevision));
                PluginRegistry.Save(_registryPath, records);
                await NotifyRegistryChangedAsync(pluginId);
                return prepared;
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return Failure<PluginDefinition>("cancelled", "The plugin installation was cancelled.");
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return Failure<PluginDefinition>(AppErrorCodes.ValidationFailed, exception.Message);
        }
        finally
        {
            if (installed && !PluginRegistry.Load(_registryPath, true).Entries.Any(record =>
                    record.ManifestPath.StartsWith(finalDirectory + Path.DirectorySeparatorChar, PathComparison)))
            {
                TryDelete(finalDirectory!);
            }

            TryDelete(temporaryRoot);
        }
    }

    public Task<Result> InstallExtensionAsync(string extensionId,
        CancellationToken cancellationToken = default)
    {
        if (_downloads is not { } downloads)
        {
            return InstallExtensionCoreAsync(extensionId, null, cancellationToken);
        }

        Result? captured = null;
        Task<Result> completion = downloads.Start($"安装扩展 {extensionId}", async (progress, token) =>
        {
            captured = await InstallExtensionCoreAsync(extensionId, progress, token)
                .ConfigureAwait(false);
            if (captured.IsSuccess)
            {
                return Result.Success();
            }

            return captured.ErrorCode == "cancelled" || token.IsCancellationRequested
                ? Result.Failure(DownloadTaskService.CancelledErrorCode, captured.ErrorMessage!)
                : Result.Failure(captured.ErrorCode!, captured.ErrorMessage!);
        }, cancellationToken, canRetry: false).Completion;
        return AwaitInstall(completion);

        async Task<Result> AwaitInstall(Task<Result> pending)
        {
            Result outcome = await pending.ConfigureAwait(false);
            return outcome.IsSuccess && captured is { } done
                ? done
                : captured ?? Result.Failure(
                    outcome.ErrorCode == DownloadTaskService.CancelledErrorCode
                        ? "cancelled"
                        : outcome.ErrorCode ?? "install_failed",
                    outcome.ErrorMessage ?? "The extension installation failed.");
        }
    }

    private async Task<Result> InstallExtensionCoreAsync(string extensionId,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        string temporaryRoot = Path.Combine(_pluginsRoot, ".staging", Guid.NewGuid().ToString("N"));
        string workDirectory = Path.Combine(temporaryRoot, "package");
        try
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (_lastIndex is null)
                {
                    Result<PluginStoreSnapshot> loaded = await RefreshCoreAsync(false, cancellationToken);
                    if (loaded.IsFailure)
                    {
                        return Result.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
                    }
                }

                ExtensionCatalogEntry? entry =
                    _lastIndex!.Extensions.FirstOrDefault(extension => extension.Id == extensionId);
                if (entry is null)
                {
                    return Result.Failure(AppErrorCodes.NotFound,
                        $"Extension '{extensionId}' is absent from the verified community index.");
                }

                if (_extensionActivator is null)
                {
                    return Result.Failure(AppErrorCodes.UnsupportedOperation,
                        "This host is not configured to install community extensions.");
                }

                Directory.CreateDirectory(temporaryRoot);
                Result downloaded = await PluginPackageInstaller.DownloadVerifiedAsync(_downloadClient,
                    entry.Artifact.Url, entry.Artifact.Size, entry.Artifact.Sha256,
                    Path.Combine(temporaryRoot, "package.zip"), cancellationToken, progress);
                if (downloaded.IsFailure)
                {
                    return downloaded;
                }

                PluginPackageInstaller.ExtractArchive(Path.Combine(temporaryRoot, "package.zip"),
                    workDirectory);
                Result<ExtensionPackageSnapshot> prepared = ReadExtensionPackage(entry.Id, workDirectory);
                if (prepared.IsFailure)
                {
                    return Result.Failure(prepared.ErrorCode!, prepared.ErrorMessage!);
                }

                ExtensionEntryMetadata metadata;
                try
                {
                    metadata = ExtensionEntryMetadata.Parse(prepared.Value);
                }
                catch (ExtensionMetadataException exception)
                {
                    return Result.Failure(AppErrorCodes.ValidationFailed,
                        $"The downloaded extension metadata is invalid: {exception.Message}");
                }

                if (metadata.Version != entry.Version || metadata.Kind != entry.Kind ||
                    !string.Equals(metadata.HostApi, entry.HostApi, StringComparison.Ordinal))
                {
                    return Result.Failure(AppErrorCodes.ValidationFailed,
                        "The downloaded extension identity or version differs from the signed index.");
                }

                ExtensionCheckResult check =
                    await _extensionActivator(prepared.Value, cancellationToken);
                return check.Succeeded
                    ? Result.Success()
                    : Result.Failure(AppErrorCodes.ValidationFailed,
                        "The extension package was downloaded and verified but failed to activate: " +
                        string.Join("; ", check.Diagnostics.Select(diagnostic => diagnostic.Message)));
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return Result.Failure("cancelled", "The extension installation was cancelled.");
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, exception.Message);
        }
        finally
        {
            TryDelete(temporaryRoot);
        }
    }

    private static Result<ExtensionPackageSnapshot> ReadExtensionPackage(string extensionId,
        string packageDirectory)
    {
        try
        {
            Dictionary<string, string> sources = new(StringComparer.Ordinal);
            Dictionary<string, ReadOnlyMemory<byte>> binaryFiles = new(StringComparer.Ordinal);
            string root = Path.GetFullPath(packageDirectory);
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(root, file)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');
                if (ExtensionPackageSnapshot.IsTextSourcePath(relative))
                {
                    sources[relative] = File.ReadAllText(file);
                }
                else
                {
                    binaryFiles[relative] = File.ReadAllBytes(file);
                }
            }

            return Result<ExtensionPackageSnapshot>.Success(
                new ExtensionPackageSnapshot(extensionId, sources, binaryFiles));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or ArgumentException)
        {
            return Result<ExtensionPackageSnapshot>.Failure(AppErrorCodes.ValidationFailed,
                $"The downloaded extension package is invalid: {exception.Message}");
        }
    }

    public async Task<Result> SetEnabledAsync(string pluginId, bool enabled,
        CancellationToken cancellationToken = default)
    {
        return await UpdateRecordAsync(pluginId, null, enabled, cancellationToken);
    }

    public async Task<Result<PluginDefinition>> ActivateAsync(string pluginId, string version,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                PluginRegistrySnapshot registry = PluginRegistry.Load(_registryPath, true);
                PluginRegistryEntry? current = FindRecord(registry.Entries, pluginId);
                if (current is null || !IsManagedRecord(current, pluginId))
                {
                    return Failure<PluginDefinition>(AppErrorCodes.NotFound,
                        $"Plugin '{pluginId}' is not installed from the community store.");
                }

                string targetDirectory = Path.Combine(_pluginsRoot, pluginId, version);
                Result<PluginDefinition> loaded = PluginManifestLoader.Load(
                    Path.Combine(targetDirectory, PluginManifestLoader.ManifestFileName));
                if (loaded.IsFailure || loaded.Value.Id != pluginId || loaded.Value.Version != version)
                {
                    return Failure<PluginDefinition>(AppErrorCodes.ValidationFailed,
                        "The selected rollback package is missing or has a mismatched identity.");
                }

                if (!current.AllowedVerbs.Order(StringComparer.Ordinal)
                        .SequenceEqual(loaded.Value.DeclaredVerbs.Order(StringComparer.Ordinal)))
                {
                    return Failure<PluginDefinition>(AppErrorCodes.InvalidArgument,
                        "The rollback version changes requested verbs; install it as a new explicitly approved release.");
                }

                Result handshake = await VerifyHandshakeAsync(loaded.Value, cancellationToken);
                if (handshake.IsFailure)
                {
                    return Failure<PluginDefinition>(handshake.ErrorCode!, handshake.ErrorMessage!);
                }

                InstallMetadata metadata = ReadInstallMetadata(targetDirectory);
                List<PluginRegistryEntry> records = registry.Entries
                    .Where(record => !ReferenceEquals(record, current)).ToList();
                records.Add(current with
                {
                    ManifestPath = Path.Combine(targetDirectory, PluginManifestLoader.ManifestFileName),
                    SourceRepository = metadata.Repository,
                    SourceRevision = metadata.SourceRevision
                });
                PluginRegistry.Save(_registryPath, records);
                await NotifyRegistryChangedAsync(pluginId);
                return loaded;
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return Failure<PluginDefinition>("cancelled", "Plugin activation was cancelled.");
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return Failure<PluginDefinition>(AppErrorCodes.ValidationFailed, exception.Message);
        }
    }

    public async Task<Result> UninstallAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (!IsSafePluginId(pluginId))
                {
                    return Result.Failure(AppErrorCodes.InvalidArgument,
                        $"'{pluginId}' is not a valid plugin id.");
                }

                PluginRegistrySnapshot registry = PluginRegistry.Load(_registryPath, true);
                string pluginRoot = Path.GetFullPath(Path.Combine(_pluginsRoot, pluginId));
                List<(PluginRegistryEntry Record, string Version)> managed = [];
                foreach (PluginRegistryEntry record in registry.Entries)
                {
                    if (record.Source == "community" &&
                        ManagedRecordVersion(record, pluginRoot) is { } version)
                    {
                        managed.Add((record, version));
                    }
                }

                if (managed.Count == 0)
                {
                    PluginRegistryEntry? other = FindRecord(registry.Entries, pluginId);
                    return other is not null
                        ? Result.Failure(AppErrorCodes.InvalidArgument,
                            $"Plugin '{pluginId}' is not installed from the community store.")
                        : Result.Failure(AppErrorCodes.NotFound,
                            $"Plugin '{pluginId}' is not installed from the community store.");
                }

                Result<List<string>> scope = DeletionScope(pluginRoot, pluginId,
                    managed.Select(entry => entry.Version));
                if (scope.IsFailure)
                {
                    return Result.Failure(scope.ErrorCode!, scope.ErrorMessage!);
                }

                cancellationToken.ThrowIfCancellationRequested();

                List<PluginRegistryEntry> disabled = registry.Entries
                    .Select(record => managed.Any(entry => ReferenceEquals(entry.Record, record))
                        ? record with { Enabled = false }
                        : record).ToList();
                PluginRegistry.Save(_registryPath, disabled);
                await NotifyRegistryChangedAsync(pluginId);

                Result ancestors = ValidateAncestors(pluginRoot, pluginId);
                if (ancestors.IsFailure)
                {
                    return Result.Failure(ancestors.ErrorCode!,
                        $"Plugin '{pluginId}' was disabled and its grants revoked, but {ancestors.ErrorMessage} " +
                        "Retry the uninstall.");
                }

                HashSet<string> registered = new(managed.Select(entry => entry.Version),
                    StringComparer.OrdinalIgnoreCase);
                foreach (string directory in scope.Value
                             .OrderBy(directory => registered.Contains(Path.GetFileName(directory))
                                 ? 1
                                 : 0))
                {
                    if (ContainsReparsePoint(directory))
                    {
                        return Result.Failure(AppErrorCodes.InvalidArgument,
                            $"Plugin '{pluginId}' was disabled and its grants revoked, but a reparse point " +
                            $"appeared under '{directory}' before cleanup; refusing to delete. Retry the uninstall.");
                    }

                    try
                    {
                        Directory.Delete(directory, true);
                    }
                    catch (Exception exception) when (IsExpected(exception))
                    {
                        return Result.Failure(AppErrorCodes.ValidationFailed,
                            $"Plugin '{pluginId}' was disabled and its grants revoked, but package " +
                            $"cleanup is incomplete ({exception.Message}). Retry the uninstall to finish removal.");
                    }
                }

                List<PluginRegistryEntry> remaining = registry.Entries
                    .Where(record => managed.All(entry => !ReferenceEquals(entry.Record, record))).ToList();
                PluginRegistry.Save(_registryPath, remaining);
                await NotifyRegistryChangedAsync(pluginId);
                if (Directory.Exists(pluginRoot) && !HasReparsePoint(pluginRoot) &&
                    !Directory.EnumerateFileSystemEntries(pluginRoot).Any())
                {
                    TryDelete(pluginRoot);
                }

                return Result.Success();
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return Result.Failure("cancelled", "The plugin uninstall was cancelled.");
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, exception.Message);
        }
    }

    private async Task NotifyRegistryChangedAsync(string pluginId)
    {
        if (_registryChanged is not null)
        {
            await _registryChanged(pluginId);
        }
    }

    private bool IsSafePluginId(string pluginId)
    {
        return !string.IsNullOrWhiteSpace(pluginId) &&
               string.Equals(Path.GetFileName(pluginId), pluginId, StringComparison.Ordinal) &&
               pluginId is not ("." or "..") &&
               pluginId.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
               string.Equals(
                   Path.GetDirectoryName(Path.GetFullPath(Path.Combine(_pluginsRoot, pluginId))),
                   _pluginsRoot, PathComparison);
    }

    private static string? ManagedRecordVersion(PluginRegistryEntry record, string pluginRoot)
    {
        string manifest = Path.GetFullPath(record.ManifestPath);
        if (!string.Equals(Path.GetFileName(manifest), PluginManifestLoader.ManifestFileName,
                PathComparison))
        {
            return null;
        }

        string? versionDirectory = Path.GetDirectoryName(manifest);
        if (versionDirectory is null ||
            !string.Equals(Path.GetDirectoryName(versionDirectory), pluginRoot, PathComparison))
        {
            return null;
        }

        string version = Path.GetFileName(versionDirectory);
        return ExtensionVersion.TryParse(version, out _) ? version : null;
    }

    private Result<List<string>> DeletionScope(string pluginRoot, string pluginId,
        IEnumerable<string> registeredVersions)
    {
        try
        {
            Result ancestors = ValidateAncestors(pluginRoot, pluginId);
            if (ancestors.IsFailure)
            {
                return Failure<List<string>>(ancestors.ErrorCode!, ancestors.ErrorMessage!);
            }

            HashSet<string> registered = new(registeredVersions, StringComparer.OrdinalIgnoreCase);
            List<string> scope = [];
            if (Directory.Exists(pluginRoot))
            {
                foreach (string directory in Directory.EnumerateDirectories(pluginRoot))
                {
                    string version = Path.GetFileName(directory);
                    if (HasReparsePoint(directory))
                    {
                        if (registered.Contains(version))
                        {
                            return Failure<List<string>>(AppErrorCodes.InvalidArgument,
                                $"Plugin '{pluginId}' has a reparse point at registered version '{directory}'; refusing to delete.");
                        }

                        continue;
                    }

                    bool selected = registered.Contains(version);
                    if (!selected)
                    {
                        Result<PluginDefinition> manifest = PluginManifestLoader.Load(
                            Path.Combine(directory, PluginManifestLoader.ManifestFileName));
                        selected = manifest.IsSuccess &&
                                   manifest.Value.Id == pluginId &&
                                   manifest.Value.Version == version &&
                                   File.Exists(Path.Combine(directory, ".patchouli-install.json"));
                    }

                    if (!selected)
                    {
                        continue;
                    }

                    if (ContainsReparsePoint(directory))
                    {
                        return Failure<List<string>>(AppErrorCodes.InvalidArgument,
                            $"Plugin '{pluginId}' contains a reparse point under '{directory}'; refusing to delete.");
                    }

                    scope.Add(directory);
                }
            }

            return Result<List<string>>.Success(scope);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return Failure<List<string>>(AppErrorCodes.ValidationFailed, exception.Message);
        }
    }

    private Result ValidateAncestors(string pluginRoot, string pluginId)
    {
        string? ancestor = pluginRoot;
        string stop = Path.GetFullPath(_options.DataDirectory);
        while (ancestor is not null)
        {
            if (HasReparsePoint(ancestor))
            {
                return Result.Failure(AppErrorCodes.InvalidArgument,
                    $"Plugin '{pluginId}' resolves through a reparse point at '{ancestor}'; refusing to delete.");
            }

            ancestor = string.Equals(ancestor, stop, PathComparison)
                ? null
                : Path.GetDirectoryName(ancestor);
        }

        return Result.Success();
    }

    private static bool HasReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is FileNotFoundException
                                              or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static bool ContainsReparsePoint(string directory)
    {
        if (HasReparsePoint(directory))
        {
            return true;
        }

        List<string> pending = [directory];
        while (pending.Count > 0)
        {
            string current = pending[^1];
            pending.RemoveAt(pending.Count - 1);
            foreach (string entry in Directory.EnumerateFileSystemEntries(current))
            {
                if (HasReparsePoint(entry))
                {
                    return true;
                }

                if (Directory.Exists(entry))
                {
                    pending.Add(entry);
                }
            }
        }

        return false;
    }

    private async Task<Result> UpdateRecordAsync(string pluginId, string? manifestPath, bool enabled,
        CancellationToken cancellationToken)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                PluginRegistrySnapshot registry = PluginRegistry.Load(_registryPath, true);
                PluginRegistryEntry? current = FindRecord(registry.Entries, pluginId);
                if (current is null || !IsManagedRecord(current, pluginId))
                {
                    return Result.Failure(AppErrorCodes.NotFound,
                        $"Plugin '{pluginId}' is not installed from the community store.");
                }

                List<PluginRegistryEntry> records = registry.Entries
                    .Where(record => !ReferenceEquals(record, current)).ToList();
                records.Add(current with { Enabled = enabled, ManifestPath = manifestPath ?? current.ManifestPath });
                PluginRegistry.Save(_registryPath, records);
                await NotifyRegistryChangedAsync(pluginId);
                return Result.Success();
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return Result.Failure("cancelled", "The plugin registry update was cancelled.");
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, exception.Message);
        }
    }

    private async Task<Result<PluginCatalogIndex>> FetchIndexAsync(CancellationToken cancellationToken)
    {
        try
        {
            byte[] content = await GetLimitedAsync(_catalogClient, _options.IndexUrl, MaxIndexBytes,
                cancellationToken);
            byte[] signatureBytes = await GetLimitedAsync(_catalogClient, _options.SignatureUrl,
                MaxSignatureBytes, cancellationToken);
            string signature = new UTF8Encoding(false, true).GetString(signatureBytes);
            Result<string> verified = PluginIndexSignature.Verify(content, signature, _options.PublicKeys);
            if (verified.IsFailure)
            {
                return Failure<PluginCatalogIndex>(verified.ErrorCode!, verified.ErrorMessage!);
            }

            Result<PluginCatalogIndex> parsed = PluginCatalogReader.Parse(content);
            if (parsed.IsSuccess)
            {
                WriteAtomic(_indexCachePath, content);
                WriteAtomic(_signatureCachePath, signatureBytes);
            }

            return parsed;
        }
        catch (OperationCanceledException)
        {
            return Failure<PluginCatalogIndex>("cancelled", "Refreshing the plugin catalog was cancelled.");
        }
        catch (Exception exception) when (IsExpected(exception) || exception is DecoderFallbackException)
        {
            return Failure<PluginCatalogIndex>("unavailable",
                $"The signed community index could not be downloaded: {exception.Message}");
        }
    }

    private Result<PluginCatalogIndex> LoadCachedIndex()
    {
        try
        {
            if (!File.Exists(_indexCachePath) || !File.Exists(_signatureCachePath) ||
                new FileInfo(_indexCachePath).Length > MaxIndexBytes ||
                new FileInfo(_signatureCachePath).Length > MaxSignatureBytes)
            {
                return Failure<PluginCatalogIndex>(AppErrorCodes.NotFound, "No bounded verified cache exists.");
            }

            byte[] content = File.ReadAllBytes(_indexCachePath);
            byte[] signatureBytes = File.ReadAllBytes(_signatureCachePath);
            Result<string> verified = PluginIndexSignature.Verify(content,
                new UTF8Encoding(false, true).GetString(signatureBytes), _options.PublicKeys);
            return verified.IsFailure
                ? Failure<PluginCatalogIndex>(verified.ErrorCode!, verified.ErrorMessage!)
                : PluginCatalogReader.Parse(content);
        }
        catch (Exception exception) when (IsExpected(exception) || exception is DecoderFallbackException)
        {
            return Failure<PluginCatalogIndex>(AppErrorCodes.ValidationFailed,
                $"The cached community index is invalid: {exception.Message}");
        }
    }

    private async Task<PluginStoreSnapshot> SnapshotAsync(PluginCatalogIndex index, bool fromCache,
        string? warning, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string> installedExtensions =
            _extensionInstalledVersions is null
                ? new Dictionary<string, string>()
                : await _extensionInstalledVersions(cancellationToken);
        PluginRegistrySnapshot registry = PluginRegistry.Load(_registryPath, true);
        List<PluginStoreItem> items = [];
        foreach (PluginCatalogEntry entry in index.Plugins.OrderBy(plugin => plugin.Name, StringComparer.Ordinal))
        {
            PluginRegistryEntry? current = FindRecord(registry.Entries, entry.Id) ??
                                           registry.Entries.FirstOrDefault(record =>
                                               ManagedRecordVersion(record,
                                                       Path.GetFullPath(Path.Combine(_pluginsRoot, entry.Id)))
                                                   is not null && record.Source == "community");
            Result<PluginDefinition> installed = current is null
                ? Failure<PluginDefinition>(AppErrorCodes.NotFound, "not installed")
                : PluginManifestLoader.Load(current.ManifestPath);
            string? installedVersion = installed.IsSuccess && installed.Value.Id == entry.Id
                ? installed.Value.Version
                : current is not null
                    ? ManagedRecordVersion(current,
                        Path.GetFullPath(Path.Combine(_pluginsRoot, entry.Id)))
                    : null;
            List<string> versions = InstalledVersions(entry.Id);
            items.Add(new PluginStoreItem(entry, installedVersion, current?.Enabled == true,
                current is not null && IsManagedRecord(current, entry.Id), versions,
                entry.ArtifactFor(_options.CurrentPlatform) is null
                    ? $"No {_options.CurrentPlatform} package is published."
                    : null));
        }

        List<ExtensionStoreItem> extensions = index.Extensions
            .OrderBy(extension => extension.Name, StringComparer.Ordinal)
            .Select(extension => new ExtensionStoreItem(extension,
                installedExtensions.TryGetValue(extension.Id, out string? installedVersion)
                    ? installedVersion
                    : null))
            .ToList();
        return new PluginStoreSnapshot(items, extensions, index.GeneratedAt, fromCache, warning);
    }

    private List<string> InstalledVersions(string pluginId)
    {
        string root = Path.Combine(_pluginsRoot, pluginId);
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.EnumerateDirectories(root).Select(directory =>
                PluginManifestLoader.Load(Path.Combine(directory, PluginManifestLoader.ManifestFileName)))
            .Where(result => result.IsSuccess && result.Value.Id == pluginId)
            .Select(result => result.Value.Version)
            .OrderByDescending(ExtensionVersion.Parse).ToList();
    }

    private async Task<Result> VerifyHandshakeAsync(PluginDefinition definition,
        CancellationToken cancellationToken)
    {
        PluginStdioClient client = new(new PluginProcessSpec(definition.Runtime.Command,
            definition.Runtime.Arguments, definition.PackageDirectory), _launcher, _clientOptions);
        await using (client)
        {
            return await client.StartAsync(cancellationToken);
        }
    }

    private PluginRegistryEntry? FindRecord(IEnumerable<PluginRegistryEntry> records, string pluginId)
    {
        foreach (PluginRegistryEntry record in records)
        {
            Result<PluginDefinition> loaded = PluginManifestLoader.Load(record.ManifestPath);
            if (loaded.IsSuccess && loaded.Value.Id == pluginId)
            {
                return record;
            }
        }

        return null;
    }

    private bool IsManagedRecord(PluginRegistryEntry record, string pluginId)
    {
        string root = Path.GetFullPath(Path.Combine(_pluginsRoot, pluginId));
        return record.Source == "community" && ManagedRecordVersion(record, root) is not null;
    }

    private static async Task<byte[]> GetLimitedAsync(HttpClient client, string url, int maximum,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} from {url}");
        }

        if (response.Content.Headers.ContentLength > maximum)
        {
            throw new InvalidDataException("The response exceeds the allowed size.");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using MemoryStream output = new();
        byte[] buffer = new byte[81920];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > maximum)
            {
                throw new InvalidDataException("The response exceeds the allowed size.");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private static void WriteAtomic(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, content);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void WriteInstallMetadata(string directory, PluginCatalogEntry entry)
    {
        string json = JsonSerializer.Serialize(new
        {
            repository = entry.Repository,
            source_revision = entry.SourceRevision
        });
        File.WriteAllText(Path.Combine(directory, ".patchouli-install.json"), json);
    }

    private static InstallMetadata ReadInstallMetadata(string directory)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(directory, ".patchouli-install.json")));
        JsonElement root = document.RootElement;
        string repository = root.GetProperty("repository").GetString() ?? "";
        string revision = root.GetProperty("source_revision").GetString() ?? "";
        if (repository.Length == 0 || revision.Length == 0)
        {
            throw new InvalidDataException("The rollback package has no valid installation provenance.");
        }

        return new InstallMetadata(repository, revision);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine($"Plugin cleanup failed for '{directory}': {exception.Message}");
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
    }

    private static bool IsExpected(Exception exception)
    {
        return exception is IOException or UnauthorizedAccessException or InvalidDataException
            or InvalidOperationException or HttpRequestException or JsonException or FormatException;
    }

    private static Result<T> Failure<T>(string code, string message)
    {
        return Result<T>.Failure(code, message);
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private sealed record InstallMetadata(string Repository, string SourceRevision);
}
