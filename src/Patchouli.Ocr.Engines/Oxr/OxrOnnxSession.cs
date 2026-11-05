using System.Globalization;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Patchouli.Core.Documents;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.Infrastructure.Ocr.NdlKoten;
using Patchouli.Ocr.Engines.Vision;
using SkiaSharp;

namespace Patchouli.Ocr.Engines.Oxr;

public sealed class OxrOnnxSession : IDisposable
{
    public const string EngineId = "oxr";
    private const string ModelRevision = "kwadraten/OXR-1.0-ONNX@f6a1cd8115cd4ff3d34b6e0a7d366677a8f7cee2";

    private static readonly string[] RequiredFiles =
    [
        "oxr-vision-448.onnx", "oxr-vision-448.onnx.data",
        "oxr-text-embedding.onnx", "oxr-text-embedding.onnx.data",
        "oxr-language.onnx", "oxr-language.onnx.data",
        "oxr-layout.onnx", "layout-metadata.json",
        "oxr-indicators.f32", "oxr-prompt.json", "tokenizer.json",
        "LICENSE", "NOTICE", "LICENSES/Apache-2.0.txt",
        "LICENSES/D-FINE-NOTICES.md"
    ];

    private static readonly Dictionary<string, long> RequiredSizes = new(StringComparer.Ordinal)
    {
        ["oxr-vision-448.onnx"] = 4783722,
        ["oxr-vision-448.onnx.data"] = 3130974168,
        ["oxr-language.onnx"] = 1335139,
        ["oxr-language.onnx.data"] = 2384199680,
        ["oxr-text-embedding.onnx"] = 295,
        ["oxr-text-embedding.onnx.data"] = 622329856,
        ["oxr-layout.onnx"] = 78605372,
        ["oxr-indicators.f32"] = 16384,
        ["tokenizer.json"] = 11422654
    };

    private readonly Lazy<InferenceSession> _vision;
    private readonly Lazy<InferenceSession> _embedding;
    private readonly Lazy<InferenceSession> _language;
    private readonly Lazy<InferenceSession> _layout;
    private readonly OxrTokenizer _tokenizer;
    private readonly long[] _prefix;
    private readonly long[] _suffix;
    private readonly float[] _indicatorVectors;
    private readonly int _maxNewTokens;
    private int _sourceOrder;
    private int _disposed;

    public OxrOnnxSession(ExtensionConfigSnapshot config)
    {
        Result ready = CheckReadiness(config);
        if (ready.IsFailure)
        {
            throw new InvalidOperationException(ready.ErrorMessage);
        }

        string directory = config.Values["model-path"];
        _maxNewTokens = MaxNewTokens(config);
        _tokenizer = new OxrTokenizer(Path.Combine(directory, "tokenizer.json"));
        using JsonDocument prompt = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(directory, "oxr-prompt.json")));
        JsonElement root = prompt.RootElement;
        _prefix = root.GetProperty("prefix").EnumerateArray().Select(value => value.GetInt64()).ToArray();
        _suffix = root.GetProperty("suffix").EnumerateArray().Select(value => value.GetInt64()).ToArray();
        if (root.GetProperty("image_begin_indicator").GetInt32() != -301 ||
            root.GetProperty("image_atom").GetInt32() != -300 ||
            root.GetProperty("image_end_indicator").GetInt32() != -302 ||
            root.GetProperty("visual_atom_count").GetInt32() != 196 ||
            root.GetProperty("end_of_turn_token").GetInt32() != 151645)
        {
            throw new InvalidDataException("The OXR prompt is incompatible with the exported ONNX model.");
        }

        byte[] indicators = File.ReadAllBytes(Path.Combine(directory, "oxr-indicators.f32"));
        if (indicators.Length != 4 * 1024 * sizeof(float))
        {
            throw new InvalidDataException("The OXR visual indicator embeddings have an invalid size.");
        }

        _indicatorVectors = new float[4 * 1024];
        Buffer.BlockCopy(indicators, 0, _indicatorVectors, 0, indicators.Length);
        _vision = new Lazy<InferenceSession>(() => CreateSession(directory, "oxr-vision-448.onnx"));
        _embedding = new Lazy<InferenceSession>(() => CreateSession(directory, "oxr-text-embedding.onnx"));
        _language = new Lazy<InferenceSession>(() => CreateSession(directory, "oxr-language.onnx"));
        _layout = new Lazy<InferenceSession>(() => CreateSession(directory, "oxr-layout.onnx"));
    }

    public static OcrEngineDefinition GetDefinition()
    {
        return new OcrEngineDefinition(
            EngineId, "OXR (ONNX)", "local_library", "oxr-1.0",
            ["page_image", "image_file", "region_image"],
            RequiresModelPath: true, SupportsVerticalText: true, SupportsTableDetection: true,
            ModelDirectoryKind: "oxr-models",
            Notes:
            "Runs D-FINE layout, XY-cut reading order, fixed-448-pixel OXR recognition and text generation offline with ONNX Runtime.",
            ModelResources:
            [
                new OcrModelResource("oxr-vision-448.onnx", ModelRevision,
                    "21693fa5b4cc52e4b8ecdec3dfad45425d563fd938a0ad17534c66529f45562f"),
                new OcrModelResource("oxr-vision-448.onnx.data", ModelRevision,
                    "d8e46e2e061276b18553106fbff7a4e60c374a41f11cd97204381c1833c804da"),
                new OcrModelResource("oxr-text-embedding.onnx", ModelRevision,
                    "9a17f7461aff5e2682c646c64a8983d0725a3f61c800e2b7c1e1deb9a71dc2a4"),
                new OcrModelResource("oxr-text-embedding.onnx.data", ModelRevision,
                    "066a9dbb18ff50788d8b3f9f2259ec82d0ec9577f8ba37cb4a4263a06ff9ee45"),
                new OcrModelResource("oxr-language.onnx", ModelRevision,
                    "04d32cfe162a813fb04b9c535eb74c7989a011eedb96077ce17751857b16ade9"),
                new OcrModelResource("oxr-language.onnx.data", ModelRevision,
                    "c4f3d40e82573811af472aeccfefa97d05881efa5463175e7d02564c5d1cc962"),
                new OcrModelResource("oxr-layout.onnx", ModelRevision,
                    "f5db09a5bd81fb1e419cdfc98d5e1ca0862e6a146c3317876d7f73ac32002e48"),
                new OcrModelResource("oxr-indicators.f32", ModelRevision,
                    "1a319aa1d58d977f63e6c7eb7e4ddcef5f82f2f180a90933f2a5110fedf1409f"),
                new OcrModelResource("oxr-prompt.json", ModelRevision,
                    "af5b244cc91124a0d84ac396be5cf9e2363430adae138a14d184ddf8e637b1c3"),
                new OcrModelResource("tokenizer.json", ModelRevision,
                    "aeb13307a71acd8fe81861d94ad54ab689df773318809eed3cbe794b4492dae4"),
                new OcrModelResource("layout-metadata.json", ModelRevision,
                    "a1a5552d47f217d585711fe7d8adb89273784fcb4388cd4d23ee288f13f34dc3")
            ]);
    }

    public static Result CheckReadiness(ExtensionConfigSnapshot config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!config.Values.TryGetValue("model-path", out string? directory) ||
            string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return Result.Failure(AppErrorCodes.NotFound,
                "OXR requires a local directory containing its pinned ONNX model files.");
        }

        foreach (string name in RequiredFiles)
        {
            string file = Path.Combine(directory, name);
            if (!File.Exists(file) ||
                (RequiredSizes.TryGetValue(name, out long expected) && new FileInfo(file).Length != expected))
            {
                return Result.Failure(AppErrorCodes.NotFound,
                    $"The OXR model file '{name}' is missing or incomplete in '{directory}'.");
            }
        }

        try
        {
            _ = MaxNewTokens(config);
        }
        catch (ArgumentOutOfRangeException error)
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, error.Message);
        }

        return Result.Success();
    }

    public OcrExtensionBoxCandidate[] Recognize(OcrJobInput input, OcrInputImage image,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(image);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        using SKBitmap source = NativeImageDecode.DecodeOriented(image.Data.Span)
                                ?? throw new InvalidDataException("Could not decode the OXR input image.");
        List<OxrLayoutRegion> regions = [];
        foreach (OxrLayoutRegion region in OxrLayoutAnalyzer.Detect(source, _layout.Value))
        {
            if (region.Category is not (0 or 1 or 2 or 3 or 4 or 5 or 7 or 8 or 9) ||
                regions.Any(selected => IntersectionOverUnion(selected.Box, region.Box) > 0.8))
            {
                continue;
            }

            regions.Add(region);
        }

        if (regions.Count == 0 || regions.Count > 32)
        {
            OcrExtensionBoxCandidate? page = ToCandidate(
                RecognizeText(image.Data.Span, cancellationToken), input, input.Region, "text", null);
            return page is null ? [] : [page];
        }

        Box[] boxes = regions.Select(region =>
            new Box(region.Left, region.Top, region.Right, region.Bottom)).ToArray();
        int[] ranks = ReadingOrderSolver.Solve(boxes);
        List<OcrExtensionBoxCandidate> candidates = [];
        foreach (int index in Enumerable.Range(0, regions.Count).OrderBy(index => ranks[index]))
        {
            cancellationToken.ThrowIfCancellationRequested();
            OxrLayoutRegion region = regions[index];
            using SKBitmap crop = VisionImaging.CropBitmap(source, region.Box);
            string text = RecognizeText(VisionImaging.EncodePng(crop), cancellationToken);
            NormalizedBBox bbox = VisionImaging.RegionBoxNormalized(input.Region, region.Box);
            OcrExtensionBoxCandidate? candidate = ToCandidate(
                text, input, bbox, Category(region.Category), region.Score);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }

        if (candidates.Count == 0)
        {
            OcrExtensionBoxCandidate? page = ToCandidate(
                RecognizeText(image.Data.Span, cancellationToken), input, input.Region, "text", null);
            return page is null ? [] : [page];
        }

        return candidates.ToArray();
    }

    private string RecognizeText(ReadOnlySpan<byte> imageBytes, CancellationToken cancellationToken)
    {
        float[] pixels = OxrImageProcessor.Prepare(imageBytes);
        float[] visual = RunVision(pixels);
        long[] tokens =
        [
            .. _prefix,
            -301,
            .. Enumerable.Repeat(-300L, OxrImageProcessor.PatchCount / 4),
            -302,
            .. _suffix
        ];
        long[] textTokens = tokens.Select(token => Math.Max(token, 0)).ToArray();
        float[] embeddings = RunEmbedding(textTokens);
        const int width = 1024;
        int beginning = _prefix.Length;
        Array.Copy(_indicatorVectors, 0, embeddings, beginning * width, width);
        Array.Copy(visual, 0, embeddings, (beginning + 1) * width, visual.Length);
        Array.Copy(_indicatorVectors, width, embeddings,
            (beginning + 1 + OxrImageProcessor.PatchCount / 4) * width, width);

        float[][] keys = new float[28][];
        float[][] values = new float[28][];
        for (int layer = 0; layer < keys.Length; layer++)
        {
            keys[layer] = [];
            values[layer] = [];
        }

        int pastLength = 0;
        int sequence = tokens.Length;
        long[] positions = Enumerable.Range(0, sequence).Select(value => (long)value).ToArray();
        List<long> generated = [];
        bool finished = false;
        for (int step = 0; step < _maxNewTokens; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long next = Advance(embeddings, positions, pastLength, keys, values,
                cancellationToken);
            pastLength += sequence;
            if (next is 151645 or 151643)
            {
                finished = true;
                break;
            }

            generated.Add(next);
            embeddings = RunEmbedding([next]);
            positions = [pastLength];
            sequence = 1;
        }

        if (!finished)
        {
            throw new InvalidDataException("OXR stopped before producing an end-of-turn token.");
        }

        return _tokenizer.Decode(generated).Trim();
    }

    private OcrExtensionBoxCandidate? ToCandidate(string text, OcrJobInput input,
        NormalizedBBox bbox, string category, double? confidence)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        VisionTypedItem typed = new(category, null, text, text, null, null, null, null,
            null, bbox, confidence);
        DocumentBoxSeed seed = VisionResultParser.ToSeed(
                                   typed, bbox, Interlocked.Increment(ref _sourceOrder))
                               ?? throw new InvalidDataException("OXR returned an invalid text box.");
        return new OcrExtensionBoxCandidate(input.Id, seed);
    }

    private static double IntersectionOverUnion(NormalizedBBox first, NormalizedBBox second)
    {
        double width = Math.Max(0, Math.Min(first.X + first.Width, second.X + second.Width) -
                                   Math.Max(first.X, second.X));
        double height = Math.Max(0, Math.Min(first.Y + first.Height, second.Y + second.Height) -
                                    Math.Max(first.Y, second.Y));
        double intersection = width * height;
        return intersection / (first.Width * first.Height + second.Width * second.Height - intersection);
    }

    private static string Category(int category)
    {
        return category switch
        {
            0 => "image_caption",
            1 => "footnote",
            2 => "formula",
            4 => "footer",
            5 => "header",
            7 => "title",
            8 => "table",
            9 => "code",
            _ => "text"
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (Lazy<InferenceSession> session in new[] { _vision, _embedding, _language, _layout })
        {
            if (session.IsValueCreated)
            {
                session.Value.Dispose();
            }
        }
    }

    private static InferenceSession CreateSession(string directory, string name)
    {
        using SessionOptions options = new()
        {
            IntraOpNumThreads = 4,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
        };
        return new InferenceSession(Path.Combine(directory, name), options);
    }

    private static int MaxNewTokens(ExtensionConfigSnapshot config)
    {
        if (!config.Values.TryGetValue("max-new-tokens", out string? value) || string.IsNullOrWhiteSpace(value))
        {
            return 512;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) ||
            count is < 1 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(config), "OXR max-new-tokens must be in [1, 4096].");
        }

        return count;
    }

    private float[] RunVision(float[] pixels)
    {
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> output = _vision.Value.Run(
        [
            NamedOnnxValue.CreateFromTensor("pixel_values",
                new DenseTensor<float>(pixels, [OxrImageProcessor.PatchCount, OxrImageProcessor.PatchElements]))
        ]);
        float[] visual = output.Single(value => value.Name == "visual_embeddings")
            .AsTensor<float>().ToArray();
        if (visual.Length != OxrImageProcessor.PatchCount / 4 * 1024)
        {
            throw new InvalidDataException("OXR returned an invalid number of visual embeddings.");
        }

        return visual;
    }

    private float[] RunEmbedding(long[] ids)
    {
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> output = _embedding.Value.Run(
        [
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids, [1, ids.Length]))
        ]);
        float[] embeddings = output.Single(value => value.Name == "embeddings")
            .AsTensor<float>().ToArray();
        if (embeddings.Length != ids.Length * 1024)
        {
            throw new InvalidDataException("OXR returned invalid token embeddings.");
        }

        return embeddings;
    }

    private long Advance(float[] embeddings, long[] positions, int pastLength,
        float[][] keys, float[][] values, CancellationToken cancellationToken)
    {
        int sequence = positions.Length;
        int total = pastLength + sequence;
        float[] bias = new float[sequence * total];
        for (int row = 0; row < sequence; row++)
        {
            for (int column = pastLength + row + 1; column < total; column++)
            {
                bias[row * total + column] = -1e4f;
            }
        }

        List<NamedOnnxValue> inputs =
        [
            NamedOnnxValue.CreateFromTensor("embeddings",
                new DenseTensor<float>(embeddings, [1, sequence, 1024])),
            NamedOnnxValue.CreateFromTensor("bias",
                new DenseTensor<float>(bias, [1, 1, sequence, total])),
            NamedOnnxValue.CreateFromTensor("positions",
                new DenseTensor<long>(positions, [1, sequence]))
        ];
        for (int layer = 0; layer < keys.Length; layer++)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor($"past.{layer}.key",
                new DenseTensor<float>(keys[layer], [1, 8, pastLength, 128])));
            inputs.Add(NamedOnnxValue.CreateFromTensor($"past.{layer}.value",
                new DenseTensor<float>(values[layer], [1, 8, pastLength, 128])));
        }

        cancellationToken.ThrowIfCancellationRequested();
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> output = _language.Value.Run(inputs);
        float[] logits = output.Single(value => value.Name == "logits").AsTensor<float>().ToArray();
        int best = 0;
        for (int index = 1; index < logits.Length; index++)
        {
            if (logits[index] > logits[best])
            {
                best = index;
            }
        }

        for (int layer = 0; layer < keys.Length; layer++)
        {
            keys[layer] = output.Single(value => value.Name == $"present.{layer}.key")
                .AsTensor<float>().ToArray();
            values[layer] = output.Single(value => value.Name == $"present.{layer}.value")
                .AsTensor<float>().ToArray();
        }

        return best;
    }
}
