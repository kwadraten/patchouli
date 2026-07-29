using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Snapshots;

namespace Patchouli.UI;

/// <summary>
/// Adapter from the device-owned JSON settings lifecycle to the snapshot coordinator. Only the binding and its
/// operational state are read or written here; credentials, app-local preferences, and device authorization payloads
/// remain outside snapshot content.
/// </summary>
public sealed class SnapshotSyncSettingsStore : ISnapshotSyncBindingStore
{
    private readonly string _runtimeDatabasePath;
    private readonly string _settingsPath;
    private readonly string _stagingRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SnapshotSyncSettingsStore(string runtimeDatabasePath, string settingsPath, string stagingRoot)
    {
        _runtimeDatabasePath = runtimeDatabasePath;
        _settingsPath = settingsPath;
        _stagingRoot = stagingRoot;
    }

    public async Task<Result<SnapshotSyncBinding>> GetBindingAsync(CancellationToken cancellationToken = default)
    {
        PatchouliAppSettings settings = PatchouliAppSettings.Load(_settingsPath);
        Result<LibraryId> libraryId = await ReadCurrentLibraryIdAsync(cancellationToken);
        if (libraryId.IsFailure)
        {
            return Result<SnapshotSyncBinding>.Failure(libraryId.ErrorCode!, libraryId.ErrorMessage!);
        }

        DeviceRootBindingAppSettings? syncRoot = settings.Sync.CurrentSyncRootBinding(libraryId.Value);
        SnapshotSyncLocalState state = syncRoot?.SnapshotState ?? SnapshotSyncLocalState.NotConfigured;
        return Result<SnapshotSyncBinding>.Success(new SnapshotSyncBinding(
            _runtimeDatabasePath,
            syncRoot?.LogicalRootId ?? "",
            syncRoot?.LocalPath ?? "",
            _stagingRoot,
            settings.Sync.DeviceId,
            state,
            settings.Sync.EnabledSettingKeysForLibrary(libraryId.Value),
            settings.Sync.Bindings.Select(binding => binding.ToDeviceRootBinding()).ToArray()));
    }

    public async Task<Result> SaveLocalStateAsync(
        SnapshotSyncLocalState state,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            PatchouliAppSettings settings = PatchouliAppSettings.Load(_settingsPath);
            Result<LibraryId> libraryId = await ReadCurrentLibraryIdAsync(cancellationToken);
            if (libraryId.IsFailure)
            {
                return Result.Failure(libraryId.ErrorCode!, libraryId.ErrorMessage!);
            }

            DeviceRootBindingAppSettings? syncRoot = settings.Sync.CurrentSyncRootBinding(libraryId.Value);
            if (syncRoot is null)
            {
                return Result.Failure(AppErrorCodes.MappingRequired,
                    $"sync_root for library '{libraryId.Value}' is not mapped by this device.",
                    details: new MappingRequiredDetails(LogicalRootKinds.SyncRoot, "", libraryId.Value.ToString(),
                        settings.Sync.DeviceId, LogicalRootRecoveryActions.ChooseLocalSyncRoot));
            }

            SettingsSaveResult saved = (settings with
            {
                Sync = settings.Sync.WithDeviceBinding(syncRoot with { SnapshotState = state })
            }).Save(_settingsPath);
            return saved.IsSuccess
                ? Result.Success()
                : Result.Failure(saved.ErrorCode ?? "settings_save_failed",
                    saved.ErrorMessage ?? "Unable to save snapshot sync state.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Result<LibraryId>> ReadCurrentLibraryIdAsync(CancellationToken cancellationToken)
    {
        try
        {
            string libraryId = await SnapshotPublisher.ReadLibraryIdAsync(_runtimeDatabasePath);
            return Result<LibraryId>.Success(LibraryId.Parse(libraryId));
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or FormatException)
        {
            return Result<LibraryId>.Failure(AppErrorCodes.DatabaseError,
                $"Unable to read the current library identity for sync binding lookup: {exception.Message}");
        }
    }
}
