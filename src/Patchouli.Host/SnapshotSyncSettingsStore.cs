using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Snapshots;
using System.Text.Json;
using System.Text.Json.Nodes;

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
            settings.Sync.Bindings.Select(binding => binding.ToDeviceRootBinding()).ToArray(),
            settings.Sync.SyncAgentSessions,
            settings.Sync.SyncWorkflowDefinitions));
    }

    public async Task<Result> SaveLocalStateAsync(
        SnapshotSyncLocalState state,
        CancellationToken cancellationToken = default)
    {
        Result<LibraryId> libraryId = await ReadCurrentLibraryIdAsync(cancellationToken);
        if (libraryId.IsFailure)
        {
            return Result.Failure(libraryId.ErrorCode!, libraryId.ErrorMessage!);
        }

        string? deviceId = null;
        bool updated = false;
        SettingsSaveResult saved = await PatchouliAppSettings.UpdateSyncJsonAsync(_settingsPath, sync =>
        {
            deviceId = TryReadString(sync, nameof(SyncAppSettings.DeviceId));
            if (sync["DeviceBindings"] is not JsonArray bindings)
            {
                return false;
            }

            JsonObject? selectedRow = null;
            foreach (JsonNode? item in bindings)
            {
                if (item is not JsonObject row || DeserializeBinding(row) is not { } binding ||
                    !binding.Matches(libraryId.Value.ToString(), LogicalRootKinds.SyncRoot, deviceId?.Trim() ?? ""))
                {
                    continue;
                }

                selectedRow = row;
                break;
            }

            if (selectedRow is not null)
            {
                selectedRow[nameof(DeviceRootBindingAppSettings.SnapshotState)] = JsonSerializer.SerializeToNode(state);
                updated = true;
                return true;
            }

            return false;
        }, cancellationToken);

        if (!saved.IsSuccess)
        {
            return Result.Failure(saved.ErrorCode ?? "settings_save_failed",
                saved.ErrorMessage ?? "Unable to save snapshot sync state.");
        }

        return updated
            ? Result.Success()
            : Result.Failure(AppErrorCodes.MappingRequired,
                $"sync_root for library '{libraryId.Value}' is not mapped by this device.",
                details: new MappingRequiredDetails(LogicalRootKinds.SyncRoot, "", libraryId.Value.ToString(),
                    deviceId ?? "", LogicalRootRecoveryActions.ChooseLocalSyncRoot));
    }

    private static DeviceRootBindingAppSettings? DeserializeBinding(JsonObject node)
    {
        try
        {
            return node.Deserialize<DeviceRootBindingAppSettings>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryReadString(JsonObject node, string name)
    {
        return node.TryGetPropertyValue(name, out JsonNode? value) && value is JsonValue jsonValue &&
               jsonValue.TryGetValue(out string? result)
            ? result
            : null;
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
