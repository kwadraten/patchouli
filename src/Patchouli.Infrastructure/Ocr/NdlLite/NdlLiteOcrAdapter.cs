using Microsoft.ML.OnnxRuntime;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Ocr;
using SkiaSharp;

namespace Patchouli.Infrastructure.Ocr.NdlLite;

/// <summary>
/// Native NDLOCR-Lite adapter backed by the in-process ONNX DEIM detector and
/// three-tier PARSeq cascade. A missing or incomplete model set surfaces as a
/// readiness failure instead of a silent fallback.
/// </summary>
public sealed class NdlLiteOcrAdapter : IRealOcrAdapter
{
    private readonly IOcrModelPathValidator _modelPathValidator;
    private readonly object _pipelineLock = new();
    private NdlLiteOcrPipeline? _pipeline;
    private string? _pipelineModelPath;

    public NdlLiteOcrAdapter(IOcrModelPathValidator modelPathValidator)
    {
        _modelPathValidator = modelPathValidator;
    }

    public string EngineId => OcrEngineIds.NdlLite;

    public string DisplayName => "NDLOCR-Lite";

    public string Kind => OcrAdapterKind.LocalLibrary;

    public OcrEngineCapability GetCapability()
    {
        return new OcrEngineCapability(
            EngineId,
            DisplayName,
            true,
            false,
            true,
            false,
            true,
            true,
            false,
            false,
            true,
            [OcrInputKinds.PageImage, OcrInputKinds.ImageFile, OcrInputKinds.RegionImage],
            NdlLiteModelFiles.Attribution);
    }

    public async Task<OcrEnvironmentCheckResult> CheckEnvironmentAsync(OcrPresetVersion presetVersion,
        CancellationToken cancellationToken = default)
    {
        string? modelsDirectory = presetVersion.ModelPath;
        if (string.IsNullOrWhiteSpace(modelsDirectory))
        {
            return new OcrEnvironmentCheckResult(EngineId, presetVersion.ModelId, modelsDirectory,
                OcrEnvironmentStatus.MissingModelPath, false,
                "NDLOCR-Lite requires a model directory. Download the models in Settings > Local Files.",
                OcrRequiredAction.RebindModelPath, []);
        }

        OcrEnvironmentCheckResult directory = await _modelPathValidator
            .ValidateModelPathAsync(modelsDirectory, true, cancellationToken);
        if (!directory.IsReady)
        {
            return directory with { EngineId = EngineId, ModelId = presetVersion.ModelId };
        }

        IReadOnlyList<NdlLiteModelFileEntry> missing = NdlLiteModelFiles.GetMissing(modelsDirectory);
        if (missing.Count > 0)
        {
            string names = string.Join(", ", missing.Select(static file => file.FileName));
            return new OcrEnvironmentCheckResult(EngineId, presetVersion.ModelId, modelsDirectory,
                OcrEnvironmentStatus.MissingModelPath, false,
                $"NDLOCR-Lite model files are missing or incomplete ({names}). Download them in Settings > Local Files.",
                OcrRequiredAction.RebindModelPath, []);
        }

        return new OcrEnvironmentCheckResult(EngineId, presetVersion.ModelId, modelsDirectory,
            OcrEnvironmentStatus.Ready, true,
            "NDLOCR-Lite DEIM detector and PARSeq cascade are ready.", OcrRequiredAction.None, []);
    }

    public Task<Result> ValidatePresetAsync(OcrPresetVersion presetVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(presetVersion.ModelPath))
        {
            return Task.FromResult(Result.Failure(
                AppErrorCodes.ValidationFailed,
                "The NDLOCR-Lite preset must specify a model directory."));
        }

        return Task.FromResult(Result.Success());
    }

    public Task<Result> ValidateInputAsync(OcrInputDescriptor input, CancellationToken cancellationToken = default)
    {
        if (input.InputKind is not OcrInputKinds.PageImage
            and not OcrInputKinds.ImageFile
            and not OcrInputKinds.RegionImage)
        {
            return Task.FromResult(Result.Failure(
                AppErrorCodes.UnsupportedOperation,
                $"Input kind '{input.InputKind}' is not supported by NDLOCR-Lite."));
        }

        if (string.IsNullOrWhiteSpace(input.ImagePath) || !File.Exists(input.ImagePath))
        {
            return Task.FromResult(Result.Failure(
                AppErrorCodes.NotFound,
                "A rendered page image is required for NDLOCR-Lite."));
        }

        return Task.FromResult(Result.Success());
    }

    public Task<Result<OcrEnginePageResult>> RunPageAsync(OcrInputDescriptor input, OcrPresetVersion presetVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(presetVersion.ModelPath))
        {
            return Task.FromResult(Result<OcrEnginePageResult>.Failure(
                AppErrorCodes.InvalidState,
                "NDLOCR-Lite model directory is not configured."));
        }

        if (string.IsNullOrWhiteSpace(input.ImagePath) || !File.Exists(input.ImagePath))
        {
            return Task.FromResult(Result<OcrEnginePageResult>.Failure(
                AppErrorCodes.NotFound,
                "OCR input image was not found."));
        }

        string modelsDirectory = presetVersion.ModelPath;
        if (!NdlLiteModelFiles.IsComplete(modelsDirectory))
        {
            return Task.FromResult(Result<OcrEnginePageResult>.Failure(
                AppErrorCodes.InvalidState,
                "NDLOCR-Lite model files are missing. Download them in Settings > Local Files."));
        }

        cancellationToken.ThrowIfCancellationRequested();

        using SKBitmap? image = SKBitmap.Decode(input.ImagePath);
        if (image is null)
        {
            return Task.FromResult(Result<OcrEnginePageResult>.Failure(
                AppErrorCodes.InvalidState,
                $"Unable to decode image: {input.ImagePath}"));
        }

        NormalizedBBox? regionBBox = input.RegionBBox;
        bool isRegion = input.InputKind == OcrInputKinds.RegionImage && regionBBox is not null;
        using SKBitmap? workingImage = isRegion
            ? CropToNormalized(image, regionBBox!.Value)
            : image.Copy();

        if (workingImage is null)
        {
            return Task.FromResult(Result<OcrEnginePageResult>.Failure(
                AppErrorCodes.InvalidState,
                "Unable to prepare the OCR input image."));
        }

        NdlLitePageResult result;
        try
        {
            lock (_pipelineLock)
            {
                result = GetOrCreatePipelineLocked(modelsDirectory).Run(workingImage);
            }
        }
        catch (OnnxRuntimeException exception)
        {
            return Task.FromResult(Result<OcrEnginePageResult>.Failure(
                AppErrorCodes.InvalidState,
                $"NDLOCR-Lite inference failed: {exception.Message}"));
        }
        catch (InvalidOperationException exception)
        {
            return Task.FromResult(Result<OcrEnginePageResult>.Failure(
                AppErrorCodes.InvalidState,
                $"NDLOCR-Lite could not run its models: {exception.Message}"));
        }
        catch (IOException exception)
        {
            return Task.FromResult(Result<OcrEnginePageResult>.Failure(
                AppErrorCodes.InvalidState,
                $"NDLOCR-Lite could not read its model files: {exception.Message}"));
        }

        List<OcrEngineTextBox> textBoxes = new(result.Lines.Count);
        NormalizedBBox regionBox = isRegion ? regionBBox!.Value : new NormalizedBBox(0, 0, 1, 1);
        foreach (NdlLiteLine line in result.Lines)
        {
            NormalizedBBox normalized = isRegion
                ? MapRegionBox(line.Box, regionBox, workingImage.Width, workingImage.Height)
                : MapPageBox(line.Box, workingImage.Width, workingImage.Height);
            textBoxes.Add(new OcrEngineTextBox(line.Text, normalized, line.Confidence));
        }

        NormalizedBBox pageBBox = isRegion ? regionBox : new NormalizedBBox(0, 0, 1, 1);
        return Task.FromResult(Result<OcrEnginePageResult>.Success(new OcrEnginePageResult(
            input.PageId,
            true,
            result.Text,
            pageBBox,
            null,
            null,
            null,
            textBoxes)));
    }

    private static NormalizedBBox MapPageBox(NdlLiteBox box, int imageWidth, int imageHeight)
    {
        double x = box.X0 / (double)imageWidth;
        double y = box.Y0 / (double)imageHeight;
        double width = box.Width / (double)imageWidth;
        double height = box.Height / (double)imageHeight;
        return ClampNormalized(new NormalizedBBox(x, y, width, height));
    }

    private static NormalizedBBox MapRegionBox(NdlLiteBox box, NormalizedBBox region, int cropWidth, int cropHeight)
    {
        double scaleX = region.Width / cropWidth;
        double scaleY = region.Height / cropHeight;
        double x = region.X + box.X0 * scaleX;
        double y = region.Y + box.Y0 * scaleY;
        double width = box.Width * scaleX;
        double height = box.Height * scaleY;
        return ClampNormalized(new NormalizedBBox(x, y, width, height));
    }

    private static NormalizedBBox ClampNormalized(NormalizedBBox bbox)
    {
        double x = Math.Clamp(bbox.X, 0.0, 1.0);
        double y = Math.Clamp(bbox.Y, 0.0, 1.0);
        double width = Math.Clamp(bbox.Width, 0.0, 1.0 - x);
        double height = Math.Clamp(bbox.Height, 0.0, 1.0 - y);
        return new NormalizedBBox(x, y, width, height);
    }

    private NdlLiteOcrPipeline GetOrCreatePipelineLocked(string modelsDirectory)
    {
        if (_pipeline is not null && _pipelineModelPath == modelsDirectory)
        {
            return _pipeline;
        }

        _pipeline?.Dispose();
        _pipeline = null;
        _pipelineModelPath = null;
        NdlLiteOcrPipeline created = new(modelsDirectory);
        _pipeline = created;
        _pipelineModelPath = modelsDirectory;
        return created;
    }

    private static SKBitmap CropToNormalized(SKBitmap image, NormalizedBBox bbox)
    {
        int x0 = (int)(bbox.X * image.Width);
        int y0 = (int)(bbox.Y * image.Height);
        int x1 = (int)((bbox.X + bbox.Width) * image.Width);
        int y1 = (int)((bbox.Y + bbox.Height) * image.Height);
        x0 = Math.Clamp(x0, 0, image.Width - 1);
        y0 = Math.Clamp(y0, 0, image.Height - 1);
        x1 = Math.Clamp(x1, x0 + 1, image.Width);
        y1 = Math.Clamp(y1, y0 + 1, image.Height);

        SKBitmap crop = new(x1 - x0, y1 - y0, image.ColorType, image.AlphaType);
        using SKCanvas canvas = new(crop);
        canvas.DrawBitmap(image, new SKRect(x0, y0, x1, y1), new SKRect(0, 0, crop.Width, crop.Height));
        return crop;
    }
}
