using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Ocr;
using Patchouli.UI;

namespace Patchouli.Host;

/// <summary>
///     Resolves the per-library/per-scope OCR preset choice: stored selections are validated
///     through <see cref="IOcrPresetService.ResolveSelectionAsync" />, so a missing, archived, or
///     cross-library id surfaces an actionable error instead of silently picking another preset.
///     When no selection is stored, callers supply the implicit default (engine settings
///     migration path).
/// </summary>
public sealed class OcrPresetSelectionService(
    OcrPresetSelectionStore store,
    IOcrPresetService presets,
    ILibraryIdentityService library)
{
    /// <summary>
    ///     The stored selection for one scope, resolved library-safely. Success + null means no
    ///     explicit choice is stored; a stored-but-unusable id fails (no fallback).
    /// </summary>
    public async Task<Result<OcrPresetId?>> ResolveStoredSelectionAsync(OcrScope scope,
        CancellationToken cancellationToken = default)
    {
        Result<LibraryMetadata> current = await library.GetCurrentLibraryAsync(cancellationToken);
        if (current.IsFailure)
        {
            return Result<OcrPresetId?>.Failure(current.ErrorCode!, current.ErrorMessage!);
        }

        Result<OcrPresetSelections> stored =
            await store.ReadAsync(current.Value.LibraryId, cancellationToken);
        if (stored.IsFailure)
        {
            return Result<OcrPresetId?>.Failure(stored.ErrorCode!, stored.ErrorMessage!);
        }

        string? raw = stored.Value.PresetIdFor(scope);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Result<OcrPresetId?>.Success(null);
        }

        if (!Guid.TryParse(raw, out Guid guid))
        {
            return Result<OcrPresetId?>.Failure(AppErrorCodes.ValidationFailed,
                $"Stored {scope} OCR preset id '{raw}' is not a preset UUID; re-select a preset.");
        }

        Result<OcrPreset> preset = await presets.ResolveSelectionAsync(new OcrPresetId(guid),
            cancellationToken);
        return preset.IsFailure
            ? Result<OcrPresetId?>.Failure(preset.ErrorCode!, preset.ErrorMessage!)
            : Result<OcrPresetId?>.Success(preset.Value.PresetId);
    }

    /// <summary>
    ///     The raw stored selections for the active library — including ids that may no longer
    ///     resolve — so settings screens can display the user's choice even when the preset row
    ///     is gone. <see cref="ResolveStoredSelectionAsync" /> remains the validation path.
    /// </summary>
    public async Task<Result<OcrPresetSelections>> ReadStoredSelectionsAsync(
        CancellationToken cancellationToken = default)
    {
        Result<LibraryMetadata> current = await library.GetCurrentLibraryAsync(cancellationToken);
        return current.IsFailure
            ? Result<OcrPresetSelections>.Failure(current.ErrorCode!, current.ErrorMessage!)
            : await store.ReadAsync(current.Value.LibraryId, cancellationToken);
    }

    /// <summary>
    ///     Stored selection wins; without one the caller's implicit-default delegate runs
    ///     (typically <c>OcrDefaults.EnsurePresetAsync(engineForScope)</c>).
    /// </summary>
    public async Task<Result<OcrPresetId>> ResolveForScopeAsync(OcrScope scope,
        Func<CancellationToken, Task<Result<OcrPresetId>>> ensureDefault,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ensureDefault);
        Result<OcrPresetId?> stored = await ResolveStoredSelectionAsync(scope, cancellationToken);
        if (stored.IsFailure)
        {
            return Result<OcrPresetId>.Failure(stored.ErrorCode!, stored.ErrorMessage!);
        }

        return stored.Value is { } presetId
            ? Result<OcrPresetId>.Success(presetId)
            : await ensureDefault(cancellationToken);
    }

    /// <summary>
    ///     Persists one scope's choice for the active library. Passing null clears the scope back
    ///     to the implicit default; an explicit id is validated before it is stored so a
    ///     cross-library or archived preset can never be persisted as this library's choice.
    /// </summary>
    public async Task<Result> SaveSelectionAsync(OcrScope scope, OcrPresetId? presetId,
        CancellationToken cancellationToken = default)
    {
        Result<LibraryMetadata> current = await library.GetCurrentLibraryAsync(cancellationToken);
        if (current.IsFailure)
        {
            return Result.Failure(current.ErrorCode!, current.ErrorMessage!);
        }

        if (presetId is { } chosen)
        {
            Result<OcrPreset> preset = await presets.ResolveSelectionAsync(chosen, cancellationToken);
            if (preset.IsFailure)
            {
                return Result.Failure(preset.ErrorCode!, preset.ErrorMessage!);
            }
        }

        // Merge under the store's shared file gate — a ReadAsync/WriteAsync pair would let a
        // concurrent save on another instance overwrite this scope update.
        return await store.MutateAsync(current.Value.LibraryId,
            existing => existing.WithPreset(scope, presetId?.ToString()), cancellationToken);
    }

    /// <summary>
    ///     Atomically persists several scope selections in one read-validate-write pass; null
    ///     clears a scope back to the implicit default. Every non-null id is validated
    ///     library-safely before anything is written, and unrelated scopes/libraries are
    ///     untouched.
    /// </summary>
    public async Task<Result> SaveSelectionsAsync(IReadOnlyDictionary<OcrScope, OcrPresetId?> selections,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selections);
        Result<LibraryMetadata> current = await library.GetCurrentLibraryAsync(cancellationToken);
        if (current.IsFailure)
        {
            return Result.Failure(current.ErrorCode!, current.ErrorMessage!);
        }

        foreach (OcrPresetId? presetId in selections.Values)
        {
            if (presetId is not { } chosen)
            {
                continue;
            }

            Result<OcrPreset> preset = await presets.ResolveSelectionAsync(chosen, cancellationToken);
            if (preset.IsFailure)
            {
                return Result.Failure(preset.ErrorCode!, preset.ErrorMessage!);
            }
        }

        // All scope merges fold into ONE gated mutation — parallel saves on other store
        // instances merge against the file state at write time, not a stale snapshot.
        return await store.MutateAsync(current.Value.LibraryId, existing =>
        {
            OcrPresetSelections updated = existing;
            foreach ((OcrScope scope, OcrPresetId? presetId) in selections)
            {
                updated = updated.WithPreset(scope, presetId?.ToString());
            }

            return updated;
        }, cancellationToken);
    }
}
