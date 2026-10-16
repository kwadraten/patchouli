using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Llm;

namespace Patchouli.Ocr;

/// <summary>Optional local layout detection around the online image recognizer, shared by every OCR scope.</summary>
public sealed class LayoutAwareMultimodalOcrAdapter(
    IRealOcrAdapter recognizer,
    IDocumentLayoutDetector detector,
    Func<LlmAppSettings> settings,
    Func<string, string> resolveModelPath) : IRealOcrAdapter, IRegionOcrPresetValidator
{
    public string EngineId => recognizer.EngineId;
    public string DisplayName => recognizer.DisplayName;
    public string Kind => recognizer.Kind;

    public OcrEngineCapability GetCapability()
    {
        return recognizer.GetCapability() with
        {
            SupportsLocalModel = true,
            RequiresModelPath = settings().OcrUseLocalLayout,
            Notes = "Local ONNX layout detection with online recognition, or direct online boxes and text."
        };
    }

    public async Task<OcrEnvironmentCheckResult> CheckEnvironmentAsync(OcrPresetVersion presetVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LlmAppSettings current = settings();
        string modelPath = resolveModelPath(current.OcrLayoutModel);
        Result layout = current.OcrUseLocalLayout
            ? detector.ValidateModel(current.OcrLayoutModel, modelPath)
            : Result.Success();
        if (layout.IsFailure)
        {
            return new OcrEnvironmentCheckResult(EngineId, presetVersion.ModelId, modelPath,
                OcrEnvironmentStatus.MissingModelPath, false, layout.ErrorMessage!,
                OcrRequiredAction.RebindModelPath, []);
        }

        return await recognizer.CheckEnvironmentAsync(presetVersion, cancellationToken);
    }

    public async Task<Result> ValidatePresetAsync(OcrPresetVersion presetVersion,
        CancellationToken cancellationToken = default)
    {
        OcrEnvironmentCheckResult check = await CheckEnvironmentAsync(presetVersion, cancellationToken);
        return check.IsReady ? Result.Success() : Result.Failure(AppErrorCodes.ValidationFailed, check.Message);
    }

    public Task<Result> ValidateInputAsync(OcrInputDescriptor input, CancellationToken cancellationToken = default)
    {
        return recognizer.ValidateInputAsync(input, cancellationToken);
    }

    public async Task<Result> ValidateRegionPresetAsync(OcrPresetVersion presetVersion,
        CancellationToken cancellationToken = default)
    {
        Result preset = await recognizer.ValidatePresetAsync(presetVersion, cancellationToken);
        if (preset.IsFailure)
        {
            return preset;
        }

        OcrEnvironmentCheckResult check = await recognizer.CheckEnvironmentAsync(presetVersion, cancellationToken);
        return check.IsReady ? Result.Success() : Result.Failure(AppErrorCodes.InvalidState, check.Message);
    }

    public async Task<Result<OcrEnginePageResult>> RunPageAsync(OcrInputDescriptor input,
        OcrPresetVersion presetVersion, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LlmAppSettings current = settings();
        if (!current.OcrUseLocalLayout || input.RegionBBox is not null)
        {
            return await recognizer.RunPageAsync(input, presetVersion, cancellationToken);
        }

        string modelPath = resolveModelPath(current.OcrLayoutModel);
        Result model = detector.ValidateModel(current.OcrLayoutModel, modelPath);
        if (model.IsFailure)
        {
            return Result<OcrEnginePageResult>.Failure(model.ErrorCode!, model.ErrorMessage!);
        }

        if (string.IsNullOrWhiteSpace(input.ImagePath) || !File.Exists(input.ImagePath))
        {
            return Result<OcrEnginePageResult>.Failure(AppErrorCodes.NotFound, "Rendered page image was not found.");
        }

        NormalizedBBox area = new(0, 0, 1, 1);

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(input.ImagePath, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result<OcrEnginePageResult>.Failure(AppErrorCodes.NotFound,
                $"The rendered page image could not be read: {exception.Message}");
        }

        Result<IReadOnlyList<DocumentLayoutRegion>> layout = await detector.DetectAsync(bytes,
            current.OcrLayoutModel, modelPath, cancellationToken);
        if (layout.IsFailure)
        {
            return Result<OcrEnginePageResult>.Failure(layout.ErrorCode!, layout.ErrorMessage!);
        }

        IReadOnlyList<DocumentLayoutRegion> ordered = layout.Value;
        if (ordered.Count > 1)
        {
            if (recognizer is not IMultimodalLayoutOrderResolver resolver)
            {
                return Result<OcrEnginePageResult>.Failure(AppErrorCodes.UnsupportedOperation,
                    "The multimodal recognizer cannot determine layout reading order.");
            }

            Result<IReadOnlyList<int>> order = await resolver.ResolveReadingOrderAsync(bytes,
                ordered, presetVersion, cancellationToken);
            if (order.IsFailure)
            {
                return Result<OcrEnginePageResult>.Failure(order.ErrorCode!, order.ErrorMessage!);
            }

            ordered = order.Value.Select(index => layout.Value[index]).ToArray();
        }

        Result<OcrEnginePageResult>[] results = new Result<OcrEnginePageResult>[ordered.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, ordered.Count), new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(current.OcrConcurrency, 1, OcrPerformanceSettings.MaxConcurrency),
            CancellationToken = cancellationToken
        }, async (index, token) =>
        {
            OcrInputDescriptor crop = input with
            {
                InputKind = OcrInputKinds.RegionImage, RegionBBox = ordered[index].BBox
            };
            results[index] = await recognizer.RunPageAsync(crop, presetVersion, token);
        });

        List<OcrEngineTextBox> boxes = [];
        for (int index = 0; index < ordered.Count; index++)
        {
            DocumentLayoutRegion region = ordered[index];
            Result<OcrEnginePageResult> recognized = results[index];
            if (recognized.IsFailure)
            {
                return recognized;
            }

            if (!recognized.Value.Succeeded)
            {
                return Result<OcrEnginePageResult>.Failure(recognized.Value.ErrorCode ?? AppErrorCodes.InvalidState,
                    recognized.Value.ErrorMessage ?? "Region recognition failed.");
            }

            if (!string.IsNullOrWhiteSpace(recognized.Value.Text))
            {
                // The detector supplies geometry; the language model supplies text, never replacement coordinates.
                boxes.Add(new OcrEngineTextBox(recognized.Value.Text.Trim(), region.BBox, region.Confidence));
            }
        }

        return Result<OcrEnginePageResult>.Success(new OcrEnginePageResult(input.PageId, true,
            boxes.Count == 0 ? null : string.Join('\n', boxes.Select(box => box.Text)), area,
            null, null, TextBoxes: boxes));
    }
}
