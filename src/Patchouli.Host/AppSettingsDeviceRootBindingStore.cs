using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Patchouli.UI;

public sealed class AppSettingsDeviceRootBindingStore : IDeviceRootBindingStore
{
    private readonly string _settingsPath;

    public AppSettingsDeviceRootBindingStore(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    public Task<Result<string>> GetDeviceIdAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        string deviceId = PatchouliAppSettings.Load(_settingsPath).Sync.DeviceId.Trim();
        return Task.FromResult(string.IsNullOrWhiteSpace(deviceId)
            ? Result<string>.Failure(AppErrorCodes.ValidationFailed,
                "A device identity is required for device-local root bindings.")
            : Result<string>.Success(deviceId));
    }

    public Task<Result<DeviceRootBinding?>> GetBindingAsync(
        LibraryId libraryId,
        string rootKind,
        string logicalRootId,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Result valid = ValidateKey(rootKind, logicalRootId, deviceId);
        if (valid.IsFailure)
        {
            return Task.FromResult(Result<DeviceRootBinding?>.Failure(valid.ErrorCode!, valid.ErrorMessage!));
        }

        DeviceRootBindingAppSettings? binding = PatchouliAppSettings.Load(_settingsPath).Sync.Bindings
            .FirstOrDefault(candidate => candidate.Matches(libraryId.ToString(), rootKind.Trim(),
                logicalRootId.Trim(), deviceId.Trim()));
        return Task.FromResult(Result<DeviceRootBinding?>.Success(binding?.ToDeviceRootBinding()));
    }

    public Task<Result<IReadOnlyList<DeviceRootBinding>>> ListBindingsAsync(
        LibraryId? libraryId = null,
        string? rootKind = null,
        string? deviceId = null,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        DeviceRootBinding[] bindings = PatchouliAppSettings.Load(_settingsPath).Sync.Bindings
            .Where(binding => binding.Matches(libraryId?.ToString(), rootKind, deviceId))
            .Select(binding => binding.ToDeviceRootBinding())
            .ToArray();
        return Task.FromResult(Result<IReadOnlyList<DeviceRootBinding>>.Success(bindings));
    }

    public async Task<Result<DeviceRootBinding>> SaveBindingAsync(
        DeviceRootBinding binding,
        CancellationToken cancellationToken = default)
    {
        Result valid = ValidateBinding(binding);
        if (valid.IsFailure)
        {
            return Result<DeviceRootBinding>.Failure(valid.ErrorCode!, valid.ErrorMessage!);
        }

        DeviceRootBindingAppSettings row = DeviceRootBindingAppSettings.FromDeviceRootBinding(binding);
        SettingsSaveResult saved = await PatchouliAppSettings.UpdateSyncJsonAsync(_settingsPath,
            sync =>
            {
                PatchouliAppSettings.UpsertDeviceBindingJson(sync, row);
                return true;
            }, cancellationToken);
        return saved.IsSuccess
            ? Result<DeviceRootBinding>.Success(row.ToDeviceRootBinding())
            : Result<DeviceRootBinding>.Failure(saved.ErrorCode ?? "settings_save_failed",
                saved.ErrorMessage ?? "Unable to save device root binding.");
    }

    public async Task<Result> DeleteBindingAsync(
        LibraryId libraryId,
        string rootKind,
        string logicalRootId,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        Result valid = ValidateKey(rootKind, logicalRootId, deviceId);
        if (valid.IsFailure)
        {
            return valid;
        }

        SettingsSaveResult saved = await PatchouliAppSettings.UpdateSyncJsonAsync(_settingsPath, sync =>
        {
            if (sync["DeviceBindings"] is not JsonArray bindings)
            {
                return false;
            }

            bool removed = false;
            for (int i = bindings.Count - 1; i >= 0; i--)
            {
                if (bindings[i] is JsonObject existingNode && DeserializeBinding(existingNode) is { } existing &&
                    existing.Matches(libraryId.ToString(), rootKind.Trim(), logicalRootId.Trim(), deviceId.Trim()))
                {
                    bindings.RemoveAt(i);
                    removed = true;
                }
            }

            return removed;
        }, cancellationToken);
        return saved.IsSuccess
            ? Result.Success()
            : Result.Failure(saved.ErrorCode ?? "settings_save_failed",
                saved.ErrorMessage ?? "Unable to remove device root binding.");
    }

    private static Result ValidateBinding(DeviceRootBinding binding)
    {
        Result key = ValidateKey(binding.RootKind, binding.LogicalRootId, binding.DeviceId);
        if (key.IsFailure)
        {
            return key;
        }

        if (string.IsNullOrWhiteSpace(binding.LocalPath))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "A device binding local path is required.");
        }

        if (string.IsNullOrWhiteSpace(binding.ProviderIdentity))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed,
                "A device binding provider identity is required.");
        }

        return Result.Success();
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

    private static Result ValidateKey(string rootKind, string logicalRootId, string deviceId)
    {
        if (!LogicalRootKinds.IsKnown(rootKind.Trim()))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "A known logical root kind is required.");
        }

        return string.IsNullOrWhiteSpace(logicalRootId) || string.IsNullOrWhiteSpace(deviceId)
            ? Result.Failure(AppErrorCodes.ValidationFailed,
                "A logical root id and device id are required for a device binding.")
            : Result.Success();
    }
}
