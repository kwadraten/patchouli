using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Patchouli.Llm;
using Patchouli.UI;

namespace Patchouli.Host.Settings;

public sealed class AppSettingsStore : IAppSettingsStore
{
    private readonly string _settingsPath;
    private readonly TimeSpan _debounceInterval;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _stateLock = new();

    private PatchouliAppSettings _current;
    private PatchouliAppSettings _committed;
    private long _currentRevision;
    private long _allFieldsRevision;
    private long _commitSequence;
    private readonly Dictionary<string, long> _categoryRevisions = new(StringComparer.OrdinalIgnoreCase);

    private long _persistedRevision;
    private readonly Dictionary<string, long> _persistedCategoryRevisions = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _debounceCts;
    private Task? _debounceTask;
    private bool _disposed;

    public AppSettingsStore(
        string? settingsPath = null,
        PatchouliAppSettings? initialSettings = null,
        TimeSpan? debounceInterval = null,
        TimeProvider? timeProvider = null)
    {
        _settingsPath = PatchouliAppSettings.ResolvePath(settingsPath);
        _debounceInterval = debounceInterval ?? TimeSpan.FromMilliseconds(300);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _current = initialSettings ?? PatchouliAppSettings.Load(_settingsPath);
        _committed = _current;
    }

    public event EventHandler<AppSettingsCommittedEventArgs>? SettingsCommitted;

    public PatchouliAppSettings Current
    {
        get
        {
            lock (_stateLock)
            {
                return _current;
            }
        }
    }

    public bool IsDirty
    {
        get
        {
            lock (_stateLock)
            {
                return IsDirtyLocked();
            }
        }
    }

    private bool IsDirtyLocked()
    {
        if (_allFieldsRevision > _persistedRevision)
        {
            return true;
        }

        foreach ((string category, long revision) in _categoryRevisions)
        {
            if (!_persistedCategoryRevisions.TryGetValue(category, out long persisted) || revision > persisted)
            {
                return true;
            }
        }

        return false;
    }

    public void Update(Func<PatchouliAppSettings, PatchouliAppSettings> updater, string? fieldCategory = null)
    {
        ArgumentNullException.ThrowIfNull(updater);

        CancellationTokenSource nextCts;
        CancellationToken nextToken;

        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _current = updater(_current);
            _currentRevision++;
            if (string.IsNullOrWhiteSpace(fieldCategory))
            {
                _allFieldsRevision = _currentRevision;
            }
            else
            {
                _categoryRevisions[fieldCategory] = _currentRevision;
            }

            // Cancel any pending debounce timer without disposing CTS while token might still be registered
            _debounceCts?.Cancel();

            nextCts = new CancellationTokenSource();
            _debounceCts = nextCts;
            nextToken = nextCts.Token;

            _debounceTask = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(_debounceInterval, _timeProvider, nextToken).ConfigureAwait(false);
                    await PersistDirtyCoreAsync(nextToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Debounce cancelled by subsequent update, explicit save, flush, or dispose
                }
                catch (Exception exception)
                {
                    // Failed write preserves dirty state for an explicit retry or shutdown flush.
                    System.Diagnostics.Debug.WriteLine($"Deferred settings save failed: {exception}");
                }
            }, nextToken);
        }
    }

    public async Task<SettingsSaveResult> SaveImmediatelyAsync(CancellationToken cancellationToken = default)
    {
        Task? pendingDebounceTask;
        lock (_stateLock)
        {
            _debounceCts?.Cancel();
            _debounceCts = null;
            pendingDebounceTask = _debounceTask;
            _debounceTask = null;
        }

        if (pendingDebounceTask is not null)
        {
            try
            {
                await pendingDebounceTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        return await PersistDirtyCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<SettingsSaveResult> UpdateSyncBindingAsync(
        SyncAppSettings settings,
        DeviceRootBindingAppSettings binding,
        CancellationToken cancellationToken = default)
    {
        return UpdateSyncBindingCoreAsync(settings, binding, null, cancellationToken);
    }

    public Task<SettingsSaveResult> UpdateSyncBindingAndMetadataLookupAsync(
        SyncAppSettings settings,
        DeviceRootBindingAppSettings binding,
        MetadataLookupAppSettings materializedMetadataLookup,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(materializedMetadataLookup);
        return UpdateSyncBindingCoreAsync(settings, binding, materializedMetadataLookup, cancellationToken);
    }

    private async Task<SettingsSaveResult> UpdateSyncBindingCoreAsync(
        SyncAppSettings settings,
        DeviceRootBindingAppSettings binding,
        MetadataLookupAppSettings? materializedMetadataLookup,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(binding);

        long startingSyncRevision;
        long startingMetadataLookupRevision;
        SyncAppSettings startingSync;
        MetadataLookupAppSettings startingMetadataLookup;
        lock (_stateLock)
        {
            if (_disposed)
            {
                return SettingsSaveResult.Success;
            }

            startingSyncRevision = _categoryRevisions.GetValueOrDefault("Sync");
            startingMetadataLookupRevision = _categoryRevisions.GetValueOrDefault("MetadataLookup");
            startingSync = _current.Sync;
            startingMetadataLookup = _current.MetadataLookup;
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        AppSettingsCommittedEventArgs? notification = null;
        SettingsSaveResult result;
        try
        {
            result = await PatchouliAppSettings.SaveSyncBindingAsync(
                _settingsPath, settings, binding, cancellationToken,
                materializedMetadataLookup).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                return result;
            }

            PatchouliAppSettings persistedSnapshot = PatchouliAppSettings.Load(_settingsPath);
            lock (_stateLock)
            {
                bool syncChanged = !SettingsSectionEqual(_committed.Sync, persistedSnapshot.Sync);
                bool metadataLookupChanged = materializedMetadataLookup is not null &&
                                             !SettingsSectionEqual(_committed.MetadataLookup,
                                                 persistedSnapshot.MetadataLookup);
                bool noConcurrentSyncEdits =
                    _categoryRevisions.GetValueOrDefault("Sync") == startingSyncRevision &&
                    SettingsSectionEqual(_current.Sync, startingSync);
                bool noConcurrentMetadataLookupEdits = materializedMetadataLookup is not null &&
                                                       _categoryRevisions.GetValueOrDefault("MetadataLookup") ==
                                                       startingMetadataLookupRevision &&
                                                       SettingsSectionEqual(_current.MetadataLookup,
                                                           startingMetadataLookup);
                _current = MergePersistedSyncState(_current, persistedSnapshot);
                _committed = _committed with
                {
                    Sync = persistedSnapshot.Sync,
                    MetadataLookup = materializedMetadataLookup is null
                        ? _committed.MetadataLookup
                        : persistedSnapshot.MetadataLookup
                };
                long syncCommittedRevision;
                if (noConcurrentSyncEdits)
                {
                    syncCommittedRevision = ++_currentRevision;
                    _categoryRevisions["Sync"] = syncCommittedRevision;
                    _current = _current with { Sync = persistedSnapshot.Sync };
                    _persistedCategoryRevisions["Sync"] = syncCommittedRevision;
                }
                else
                {
                    syncCommittedRevision = startingSyncRevision;
                    _persistedCategoryRevisions["Sync"] = Math.Max(
                        _persistedCategoryRevisions.GetValueOrDefault("Sync", 0), startingSyncRevision);
                }

                long metadataLookupCommittedRevision = startingMetadataLookupRevision;
                if (materializedMetadataLookup is not null)
                {
                    if (noConcurrentMetadataLookupEdits)
                    {
                        metadataLookupCommittedRevision = ++_currentRevision;
                        _categoryRevisions["MetadataLookup"] = metadataLookupCommittedRevision;
                        _current = _current with { MetadataLookup = persistedSnapshot.MetadataLookup };
                        _persistedCategoryRevisions["MetadataLookup"] = metadataLookupCommittedRevision;
                    }
                    else
                    {
                        _persistedCategoryRevisions["MetadataLookup"] = Math.Max(
                            _persistedCategoryRevisions.GetValueOrDefault("MetadataLookup", 0),
                            startingMetadataLookupRevision);
                    }
                }

                List<string> changedCategories = [];
                Dictionary<string, long> categoryRevisions = new(StringComparer.OrdinalIgnoreCase);
                if (syncChanged)
                {
                    changedCategories.Add("Sync");
                    categoryRevisions["Sync"] = syncCommittedRevision;
                }

                if (metadataLookupChanged)
                {
                    changedCategories.Add("MetadataLookup");
                    categoryRevisions["MetadataLookup"] = metadataLookupCommittedRevision;
                }

                if (changedCategories.Count > 0)
                {
                    notification = new AppSettingsCommittedEventArgs(
                        persistedSnapshot, changedCategories, categoryRevisions, ++_commitSequence);
                }
            }
        }
        finally
        {
            _writeGate.Release();
        }

        PublishCommitted(notification);
        return result;
    }

    public async Task<SettingsSaveResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        Task? pendingDebounceTask;
        lock (_stateLock)
        {
            _debounceCts?.Cancel();
            _debounceCts = null;
            pendingDebounceTask = _debounceTask;
            _debounceTask = null;

            if (!IsDirtyLocked() && pendingDebounceTask is null)
            {
                return SettingsSaveResult.Success;
            }
        }

        if (pendingDebounceTask is not null)
        {
            try
            {
                await pendingDebounceTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        return await PersistDirtyCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SettingsSaveResult> PersistDirtyCoreAsync(CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        AppSettingsCommittedEventArgs? notification = null;
        SettingsSaveResult result;
        try
        {
            PatchouliAppSettings snapshot;
            HashSet<string>? sectionsToPersist = null;
            long snapshotRevision;
            long snapshotAllFieldsRevision;
            Dictionary<string, long> snapshotCategoryRevisions;

            lock (_stateLock)
            {
                if (!IsDirtyLocked())
                {
                    return SettingsSaveResult.Success;
                }

                snapshot = _current;
                snapshotRevision = _currentRevision;
                snapshotAllFieldsRevision = _allFieldsRevision;
                snapshotCategoryRevisions =
                    new Dictionary<string, long>(_categoryRevisions, StringComparer.OrdinalIgnoreCase);

                if (snapshotAllFieldsRevision > _persistedRevision)
                {
                    // Sentinel is dirty: persist all sections
                    sectionsToPersist = null;
                }
                else
                {
                    sectionsToPersist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach ((string category, long revision) in snapshotCategoryRevisions)
                    {
                        if (!_persistedCategoryRevisions.TryGetValue(category, out long persisted) ||
                            revision > persisted)
                        {
                            sectionsToPersist.Add(category);
                        }
                    }

                    if (sectionsToPersist.Count == 0)
                    {
                        return SettingsSaveResult.Success;
                    }
                }
            }

            result = await snapshot.SaveFieldLevelAsync(
                _settingsPath,
                sectionsToPersist,
                cancellationToken).ConfigureAwait(false);

            if (result.IsSuccess)
            {
                // Field-level writes preserve sections owned by the device-root and snapshot stores.
                // Reload them after the atomic write so a later UI save cannot start from a stale Sync section.
                PatchouliAppSettings persistedSnapshot = PatchouliAppSettings.Load(_settingsPath);
                lock (_stateLock)
                {
                    string[] changedCategories = GetChangedCategories(
                        _committed, persistedSnapshot, sectionsToPersist);
                    _committed = persistedSnapshot;
                    _current = MergePersistedSyncState(_current, persistedSnapshot);
                    if (sectionsToPersist is null)
                    {
                        if (_currentRevision == snapshotRevision &&
                            _allFieldsRevision == snapshotAllFieldsRevision)
                        {
                            _current = persistedSnapshot;
                        }
                    }
                    else
                    {
                        foreach (string category in sectionsToPersist)
                        {
                            if (snapshotCategoryRevisions.TryGetValue(category, out long savedRevision) &&
                                _categoryRevisions.GetValueOrDefault(category) == savedRevision)
                            {
                                _current = CopySettingsCategory(_current, persistedSnapshot, category);
                            }
                        }
                    }

                    if (sectionsToPersist is null)
                    {
                        // All sections were persisted up to snapshotRevision
                        _persistedRevision = Math.Max(_persistedRevision, snapshotRevision);
                        foreach ((string category, long revision) in snapshotCategoryRevisions)
                        {
                            _persistedCategoryRevisions[category] = Math.Max(
                                _persistedCategoryRevisions.GetValueOrDefault(category, 0),
                                revision);
                        }
                    }
                    else
                    {
                        foreach (string category in sectionsToPersist)
                        {
                            if (snapshotCategoryRevisions.TryGetValue(category, out long rev))
                            {
                                _persistedCategoryRevisions[category] = Math.Max(
                                    _persistedCategoryRevisions.GetValueOrDefault(category, 0),
                                    rev);
                            }
                        }
                    }

                    if (changedCategories.Length > 0)
                    {
                        Dictionary<string, long> categoryRevisions = changedCategories.ToDictionary(
                            category => category,
                            category => sectionsToPersist is null
                                ? snapshotRevision
                                : snapshotCategoryRevisions.GetValueOrDefault(category),
                            StringComparer.OrdinalIgnoreCase);
                        notification = new AppSettingsCommittedEventArgs(
                            persistedSnapshot, changedCategories, categoryRevisions, ++_commitSequence);
                    }
                }
            }
        }
        finally
        {
            _writeGate.Release();
        }

        PublishCommitted(notification);
        return result;
    }

    private static string[] GetChangedCategories(
        PatchouliAppSettings previous,
        PatchouliAppSettings committed,
        HashSet<string>? sectionsToPersist)
    {
        List<string> categories = [];
        AddChangedCategory(categories, "Runtime", previous.Runtime, committed.Runtime, sectionsToPersist);
        AddChangedCategory(categories, "MinerU", previous.MinerU, committed.MinerU, sectionsToPersist);
        AddChangedCategory(categories, "Mcp", previous.Mcp, committed.Mcp, sectionsToPersist);
        AddChangedCategory(categories, "Ui", previous.Ui, committed.Ui, sectionsToPersist);
        AddChangedCategory(categories, "Credentials", previous.Credentials, committed.Credentials, sectionsToPersist);
        AddChangedCategory(categories, "OcrEngines", previous.OcrEngines, committed.OcrEngines, sectionsToPersist);
        AddChangedCategory(categories, "Import", previous.Import, committed.Import, sectionsToPersist);
        LlmAppSettings previousLlm = previous.Llm.Normalize();
        LlmAppSettings committedLlm = committed.Llm.Normalize();
        bool llmEqual = previousLlm.Providers.SequenceEqual(committedLlm.Providers) &&
                        previousLlm with { Providers = committedLlm.Providers } == committedLlm;
        if (IsSectionPersisted("Llm", sectionsToPersist) && !llmEqual)
        {
            categories.Add("Llm");
        }

        if (IsSectionPersisted("MetadataLookup", sectionsToPersist) &&
            !previous.MetadataLookup.Sources.SequenceEqual(committed.MetadataLookup.Sources))
        {
            categories.Add("MetadataLookup");
        }

        if (IsSectionPersisted("FileScanning", sectionsToPersist) &&
            !previous.FileScanning.ExclusionPatterns.SequenceEqual(
                committed.FileScanning.ExclusionPatterns, StringComparer.Ordinal))
        {
            categories.Add("FileScanning");
        }

        if (IsSectionPersisted("Sync", sectionsToPersist) &&
            !SettingsSectionEqual(previous.Sync, committed.Sync))
        {
            categories.Add("Sync");
        }

        return categories.ToArray();
    }

    private static bool IsSectionPersisted(string category, HashSet<string>? sectionsToPersist)
    {
        return sectionsToPersist is null || sectionsToPersist.Contains(category);
    }

    private static void AddChangedCategory<T>(
        ICollection<string> categories,
        string category,
        T previous,
        T committed,
        HashSet<string>? sectionsToPersist)
    {
        if (IsSectionPersisted(category, sectionsToPersist) && !SettingsSectionEqual(previous, committed))
        {
            categories.Add(category);
        }
    }

    private static PatchouliAppSettings MergePersistedSyncState(
        PatchouliAppSettings current,
        PatchouliAppSettings persisted)
    {
        return current with
        {
            Sync = current.Sync with
            {
                DeviceBindings = persisted.Sync.DeviceBindings,
                SnapshotState = persisted.Sync.SnapshotState,
                SyncedSettingKeys = persisted.Sync.SyncedSettingKeys
            }
        };
    }

    private static PatchouliAppSettings CopySettingsCategory(
        PatchouliAppSettings current,
        PatchouliAppSettings persisted,
        string category)
    {
        return category switch
        {
            "Runtime" => current with { Runtime = persisted.Runtime },
            "MinerU" => current with { MinerU = persisted.MinerU },
            "Mcp" => current with { Mcp = persisted.Mcp },
            "Ui" => current with { Ui = persisted.Ui },
            "Credentials" => current with { Credentials = persisted.Credentials },
            "OcrEngines" => current with { OcrEngines = persisted.OcrEngines },
            "Import" => current with { Import = persisted.Import },
            "Llm" => current with { Llm = persisted.Llm },
            "MetadataLookup" => current with { MetadataLookup = persisted.MetadataLookup },
            "FileScanning" => current with { FileScanning = persisted.FileScanning },
            "Sync" => current with { Sync = persisted.Sync },
            _ => current
        };
    }

    private static bool SettingsSectionEqual<T>(T first, T second)
    {
        return JsonSerializer.Serialize(first) == JsonSerializer.Serialize(second);
    }

    private void PublishCommitted(AppSettingsCommittedEventArgs? notification)
    {
        if (notification is null || SettingsCommitted is not { } handlers)
        {
            return;
        }

        foreach (EventHandler<AppSettingsCommittedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, notification);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Settings committed notification failed: {exception}");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? pendingDebounceTask;
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _debounceCts?.Cancel();
            _debounceCts = null;
            pendingDebounceTask = _debounceTask;
            _debounceTask = null;
        }

        if (pendingDebounceTask is not null)
        {
            try
            {
                await pendingDebounceTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        try
        {
            await PersistDirtyCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Shutdown cannot surface a dialog here; preserve dirty state and retain diagnostics.
            System.Diagnostics.Debug.WriteLine($"Final settings flush failed: {exception}");
        }
        finally
        {
            _writeGate.Dispose();
        }
    }
}
