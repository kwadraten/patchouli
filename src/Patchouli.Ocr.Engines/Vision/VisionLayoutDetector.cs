using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using SkiaSharp;

namespace Patchouli.Ocr.Engines.Vision;

/// <summary>A local detector's normalized region, returned in the detector's reading order.</summary>
internal sealed record VisionLayoutRegion(
    string Label,
    NormalizedBBox BBox,
    double Confidence,
    int? Order = null);

/// <summary>The layout-detection seam a <see cref="MultimodalOcrSession"/> consumes.</summary>
internal interface IVisionLayoutDetector : IDisposable
{
    Task<Result<IReadOnlyList<VisionLayoutRegion>>> DetectAsync(byte[] imageBytes,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// CPU inference over the user-supplied layout ONNX file (PP-DocLayoutV3 or DocLayout-YOLO).
/// Weights are never downloaded or bundled. The session is created lazily on the first
/// <see cref="DetectAsync"/> call and held until <see cref="Dispose"/>: checking readiness or
/// listing engines never touches ONNX. The layout model id and file are pinned for the session's
/// whole lifetime — a mid-job configuration change cannot retarget a pinned run.
/// </summary>
internal sealed class VisionLayoutDetector : IVisionLayoutDetector
{
    private static readonly string[] YoloLabels =
    [
        "title", "plain text", "abandon", "figure", "figure_caption", "table", "table_caption",
        "table_footnote", "isolate_formula", "formula_caption"
    ];

    private static readonly string[] PaddleLabels =
    [
        "abstract", "algorithm", "aside_text", "chart", "content", "display_formula", "doc_title",
        "figure_title", "footer", "footer_image", "footnote", "formula_number", "header",
        "header_image", "image", "inline_formula", "number", "paragraph_title", "reference",
        "reference_content", "seal", "table", "text", "vertical_text", "vision_footnote"
    ];

    private readonly string _modelId;
    private readonly string _modelPath;
    private readonly object _gate = new();
    private InferenceSession? _session;
    private int _disposed;

    public VisionLayoutDetector(string modelId, string modelPath)
    {
        _modelId = VisionLayoutModels.Normalize(modelId);
        _modelPath = modelPath ?? "";
    }

    /// <summary>
    /// File-level model validation: right model id, an existing .onnx file. No ONNX session is
    /// opened, so this is safe for non-executing checks.
    /// </summary>
    public static Result ValidateModel(string modelId, string modelPath)
    {
        if (modelId is not (VisionLayoutModels.DocLayoutYolo or VisionLayoutModels.PpDocLayoutV3))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed,
                "请选择 DocLayout-YOLO 或 PP-DocLayoutV3 版面模型。");
        }

        return !string.IsNullOrWhiteSpace(modelPath) && File.Exists(modelPath) &&
               string.Equals(Path.GetExtension(modelPath), ".onnx", StringComparison.OrdinalIgnoreCase)
            ? Result.Success()
            : Result.Failure(AppErrorCodes.ValidationFailed,
                "请在「本地文件」页点击所选版面模型的「下载模型」；也可关闭本地版面识别。");
    }

    public Task<Result<IReadOnlyList<VisionLayoutRegion>>> DetectAsync(byte[] imageBytes,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Detect(imageBytes, cancellationToken), cancellationToken);
    }

    private Result<IReadOnlyList<VisionLayoutRegion>> Detect(byte[] imageBytes,
        CancellationToken cancellationToken)
    {
        Result valid = ValidateModel(_modelId, _modelPath);
        if (valid.IsFailure)
        {
            return Result<IReadOnlyList<VisionLayoutRegion>>.Failure(valid.ErrorCode!, valid.ErrorMessage!);
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using SKBitmap? image = SKBitmap.Decode(imageBytes);
                if (image is null)
                {
                    return Result<IReadOnlyList<VisionLayoutRegion>>.Failure(AppErrorCodes.ValidationFailed,
                        "The layout input image could not be decoded.");
                }

                InferenceSession session = EnsureSession();
                bool yolo = _modelId == VisionLayoutModels.DocLayoutYolo;
                string[] requiredInputs = yolo ? ["images"] : ["image", "im_shape", "scale_factor"];
                if (session.InputMetadata.Count != requiredInputs.Length ||
                    requiredInputs.Any(name => !session.InputMetadata.ContainsKey(name)))
                {
                    return Result<IReadOnlyList<VisionLayoutRegion>>.Failure(AppErrorCodes.ValidationFailed,
                        "The ONNX file does not match the selected layout model. Choose the documented model export.");
                }

                int size = yolo ? 1024 : 800;
                DenseTensor<float> pixels =
                    PrepareImage(image, size, yolo, out double scale, out int left, out int top);
                List<NamedOnnxValue> inputs = [NamedOnnxValue.CreateFromTensor(yolo ? "images" : "image", pixels)];
                if (!yolo)
                {
                    inputs.Add(NamedOnnxValue.CreateFromTensor("im_shape",
                        new DenseTensor<float>(new float[] { size, size }, new[] { 1, 2 })));
                    inputs.Add(NamedOnnxValue.CreateFromTensor("scale_factor",
                        new DenseTensor<float>(new[] { size / (float)image.Height, size / (float)image.Width },
                            new[] { 1, 2 })));
                }

                // Only boxes are needed: avoid materializing the V3 segmentation mask output.
                using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs =
                    session.Run(inputs, [session.OutputMetadata.Keys.First()]);
                cancellationToken.ThrowIfCancellationRequested();
                Tensor<float> boxes = outputs.Single().AsTensor<float>();
                int columns = boxes.Dimensions[^1];
                if (columns != (yolo ? 6 : 7))
                {
                    return Result<IReadOnlyList<VisionLayoutRegion>>.Failure(AppErrorCodes.ValidationFailed,
                        "Unsupported layout ONNX output shape; the documented YOLO or official Paddle ONNX export is required.");
                }

                return Result<IReadOnlyList<VisionLayoutRegion>>.Success(
                    DecodeBoxes(boxes.ToArray(), yolo, image.Width, image.Height, scale, left, top));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ObjectDisposedException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return Result<IReadOnlyList<VisionLayoutRegion>>.Failure(AppErrorCodes.ValidationFailed,
                    $"无法运行所选版面模型，请检查 ONNX 文件与模型选项是否对应：{exception.Message}");
            }
        }
    }

    private InferenceSession EnsureSession()
    {
        if (_session is not null)
        {
            return _session;
        }

        FileInfo file = new(_modelPath);
        if (!file.Exists)
        {
            throw new InvalidOperationException("Layout model weights are unavailable.");
        }

        using SessionOptions options = new() { IntraOpNumThreads = 4, InterOpNumThreads = 1 };
        _session = new InferenceSession(file.FullName, options);
        return _session;
    }

    private static DenseTensor<float> PrepareImage(SKBitmap image, int size, bool letterbox,
        out double scale, out int left, out int top)
    {
        scale = letterbox ? Math.Min(size / (double)image.Width, size / (double)image.Height) : 1;
        int width = letterbox ? Math.Max(1, (int)Math.Round(image.Width * scale)) : size;
        int height = letterbox ? Math.Max(1, (int)Math.Round(image.Height * scale)) : size;
        left = (size - width) / 2;
        top = (size - height) / 2;
        using SKBitmap resized = image.Resize(new SKSizeI(width, height),
                                     new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None))
                                 ?? throw new InvalidOperationException("Could not resize the layout image.");
        using SKBitmap canvasBitmap = new(size, size, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (SKCanvas canvas = new(canvasBitmap))
        {
            canvas.Clear(new SKColor(114, 114, 114));
            canvas.DrawBitmap(resized, left, top);
        }

        SKColor[] colors = canvasBitmap.Pixels;
        DenseTensor<float> tensor = new(new[] { 1, 3, size, size });
        Span<float> values = tensor.Buffer.Span;
        int plane = size * size;
        for (int index = 0; index < plane; index++)
        {
            values[index] = colors[index].Red / 255f;
            values[plane + index] = colors[index].Green / 255f;
            values[plane * 2 + index] = colors[index].Blue / 255f;
        }

        return tensor;
    }

    internal static IReadOnlyList<VisionLayoutRegion> DecodeBoxes(float[] values, bool yolo,
        int width, int height, double scale, int left, int top)
    {
        int columns = yolo ? 6 : 7;
        List<VisionLayoutRegion> regions = [];
        for (int offset = 0; offset < values.Length; offset += columns)
        {
            double score = values[offset + (yolo ? 4 : 1)];
            if (!double.IsFinite(score) || score < (yolo ? 0.25 : 0.5))
            {
                continue;
            }

            int label = (int)values[offset + (yolo ? 5 : 0)];
            string[] labels = yolo ? YoloLabels : PaddleLabels;
            if (label < 0 || label >= labels.Length)
            {
                continue;
            }

            int boxOffset = offset + (yolo ? 0 : 2);
            double x0 = values[boxOffset], y0 = values[boxOffset + 1];
            double x1 = values[boxOffset + 2], y1 = values[boxOffset + 3];
            if (yolo)
            {
                x0 = (x0 - left) / scale;
                x1 = (x1 - left) / scale;
                y0 = (y0 - top) / scale;
                y1 = (y1 - top) / scale;
            }

            x0 = Math.Clamp(x0 / width, 0, 1);
            x1 = Math.Clamp(x1 / width, 0, 1);
            y0 = Math.Clamp(y0 / height, 0, 1);
            y1 = Math.Clamp(y1 / height, 0, 1);
            NormalizedBBox bbox = new(x0, y0, x1 - x0, y1 - y0);
            if (bbox.Validate().IsSuccess)
            {
                regions.Add(new VisionLayoutRegion(labels[label], bbox, score,
                    yolo ? null : (int)values[offset + 6]));
            }
        }

        // Both published exports can return duplicate detections. Do not recognize the same region twice.
        List<VisionLayoutRegion> distinct = [];
        foreach (VisionLayoutRegion candidate in regions.OrderByDescending(region => region.Confidence))
        {
            if (distinct.All(region => IntersectionOverUnion(region.BBox, candidate.BBox) < 0.8))
            {
                distinct.Add(candidate);
            }
        }

        return yolo
            ? distinct.OrderBy(region => region.BBox.Y).ThenBy(region => region.BBox.X).ToArray()
            : distinct.OrderBy(region => region.Order).ToArray();
    }

    private static double IntersectionOverUnion(NormalizedBBox a, NormalizedBBox b)
    {
        double intersection = Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X)) *
                              Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y));
        return intersection / (a.Width * a.Height + b.Width * b.Height - intersection);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            lock (_gate)
            {
                _session?.Dispose();
                _session = null;
            }
        }
    }
}
