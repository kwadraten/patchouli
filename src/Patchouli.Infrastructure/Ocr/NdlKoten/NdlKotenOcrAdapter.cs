using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Ocr;
using SkiaSharp;

namespace Patchouli.Infrastructure.Ocr.NdlKoten;

public sealed class NdlKotenOcrAdapter : IRealOcrAdapter
{
    private readonly IOcrModelPathValidator _modelPathValidator;
    private readonly object _pipelineLock = new();
    private NdlKotenOcrPipeline? _pipeline;
    private string? _pipelineModelPath;

    public NdlKotenOcrAdapter(IOcrModelPathValidator modelPathValidator)
    {
        _modelPathValidator = modelPathValidator;
    }

    public string EngineId => OcrEngineIds.NdlKoten;

    public string DisplayName => "NDL Koten OCR Lite";

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
            NdlKotenModelFiles.Attribution);
    }

    public Task<OcrEnvironmentCheckResult> CheckEnvironmentAsync(OcrPresetVersion presetVersion,
        CancellationToken cancellationToken = default)
    {
        return _modelPathValidator.ValidateModelPathAsync(presetVersion.ModelPath, true, cancellationToken);
    }

    public Task<Result> ValidatePresetAsync(OcrPresetVersion presetVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(presetVersion.ModelPath))
        {
            return Task.FromResult(Result.Failure(
                AppErrorCodes.ValidationFailed,
                "The NDL Koten OCR Lite preset must specify a model directory."));
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
                $"Input kind '{input.InputKind}' is not supported by NDL Koten OCR Lite."));
        }

        if (string.IsNullOrWhiteSpace(input.ImagePath) || !File.Exists(input.ImagePath))
        {
            return Task.FromResult(Result.Failure(
                AppErrorCodes.NotFound,
                "A rendered page image is required for NDL Koten OCR Lite."));
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
                "NDL Koten OCR Lite model directory is not configured."));
        }

        if (string.IsNullOrWhiteSpace(input.ImagePath) || !File.Exists(input.ImagePath))
        {
            return Task.FromResult(Result<OcrEnginePageResult>.Failure(
                AppErrorCodes.NotFound,
                "OCR input image was not found."));
        }

        string modelsDirectory = presetVersion.ModelPath;
        if (!NdlKotenModelFiles.IsComplete(modelsDirectory))
        {
            return Task.FromResult(Result<OcrEnginePageResult>.Failure(
                AppErrorCodes.InvalidState,
                "NDL Koten OCR Lite model files are missing. Download them in Settings > Local Files."));
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
        using SKBitmap? workingImage = input.InputKind == OcrInputKinds.RegionImage && regionBBox is not null
            ? CropToNormalized(image, regionBBox.Value)
            : image.Copy();

        if (workingImage is null)
        {
            return Task.FromResult(Result<OcrEnginePageResult>.Failure(
                AppErrorCodes.InvalidState,
                "Unable to prepare OCR input image."));
        }

        NdlKotenPageResult result;
        lock (_pipelineLock)
        {
            result = GetOrCreatePipelineLocked(modelsDirectory).Run(workingImage);
        }

        List<OcrEngineTextBox> textBoxes = new();
        bool isRegion = input.InputKind == OcrInputKinds.RegionImage && input.RegionBBox is not null;
        NormalizedBBox regionBox = isRegion ? regionBBox!.Value : new NormalizedBBox(0, 0, 1, 1);
        foreach (NdlKotenLine line in result.Lines)
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

    private static NormalizedBBox MapPageBox(Box box, int imageWidth, int imageHeight)
    {
        double x = box.X0 / (double)imageWidth;
        double y = box.Y0 / (double)imageHeight;
        double width = (box.X1 - box.X0) / (double)imageWidth;
        double height = (box.Y1 - box.Y0) / (double)imageHeight;
        return ClampNormalized(new NormalizedBBox(x, y, width, height));
    }

    private static NormalizedBBox MapRegionBox(Box box, NormalizedBBox region, int cropWidth, int cropHeight)
    {
        double scaleX = region.Width / cropWidth;
        double scaleY = region.Height / cropHeight;
        double x = region.X + box.X0 * scaleX;
        double y = region.Y + box.Y0 * scaleY;
        double width = (box.X1 - box.X0) * scaleX;
        double height = (box.Y1 - box.Y0) * scaleY;
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

    private NdlKotenOcrPipeline GetOrCreatePipelineLocked(string modelsDirectory)
    {
        if (_pipeline is not null && _pipelineModelPath == modelsDirectory)
        {
            return _pipeline;
        }

        _pipeline?.Dispose();
        _pipeline = new NdlKotenOcrPipeline(modelsDirectory);
        _pipelineModelPath = modelsDirectory;
        return _pipeline;
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
