using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Ocr;

public interface IOcrPresetService
{
    Task<Result<OcrPreset>> CreatePresetAsync(string name, string? description, string engineId, string modelId,
        string? modelPath, string parametersJson, bool applyOnSuccess, CancellationToken cancellationToken = default);

    Task<Result<OcrPreset>> GetPresetAsync(OcrPresetId presetId, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<OcrPreset>>> ListPresetsAsync(bool includeArchived = false,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result<IReadOnlyList<OcrPreset>>.Failure(AppErrorCodes.UnsupportedOperation,
            "OCR preset listing is unavailable on this service."));
    }

    Task<Result<OcrPreset>> UpdatePresetAsync(OcrPresetId presetId, string name, string? description,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result<OcrPreset>.Failure(AppErrorCodes.UnsupportedOperation,
            "OCR preset updates are unavailable on this service."));
    }

    Task<Result<OcrPresetVersion>> CreatePresetVersionAsync(OcrPresetId presetId, string engineId, string modelId,
        string? modelPath, string parametersJson, bool applyOnSuccess, CancellationToken cancellationToken = default);

    Task<Result<OcrPresetVersion>> GetCurrentVersionAsync(OcrPresetId presetId,
        CancellationToken cancellationToken = default);

    Task<Result<OcrPresetVersion>> GetPresetVersionAsync(OcrPresetVersionId presetVersionId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result<OcrPresetVersion>.Failure(AppErrorCodes.UnsupportedOperation,
            "OCR preset version lookup is unavailable on this service."));
    }

    Task<Result<IReadOnlyList<OcrPresetVersion>>> ListVersionsAsync(OcrPresetId presetId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result<IReadOnlyList<OcrPresetVersion>>.Failure(
            AppErrorCodes.UnsupportedOperation,
            "OCR preset version listing is unavailable on this service."));
    }

    /// <summary>
    ///     Resolves one explicitly chosen preset for the active library: NotFound when the id is
    ///     unknown, ValidationFailed when the preset is archived or belongs to another library.
    ///     Explicit selections never fall back to another same-engine preset.
    /// </summary>
    Task<Result<OcrPreset>> ResolveSelectionAsync(OcrPresetId presetId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result<OcrPreset>.Failure(AppErrorCodes.UnsupportedOperation,
            "OCR preset selection is unavailable on this service."));
    }

    Task<Result<OcrPreset?>> FindActivePresetByEngineIdAsync(string engineId,
        CancellationToken cancellationToken = default);

    Task<Result<OcrPresetVersion>> RebindModelPathAsync(OcrPresetId presetId, string newModelPath,
        CancellationToken cancellationToken = default);

    Task<Result> ArchivePresetAsync(OcrPresetId presetId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Atomic composition save used by the inline preset-composition UI: writes preset
    ///     name/description and — when <paramref name="newVersion" /> is provided — inserts the
    ///     supplied immutable version and moves the preset's current-version pointer, all inside
    ///     one transaction under the preset write gate. <paramref name="expectedCurrentVersionId" />
    ///     is an optimistic-concurrency guard over the editing row's baseline: a stale row fails
    ///     with a Conflict error instead of overwriting another save. A null version performs a
    ///     metadata-only save that never touches the frozen component snapshot.
    /// </summary>
    Task<Result<OcrPreset>> SaveCompositionAsync(OcrPresetId presetId, string name,
        string? description, OcrPresetVersionId? expectedCurrentVersionId,
        OcrPresetVersionWrite? newVersion, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result<OcrPreset>.Failure(AppErrorCodes.UnsupportedOperation,
            "OCR preset composition persistence is unavailable on this service."));
    }
}

/// <summary>
///     Payload for one immutable preset version inserted by
///     <see cref="IOcrPresetService.SaveCompositionAsync" />. <see cref="ParametersJson" /> is the
///     frozen execution configuration (structured envelope produced by the host composition
///     capture); the service persists it verbatim.
/// </summary>
public sealed record OcrPresetVersionWrite(
    string EngineId,
    string ModelId,
    string? ModelPath,
    string ParametersJson,
    bool ApplyOnSuccess);
