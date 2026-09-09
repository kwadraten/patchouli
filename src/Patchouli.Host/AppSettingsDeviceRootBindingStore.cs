using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;

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

    public Task<Result<DeviceRootBinding>> SaveBindingAsync(
        DeviceRootBinding binding,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Result valid = ValidateBinding(binding);
        if (valid.IsFailure)
        {
            return Task.FromResult(Result<DeviceRootBinding>.Failure(valid.ErrorCode!, valid.ErrorMessage!));
        }

        PatchouliAppSettings settings = PatchouliAppSettings.Load(_settingsPath);
        DeviceRootBindingAppSettings row = DeviceRootBindingAppSettings.FromDeviceRootBinding(binding);
        SyncAppSettings sync = settings.Sync.WithDeviceBinding(row);
        SettingsSaveResult saved = (settings with { Sync = sync }).Save(_settingsPath);
        return Task.FromResult(saved.IsSuccess
            ? Result<DeviceRootBinding>.Success(row.ToDeviceRootBinding())
            : Result<DeviceRootBinding>.Failure(saved.ErrorCode ?? "settings_save_failed",
                saved.ErrorMessage ?? "Unable to save device root binding."));
    }

    public Task<Result> DeleteBindingAsync(
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
            return Task.FromResult(valid);
        }

        PatchouliAppSettings settings = PatchouliAppSettings.Load(_settingsPath);
        SettingsSaveResult saved = (settings with
        {
            Sync = settings.Sync.WithoutDeviceBinding(libraryId, rootKind.Trim(), logicalRootId.Trim(),
                deviceId.Trim())
        }).Save(_settingsPath);
        return Task.FromResult(saved.IsSuccess
            ? Result.Success()
            : Result.Failure(saved.ErrorCode ?? "settings_save_failed",
                saved.ErrorMessage ?? "Unable to remove device root binding."));
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
