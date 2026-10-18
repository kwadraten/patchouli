using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Patchouli.UI;

namespace Patchouli.Host.Settings;

public interface IAppSettingsStore : IAsyncDisposable
{
    PatchouliAppSettings Current { get; }
    bool IsDirty { get; }
    event EventHandler<AppSettingsCommittedEventArgs>? SettingsCommitted;
    void Update(Func<PatchouliAppSettings, PatchouliAppSettings> updater, string? fieldCategory = null);

    Task<SettingsSaveResult> UpdateSyncBindingAsync(
        SyncAppSettings settings,
        DeviceRootBindingAppSettings binding,
        CancellationToken cancellationToken = default);

    Task<SettingsSaveResult> UpdateSyncBindingAndMetadataLookupAsync(
        SyncAppSettings settings,
        DeviceRootBindingAppSettings binding,
        MetadataLookupAppSettings materializedMetadataLookup,
        CancellationToken cancellationToken = default);

    Task<SettingsSaveResult> SaveImmediatelyAsync(CancellationToken cancellationToken = default);
    Task<SettingsSaveResult> FlushAsync(CancellationToken cancellationToken = default);
}

public sealed class AppSettingsCommittedEventArgs(
    PatchouliAppSettings snapshot,
    IReadOnlyCollection<string> categories,
    IReadOnlyDictionary<string, long> categoryRevisions,
    long commitSequence) : EventArgs
{
    public PatchouliAppSettings Snapshot { get; } = snapshot;
    public IReadOnlyCollection<string> Categories { get; } = categories;
    public IReadOnlyDictionary<string, long> CategoryRevisions { get; } = categoryRevisions;
    public long CommitSequence { get; } = commitSequence;
}
