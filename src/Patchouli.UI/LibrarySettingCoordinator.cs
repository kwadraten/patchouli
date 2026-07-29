using Patchouli.Core.Results;
using Patchouli.Core.Settings;
using Patchouli.Infrastructure.Settings;

namespace Patchouli.UI;

public sealed class LibrarySettingCoordinator
{
    private readonly ILibrarySettingStore _store;
    private readonly LibrarySettingRecordService _records;

    public LibrarySettingCoordinator(ILibrarySettingStore store, LibrarySettingRecordService records)
    {
        _store = store;
        _records = records;
    }

    public async Task<Result<T?>> ReadAsync<T>(
        string settingKey,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        return await _records.GetAsync<T>(settingKey, enabled, cancellationToken);
    }

    public async Task<SettingsSaveResult> SaveEnabledAsync<T>(
        string settingKey,
        T value,
        string deviceId,
        Func<CancellationToken, Task<SettingsSaveResult>> commitLocalPolicyAsync,
        Action<T>? applyEffectiveValue = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return SettingFailure(AppErrorCodes.ValidationFailed,
                "A device identity is required for synced settings.");
        }

        Result<SettingRecord?> previous = await _store.GetAsync(settingKey, cancellationToken);
        if (previous.IsFailure)
        {
            return SettingFailure(previous.ErrorCode, previous.ErrorMessage);
        }

        Result<SettingRecord> saved = await _records.SaveAsync(settingKey, value, deviceId, true, cancellationToken);
        if (saved.IsFailure)
        {
            return SettingFailure(saved.ErrorCode, saved.ErrorMessage);
        }

        SettingsSaveResult policy = await commitLocalPolicyAsync(cancellationToken);
        if (!policy.IsSuccess)
        {
            await RestorePreviousAsync(settingKey, previous.Value, cancellationToken);
            return policy;
        }

        applyEffectiveValue?.Invoke(value);
        return SettingsSaveResult.Success;
    }

    public async Task<SettingsSaveResult> DisableAndMaterializeAsync<T>(
        string settingKey,
        bool enabled,
        T localFallback,
        Func<T, CancellationToken, Task<SettingsSaveResult>> commitLocalPolicyAsync,
        Action<T>? applyEffectiveValue = null,
        CancellationToken cancellationToken = default)
    {
        if (!enabled)
        {
            SettingsSaveResult saved = await commitLocalPolicyAsync(localFallback, cancellationToken);
            if (saved.IsSuccess)
            {
                applyEffectiveValue?.Invoke(localFallback);
            }

            return saved;
        }

        Result<T?> record = await _records.GetAsync<T>(settingKey, true, cancellationToken);
        if (record.IsFailure)
        {
            return SettingFailure(record.ErrorCode, record.ErrorMessage);
        }

        Result<SettingRecord?> previous = await _store.GetAsync(settingKey, cancellationToken);
        if (previous.IsFailure)
        {
            return SettingFailure(previous.ErrorCode, previous.ErrorMessage);
        }

        T materialized = record.Value is null ? localFallback : record.Value;
        Result deleted = await _store.DeleteAsync(settingKey, cancellationToken);
        if (deleted.IsFailure)
        {
            return SettingFailure(deleted.ErrorCode, deleted.ErrorMessage);
        }

        SettingsSaveResult policy = await commitLocalPolicyAsync(materialized, cancellationToken);
        if (!policy.IsSuccess)
        {
            await RestorePreviousAsync(settingKey, previous.Value, cancellationToken);
            return policy;
        }

        applyEffectiveValue?.Invoke(materialized);
        return SettingsSaveResult.Success;
    }

    private async Task RestorePreviousAsync(
        string settingKey,
        SettingRecord? previous,
        CancellationToken cancellationToken)
    {
        Result restored = previous is null
            ? await _store.DeleteAsync(settingKey, cancellationToken)
            : await _store.SaveAsync(previous, cancellationToken);
        _ = restored;
    }

    private static SettingsSaveResult SettingFailure(string? errorCode, string? errorMessage)
    {
        return new SettingsSaveResult(
            false,
            errorCode ?? AppErrorCodes.DatabaseError,
            errorMessage ?? "Unable to update the synchronized setting.",
            "library_setting_record",
            true);
    }
}
