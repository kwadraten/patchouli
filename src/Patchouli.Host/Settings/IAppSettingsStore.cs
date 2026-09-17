using System;
using System.Threading;
using System.Threading.Tasks;
using Patchouli.UI;

namespace Patchouli.Host.Settings;

public interface IAppSettingsStore : IAsyncDisposable
{
    PatchouliAppSettings Current { get; }
    bool IsDirty { get; }
    void Update(Func<PatchouliAppSettings, PatchouliAppSettings> updater, string? fieldCategory = null);
    Task<SettingsSaveResult> SaveImmediatelyAsync(CancellationToken cancellationToken = default);
    Task<SettingsSaveResult> FlushAsync(CancellationToken cancellationToken = default);
}
