using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    private long _currentRevision;
    private long _allFieldsRevision;
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
    }

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

            SettingsSaveResult result = await snapshot.SaveFieldLevelAsync(
                _settingsPath,
                sectionsToPersist,
                cancellationToken).ConfigureAwait(false);

            if (result.IsSuccess)
            {
                lock (_stateLock)
                {
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
                }
            }

            return result;
        }
        finally
        {
            _writeGate.Release();
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
