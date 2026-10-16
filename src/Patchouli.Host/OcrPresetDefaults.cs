using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Llm;
using Patchouli.Ocr;
using Patchouli.UI;

namespace Patchouli.Host;

/// <summary>Shared default presets and queue routing for desktop and agent OCR entry points.</summary>
public sealed class OcrPresetDefaults(
    IOcrPresetService presets,
    OcrStorageLocations storage,
    Func<LlmAppSettings> settings)
{
    private const string MultimodalPresetName = "Multimodal LLM OCR";

    private const string MultimodalPresetDescription =
        "Default multimodal OCR preset following the configured OCR model";

    private readonly IOcrPresetService _presets = presets;
    private readonly OcrStorageLocations _storage = storage;

    public Task<Result<OcrPresetId>> EnsurePresetAsync(string engineId,
        CancellationToken cancellationToken = default)
    {
        return engineId switch
        {
            OcrEngineIds.MinerU => EnsureMinerUPresetAsync(cancellationToken),
            OcrEngineIds.NdlKoten => EnsureNdlKotenPresetAsync(cancellationToken),
            OcrEngineIds.NdlLite => EnsureNdlLitePresetAsync(cancellationToken),
            OcrEngineIds.RapidOcr => EnsureRapidOcrPresetAsync(cancellationToken),
            OcrEngineIds.MultimodalLlm => EnsureMultimodalPresetAsync(cancellationToken),
            _ => Task.FromResult(Result<OcrPresetId>.Failure(AppErrorCodes.UnsupportedOperation,
                $"未实现默认 OCR preset 的引擎：{engineId}"))
        };
    }

    public (string AdapterKind, string? ProviderId) QueueRoutingFor(string engineId)
    {
        return OcrQueueRouting.ForEngine(engineId, settings().OcrSelection.ProviderId);
    }

    private async Task<Result<OcrPresetId>> EnsureMultimodalPresetAsync(CancellationToken cancellationToken)
    {
        Result<OcrPreset?> existing =
            await _presets.FindActivePresetByEngineIdAsync(OcrEngineIds.MultimodalLlm, cancellationToken);
        if (existing.IsFailure)
        {
            return Result<OcrPresetId>.Failure(existing.ErrorCode!, existing.ErrorMessage!);
        }

        bool managed = existing.Value is null ||
                       (existing.Value.Name == MultimodalPresetName &&
                        existing.Value.Description == MultimodalPresetDescription);
        if (!managed)
        {
            return Result<OcrPresetId>.Success(existing.Value!.PresetId);
        }

        string model = settings().OcrSelection.Model;
        if (string.IsNullOrWhiteSpace(model))
        {
            return Result<OcrPresetId>.Failure(AppErrorCodes.ValidationFailed,
                "请先在设置的多模态 OCR 中配置模型。");
        }

        if (existing.Value is { } preset)
        {
            Result<OcrPresetVersion>
                version = await _presets.GetCurrentVersionAsync(preset.PresetId, cancellationToken);
            if (version.IsFailure)
            {
                return Result<OcrPresetId>.Failure(version.ErrorCode!, version.ErrorMessage!);
            }

            if (version.Value.ModelId != model)
            {
                Result<OcrPresetVersion> updated = await _presets.CreatePresetVersionAsync(preset.PresetId,
                    OcrEngineIds.MultimodalLlm, model, version.Value.ModelPath,
                    version.Value.ParametersJson, version.Value.ApplyOnSuccess, cancellationToken);
                if (updated.IsFailure)
                {
                    return Result<OcrPresetId>.Failure(updated.ErrorCode!, updated.ErrorMessage!);
                }
            }

            return Result<OcrPresetId>.Success(preset.PresetId);
        }

        Result<OcrPreset> created = await _presets.CreatePresetAsync(MultimodalPresetName,
            MultimodalPresetDescription, OcrEngineIds.MultimodalLlm, model, null, "{}", true, cancellationToken);
        return created.IsFailure
            ? Result<OcrPresetId>.Failure(created.ErrorCode!, created.ErrorMessage!)
            : Result<OcrPresetId>.Success(created.Value.PresetId);
    }

    private async Task<Result<OcrPresetId>> EnsureMinerUPresetAsync(CancellationToken cancellationToken)
    {
        Result<OcrPreset?> existing =
            await _presets.FindActivePresetByEngineIdAsync(OcrEngineIds.MinerU, cancellationToken);
        if (existing.IsFailure)
        {
            return Result<OcrPresetId>.Failure(existing.ErrorCode!, existing.ErrorMessage!);
        }

        if (existing.Value is not null)
        {
            return Result<OcrPresetId>.Success(existing.Value.PresetId);
        }

        Result<OcrPreset> created = await _presets.CreatePresetAsync(
            "MinerU OCR",
            "MinerU document OCR preset",
            OcrEngineIds.MinerU,
            OcrModelIds.MinerUDefault,
            null,
            """{"isOcr":true,"enableTable":true,"enableFormula":true}""",
            true,
            cancellationToken);
        return created.IsFailure
            ? Result<OcrPresetId>.Failure(created.ErrorCode!, created.ErrorMessage!)
            : Result<OcrPresetId>.Success(created.Value.PresetId);
    }

    private async Task<Result<OcrPresetId>> EnsureNdlKotenPresetAsync(CancellationToken cancellationToken)
    {
        Result<OcrPreset?> existing =
            await _presets.FindActivePresetByEngineIdAsync(OcrEngineIds.NdlKoten, cancellationToken);
        if (existing.IsFailure)
        {
            return Result<OcrPresetId>.Failure(existing.ErrorCode!, existing.ErrorMessage!);
        }

        if (existing.Value is not null)
        {
            return Result<OcrPresetId>.Success(existing.Value.PresetId);
        }

        Result<OcrPreset> created = await _presets.CreatePresetAsync(
            "NDL Koten OCR Lite",
            "Local classical Japanese OCR preset",
            OcrEngineIds.NdlKoten,
            OcrModelIds.NdlKotenDefault,
            _storage.NdlKotenModelsDirectory,
            "{}",
            true,
            cancellationToken);
        return created.IsFailure
            ? Result<OcrPresetId>.Failure(created.ErrorCode!, created.ErrorMessage!)
            : Result<OcrPresetId>.Success(created.Value.PresetId);
    }

    private async Task<Result<OcrPresetId>> EnsureNdlLitePresetAsync(CancellationToken cancellationToken)
    {
        Result<OcrPreset?> existing =
            await _presets.FindActivePresetByEngineIdAsync(OcrEngineIds.NdlLite, cancellationToken);
        if (existing.IsFailure)
        {
            return Result<OcrPresetId>.Failure(existing.ErrorCode!, existing.ErrorMessage!);
        }

        if (existing.Value is not null)
        {
            return Result<OcrPresetId>.Success(existing.Value.PresetId);
        }

        Result<OcrPreset> created = await _presets.CreatePresetAsync(
            "NDLOCR-Lite",
            "Local Japanese OCR preset for ndlocr-lite",
            OcrEngineIds.NdlLite,
            OcrModelIds.NdlLiteDefault,
            _storage.NdlLiteModelsDirectory,
            "{}",
            true,
            cancellationToken);
        return created.IsFailure
            ? Result<OcrPresetId>.Failure(created.ErrorCode!, created.ErrorMessage!)
            : Result<OcrPresetId>.Success(created.Value.PresetId);
    }

    private async Task<Result<OcrPresetId>> EnsureRapidOcrPresetAsync(CancellationToken cancellationToken)
    {
        Result<OcrPreset?> existing =
            await _presets.FindActivePresetByEngineIdAsync(OcrEngineIds.RapidOcr, cancellationToken);
        if (existing.IsFailure)
        {
            return Result<OcrPresetId>.Failure(existing.ErrorCode!, existing.ErrorMessage!);
        }

        if (existing.Value is not null)
        {
            return Result<OcrPresetId>.Success(existing.Value.PresetId);
        }

        // Empty parameters keep the RapidOCR ONNX pipeline on the upstream v3.9.2 defaults.
        Result<OcrPreset> created = await _presets.CreatePresetAsync(
            "RapidOCR",
            "Local RapidOCR preset using the pinned PP-OCRv6 models",
            OcrEngineIds.RapidOcr,
            OcrModelIds.RapidOcrDefault,
            _storage.RapidOcrModelsDirectory,
            "{}",
            true,
            cancellationToken);
        return created.IsFailure
            ? Result<OcrPresetId>.Failure(created.ErrorCode!, created.ErrorMessage!)
            : Result<OcrPresetId>.Success(created.Value.PresetId);
    }
}
