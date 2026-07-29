using System.Text.Json;
using Patchouli.Core.Results;
using Patchouli.Core.Settings;
using Patchouli.Core.Time;

namespace Patchouli.Infrastructure.Settings;

/// <summary>
/// Catalog-checked typed access to snapshot-owned non-secret settings. Local sync policy is supplied by the caller
/// so a disabled setting never reaches the underlying record store.
/// </summary>
public sealed class LibrarySettingRecordService
{
    private readonly ILibrarySettingStore _store;
    private readonly IClock _clock;

    public LibrarySettingRecordService(ILibrarySettingStore store, IClock clock)
    {
        _store = store;
        _clock = clock;
    }

    public async Task<Result<T?>> GetAsync<T>(
        string settingKey,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        Result<SettingCatalogEntry> catalog = RequireEnabledSnapshotSetting(settingKey, enabled);
        if (catalog.IsFailure)
        {
            return Result<T?>.Failure(catalog.ErrorCode!, catalog.ErrorMessage!);
        }

        Result<SettingRecord?> record = await _store.GetAsync(settingKey, cancellationToken);
        if (record.IsFailure)
        {
            return Result<T?>.Failure(record.ErrorCode!, record.ErrorMessage!);
        }

        if (record.Value is null)
        {
            return Result<T?>.Success(default);
        }

        try
        {
            return Result<T?>.Success(JsonSerializer.Deserialize<T>(record.Value.Value));
        }
        catch (JsonException exception)
        {
            return Result<T?>.Failure(AppErrorCodes.ValidationFailed,
                $"Library setting '{settingKey}' contains invalid JSON: {exception.Message}");
        }
    }

    public async Task<Result<SettingRecord>> SaveAsync<T>(
        string settingKey,
        T value,
        string deviceId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        Result<SettingCatalogEntry> catalog = RequireEnabledSnapshotSetting(settingKey, enabled);
        if (catalog.IsFailure)
        {
            return Result<SettingRecord>.Failure(catalog.ErrorCode!, catalog.ErrorMessage!);
        }

        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return Result<SettingRecord>.Failure(AppErrorCodes.ValidationFailed,
                "A device identity is required for synced settings.");
        }

        Result<SettingRecord?> current = await _store.GetAsync(settingKey, cancellationToken);
        if (current.IsFailure)
        {
            return Result<SettingRecord>.Failure(current.ErrorCode!, current.ErrorMessage!);
        }

        SettingCatalogEntry entry = catalog.Value;
        SettingRecord next = new(
            entry.SettingKey,
            entry.SchemaVersion,
            JsonSerializer.Serialize(value),
            (current.Value?.Revision ?? 0) + 1,
            _clock.UtcNow.ToUniversalTime(),
            deviceId.Trim(),
            entry.MergePolicy);
        Result saved = await _store.SaveAsync(next, cancellationToken);
        return saved.IsSuccess
            ? Result<SettingRecord>.Success(next)
            : Result<SettingRecord>.Failure(saved.ErrorCode!, saved.ErrorMessage!);
    }

    private static Result<SettingCatalogEntry> RequireEnabledSnapshotSetting(string settingKey, bool enabled)
    {
        if (!enabled)
        {
            return Result<SettingCatalogEntry>.Failure(AppErrorCodes.UnsupportedOperation,
                $"Library setting '{settingKey}' is disabled on this device.");
        }

        if (!LibrarySettingCatalog.TryGet(settingKey, out SettingCatalogEntry? entry) ||
            !entry.IsSnapshotEligible)
        {
            return Result<SettingCatalogEntry>.Failure(AppErrorCodes.UnsupportedOperation,
                $"Setting '{settingKey}' is not eligible for library snapshots.");
        }

        return Result<SettingCatalogEntry>.Success(entry);
    }
}
