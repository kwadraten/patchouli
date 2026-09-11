using Microsoft.ML.OnnxRuntime;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Ocr;

namespace Patchouli.Infrastructure.Ocr.RapidOcr;

/// <summary>
/// RapidOCR adapter backed by the in-process .NET ONNX pipeline. Models are downloaded
/// on demand from the pinned RapidOCR v3.9.2 manifest; a missing or incomplete model set
/// surfaces as a readiness failure instead of a silent fallback.
/// </summary>
public sealed class RapidOcrOcrAdapter : IRealOcrAdapter
{
    private readonly string _defaultModelsDirectory;
    private readonly IOcrModelPathValidator _modelPathValidator;
    private readonly object _pipelineLock = new();
    private RapidOcrPipeline? _pipeline;
    private string? _pipelineKey;

    public RapidOcrOcrAdapter(IOcrModelPathValidator modelPathValidator, string defaultModelsDirectory)
    {
        _modelPathValidator = modelPathValidator;
        _defaultModelsDirectory = defaultModelsDirectory;
    }

    public string EngineId => OcrEngineIds.RapidOcr;

    public string DisplayName => "RapidOCR";

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
            RapidOcrModelFiles.Attribution);
    }

    public async Task<OcrEnvironmentCheckResult> CheckEnvironmentAsync(OcrPresetVersion presetVersion,
        CancellationToken cancellationToken = default)
    {
        RapidOcrParameters parameters = RapidOcrParameters.FromJson(presetVersion.ParametersJson);
        string? modelsDirectory = ResolveModelsDirectory(parameters, presetVersion.ModelPath);
        if (string.IsNullOrWhiteSpace(modelsDirectory))
        {
            return new OcrEnvironmentCheckResult(EngineId, presetVersion.ModelId, modelsDirectory,
                OcrEnvironmentStatus.MissingModelPath, false,
                "RapidOCR requires a model directory. Download the models in Settings > Local Files.",
                OcrRequiredAction.RebindModelPath, []);
        }

        OcrEnvironmentCheckResult directory = await _modelPathValidator.ValidateModelPathAsync(modelsDirectory, true,
            cancellationToken);
        if (!directory.IsReady)
        {
            return directory with { EngineId = EngineId, ModelId = presetVersion.ModelId };
        }

        IReadOnlyList<RapidOcrModelFile> missing = RapidOcrModelFiles.GetMissing(modelsDirectory, parameters.UseCls);
        if (missing.Count > 0)
        {
            string names = string.Join(", ", missing.Select(static file => file.FileName));
            return new OcrEnvironmentCheckResult(EngineId, presetVersion.ModelId, modelsDirectory,
                OcrEnvironmentStatus.MissingModelPath, false,
                $"RapidOCR model files are missing or incomplete ({names}). Download them in Settings > Local Files.",
                OcrRequiredAction.RebindModelPath, []);
        }

        return new OcrEnvironmentCheckResult(EngineId, presetVersion.ModelId, modelsDirectory,
            OcrEnvironmentStatus.Ready, true,
            "RapidOCR PP-OCRv6 detector and recognizer are ready.", OcrRequiredAction.None, []);
    }

    public Task<Result> ValidatePresetAsync(OcrPresetVersion presetVersion,
        CancellationToken cancellationToken = default)
    {
        RapidOcrParameters parameters = RapidOcrParameters.FromJson(presetVersion.ParametersJson);
        if (!parameters.RequiresDetectionAndRecognition)
        {
            return Task.FromResult(Result.Failure(AppErrorCodes.ValidationFailed,
                "RapidOCR requires useDet and useRec to produce text boxes."));
        }

        IReadOnlyList<string> unsupported = parameters.GetUnsupportedOptions();
        if (unsupported.Count > 0)
        {
            return Task.FromResult(Result.Failure(AppErrorCodes.ValidationFailed,
                $"RapidOCR does not support these options: {string.Join(", ", unsupported)}."));
        }

        if (string.IsNullOrWhiteSpace(ResolveModelsDirectory(parameters, presetVersion.ModelPath)))
        {
            return Task.FromResult(Result.Failure(AppErrorCodes.ValidationFailed,
                "The RapidOCR preset must specify a model directory."));
        }

        return Task.FromResult(Result.Success());
    }

    public Task<Result> ValidateInputAsync(OcrInputDescriptor input, CancellationToken cancellationToken = default)
    {
        if (input.InputKind is not OcrInputKinds.PageImage
            and not OcrInputKinds.ImageFile
            and not OcrInputKinds.RegionImage)
        {
            return Task.FromResult(Result.Failure(AppErrorCodes.UnsupportedOperation,
                $"Input kind '{input.InputKind}' is not supported by RapidOCR."));
        }

        if (string.IsNullOrWhiteSpace(input.ImagePath) || !File.Exists(input.ImagePath))
        {
            return Task.FromResult(Result.Failure(AppErrorCodes.NotFound,
                "A rendered page image is required for RapidOCR."));
        }

        return Task.FromResult(Result.Success());
    }

    public Task<Result<OcrEnginePageResult>> RunPageAsync(OcrInputDescriptor input, OcrPresetVersion presetVersion,
        CancellationToken cancellationToken = default)
    {
        RapidOcrParameters parameters = RapidOcrParameters.FromJson(presetVersion.ParametersJson);
        string? modelsDirectory = ResolveModelsDirectory(parameters, presetVersion.ModelPath);
        if (string.IsNullOrWhiteSpace(modelsDirectory) ||
            !RapidOcrModelFiles.IsComplete(modelsDirectory, parameters.UseCls))
        {
            return Task.FromResult(Result<OcrEnginePageResult>.Failure(AppErrorCodes.InvalidState,
                "RapidOCR model files are missing. Download them in Settings > Local Files."));
        }

        if (string.IsNullOrWhiteSpace(input.ImagePath) || !File.Exists(input.ImagePath))
        {
            return Task.FromResult(Result<OcrEnginePageResult>.Failure(AppErrorCodes.NotFound,
                "RapidOCR input image was not found."));
        }

        RapidOcrImage? decoded = RapidOcrImage.TryDecode(input.ImagePath);
        if (decoded is null)
        {
            return Task.FromResult(Result<OcrEnginePageResult>.Failure(AppErrorCodes.InvalidState,
                $"Unable to decode image: {input.ImagePath}"));
        }

        cancellationToken.ThrowIfCancellationRequested();

        bool isRegion = input.InputKind == OcrInputKinds.RegionImage && input.RegionBBox is not null;
        NormalizedBBox region = isRegion ? input.RegionBBox!.Value : new NormalizedBBox(0, 0, 1, 1);
        RapidOcrImage working = isRegion ? CropToNormalized(decoded, region) : decoded;

        RapidOcrPipelineResult result;
        try
        {
            lock (_pipelineLock)
            {
                RapidOcrPipeline pipeline = GetOrCreatePipelineLocked(modelsDirectory!, presetVersion.ParametersJson,
                    parameters);
                result = pipeline.Run(working);
            }
        }
        catch (OnnxRuntimeException exception)
        {
            return Task.FromResult(Result<OcrEnginePageResult>.Failure(AppErrorCodes.InvalidState,
                $"RapidOCR inference failed: {exception.Message}"));
        }
        catch (InvalidOperationException exception)
        {
            return Task.FromResult(Result<OcrEnginePageResult>.Failure(AppErrorCodes.InvalidState,
                $"RapidOCR could not load its models: {exception.Message}"));
        }

        List<OcrEngineTextBox> textBoxes = new(result.Lines.Count);
        foreach (RapidOcrPipelineLine line in result.Lines)
        {
            NormalizedBBox bbox = isRegion
                ? MapRegionBox(line.Points, region, working.Width, working.Height)
                : MapPageBox(line.Points, working.Width, working.Height);
            textBoxes.Add(new OcrEngineTextBox(line.Text, bbox, line.Score));
        }

        NormalizedBBox pageBBox = isRegion ? region : new NormalizedBBox(0, 0, 1, 1);
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

    internal static string? ResolveModelsDirectory(RapidOcrParameters parameters, string? presetModelPath)
    {
        if (!string.IsNullOrWhiteSpace(parameters.ModelRootDir))
        {
            return parameters.ModelRootDir.Trim();
        }

        return string.IsNullOrWhiteSpace(presetModelPath) ? null : presetModelPath.Trim();
    }

    private RapidOcrPipeline GetOrCreatePipelineLocked(string modelsDirectory, string parametersJson,
        RapidOcrParameters parameters)
    {
        string cacheKey = $"{modelsDirectory}|{parametersJson}";
        if (_pipeline is not null && _pipelineKey == cacheKey)
        {
            return _pipeline;
        }

        _pipeline?.Dispose();
        _pipeline = new RapidOcrPipeline(modelsDirectory, parameters);
        _pipelineKey = cacheKey;
        return _pipeline;
    }

    private static NormalizedBBox MapPageBox(IReadOnlyList<RapidOcrPoint> points, int imageWidth, int imageHeight)
    {
        double width = Math.Max(1, imageWidth);
        double height = Math.Max(1, imageHeight);
        (double minX, double minY, double maxX, double maxY) = Bounds(points);
        return ClampNormalized(new NormalizedBBox(minX / width, minY / height, (maxX - minX) / width,
            (maxY - minY) / height));
    }

    private static NormalizedBBox MapRegionBox(IReadOnlyList<RapidOcrPoint> points, NormalizedBBox region,
        int cropWidth, int cropHeight)
    {
        double width = Math.Max(1, cropWidth);
        double height = Math.Max(1, cropHeight);
        (double minX, double minY, double maxX, double maxY) = Bounds(points);
        double scaleX = region.Width / width;
        double scaleY = region.Height / height;
        return ClampNormalized(new NormalizedBBox(
            region.X + minX * scaleX,
            region.Y + minY * scaleY,
            (maxX - minX) * scaleX,
            (maxY - minY) * scaleY));
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) Bounds(IReadOnlyList<RapidOcrPoint> points)
    {
        double minX = double.MaxValue;
        double minY = double.MaxValue;
        double maxX = double.MinValue;
        double maxY = double.MinValue;
        foreach (RapidOcrPoint point in points)
        {
            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);
        }

        return (minX, minY, maxX, maxY);
    }

    private static NormalizedBBox ClampNormalized(NormalizedBBox bbox)
    {
        double x = Math.Clamp(bbox.X, 0.0, 1.0);
        double y = Math.Clamp(bbox.Y, 0.0, 1.0);
        double width = Math.Clamp(bbox.Width, 0.0, 1.0 - x);
        double height = Math.Clamp(bbox.Height, 0.0, 1.0 - y);
        return new NormalizedBBox(x, y, width, height);
    }

    private static RapidOcrImage CropToNormalized(RapidOcrImage image, NormalizedBBox bbox)
    {
        int x0 = (int)(bbox.X * image.Width);
        int y0 = (int)(bbox.Y * image.Height);
        int x1 = (int)((bbox.X + bbox.Width) * image.Width);
        int y1 = (int)((bbox.Y + bbox.Height) * image.Height);
        x0 = Math.Clamp(x0, 0, image.Width - 1);
        y0 = Math.Clamp(y0, 0, image.Height - 1);
        x1 = Math.Clamp(x1, x0 + 1, image.Width);
        y1 = Math.Clamp(y1, y0 + 1, image.Height);
        return image.Crop(x0, y0, x1, y1);
    }
}
