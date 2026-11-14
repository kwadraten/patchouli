using System.Globalization;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Patchouli.Core.Documents;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.Ocr.Engines.Vision;

namespace Patchouli.Ocr.Engines.OvisOcr3;

public sealed class OvisOcr3OnnxSession : IDisposable
{
    public const string EngineId = "ovisocr3";

    private static readonly string[] RequiredFiles =
    [
        "ovisocr3_int4_single.onnx",
        "tokenizer.json",
        "visual_pos_embed.bf16",
        "document_structured.txt"
    ];

    private static readonly float[] ZeroConv = new float[18 * 6144 * 3];
    private static readonly float[] ZeroRec = new float[18 * 16 * 128 * 128];

    private static readonly HashSet<string> Categories = new(StringComparer.Ordinal)
    {
        "abstract", "algorithm", "aside_text", "chart", "content", "display_formula", "doc_title",
        "figure_title", "footer", "footer_image", "footnote", "formula_number", "header",
        "header_image", "image", "inline_formula", "number", "paragraph_title",
        "reference_content", "seal", "table", "text", "vertical_text", "vision_footnote"
    };

    private readonly string _modelDirectory;
    private readonly int _maxNewTokens;
    private readonly Lazy<InferenceSession> _session;
    private readonly OvisOcr3Tokenizer _tokenizer;
    private readonly OvisOcr3ImageProcessor _imageProcessor;
    private int _sourceOrder;
    private int _disposed;

    public OvisOcr3OnnxSession(ExtensionConfigSnapshot config)
    {
        Result ready = CheckReadiness(config);
        if (ready.IsFailure)
        {
            throw new InvalidOperationException(ready.ErrorMessage);
        }

        _modelDirectory = config.Values["model-path"];
        _maxNewTokens = MaxNewTokens(config);
        _tokenizer = new OvisOcr3Tokenizer(Path.Combine(_modelDirectory, "tokenizer.json"));
        _imageProcessor = new OvisOcr3ImageProcessor(Path.Combine(_modelDirectory, "visual_pos_embed.bf16"));
        _session = new Lazy<InferenceSession>(CreateSession);
    }

    public static OcrEngineDefinition GetDefinition()
    {
        return new OcrEngineDefinition(
            EngineId, "OvisOCR3 (ONNX)", "local_library", "ovisocr3-int4",
            ["page_image", "image_file", "region_image"],
            RequiresModelPath: true, SupportsVerticalText: true, SupportsTableDetection: true,
            ModelDirectoryKind: "ovisocr3-models",
            Notes:
            "Runs the pinned community OvisOCR3 INT4 ONNX graph entirely offline with local tokenizer and visual position embeddings.",
            ModelResources:
            [
                new OcrModelResource("ovisocr3_int4_single.onnx",
                    "arlenecc/OvisOCR3-ONNX-int4@e654df1eed2c3b4a902eac7b458bddd7cb338e1b",
                    "7bafbef06e85847963d46fe520ba9767d18c7d2abdad91a25ce2f036558bcb3e"),
                new OcrModelResource("visual_pos_embed.bf16",
                    "ATH-MaaS/OvisOCR3@4833d5d2afa6e769ee933f024504d67ace80a24c",
                    "5f6b1b11dc866dc90f773a3e95c7b0ae077cd1744161bccbdc1302050986c287"),
                new OcrModelResource("tokenizer.json",
                    "ATH-MaaS/OvisOCR3@4833d5d2afa6e769ee933f024504d67ace80a24c",
                    "5f9e4d4901a92b997e463c1f46055088b6cca5ca61a6522d1b9f64c4bb81cb42"),
                new OcrModelResource("document_structured.txt",
                    "ATH-MaaS/OvisOCR3@4833d5d2afa6e769ee933f024504d67ace80a24c",
                    "1b6139ec72888e087c8af75f2e2dfb78b425e15ae1c1529940b96137613d1db1")
            ]);
    }

    public static Result CheckReadiness(ExtensionConfigSnapshot config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!config.Values.TryGetValue("model-path", out string? directory) ||
            string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return Result.Failure(AppErrorCodes.NotFound,
                "OvisOCR3 requires a local model directory prepared with the ONNX model, tokenizer, and visual position embeddings.");
        }

        foreach (string name in RequiredFiles)
        {
            string path = Path.Combine(directory, name);
            long expected = name switch
            {
                "ovisocr3_int4_single.onnx" => 1216759027,
                "visual_pos_embed.bf16" => 3538944,
                _ => 0
            };
            if (!File.Exists(path) || (expected != 0 && new FileInfo(path).Length != expected))
            {
                return Result.Failure(AppErrorCodes.NotFound,
                    $"The OvisOCR3 model file '{name}' is missing or incomplete in '{directory}'. Run dotnet fsi scripts/prepare-ovisocr3-model.fsx -- --model-directory with this model directory.");
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
        OvisOcr3Image prepared = _imageProcessor.Prepare(image.Data.Span);
        string prompt = File.ReadAllText(Path.Combine(_modelDirectory, "document_structured.txt"));
        long[] ids = _tokenizer.EncodePrompt(prompt, prepared.ImageTokenCount);
        float[] visionTokens = RunVision(prepared);
        float[] embeddings = RunEmbeddings(ids);
        for (int index = 0; index < ids.Length; index++)
        {
            if (ids[index] == 248056)
            {
                int visualIndex = index - Array.IndexOf(ids, 248056L);
                Array.Copy(visionTokens, visualIndex * 1024, embeddings, index * 1024, 1024);
            }
        }

        OvisOcr3TextPositions positions = OvisOcr3Positions.Prepare(ids, prepared);
        State state = new(0, [], [], ZeroConv, ZeroRec);
        int offset = 0;
        int nextId = -1;
        while (ids.Length - offset >= 32)
        {
            cancellationToken.ThrowIfCancellationRequested();
            nextId = Advance(1, [], embeddings.AsSpan(offset * 1024, 32 * 1024).ToArray(),
                positions.Cos.AsSpan(offset * 64, 32 * 64).ToArray(),
                positions.Sin.AsSpan(offset * 64, 32 * 64).ToArray(), 32, ref state);
            offset += 32;
        }

        while (offset < ids.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ids[offset] == 248056)
            {
                throw new InvalidDataException("OvisOCR3 cannot decode an image token in step mode.");
            }

            nextId = Advance(2, [ids[offset]], [],
                positions.Cos.AsSpan(offset * 64, 64).ToArray(),
                positions.Sin.AsSpan(offset * 64, 64).ToArray(), 1, ref state);
            offset++;
        }

        (float[] decodeCos, float[] decodeSin) = OvisOcr3Positions.Decode(positions.Next, _maxNewTokens);
        List<long> generated = [];
        for (int index = 0; index < _maxNewTokens; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            generated.Add(nextId);
            if (nextId is 248044 or 248046)
            {
                return Parse(_tokenizer.Decode(generated), input,
                    Interlocked.Add(ref _sourceOrder, 1 << 16) - (1 << 16));
            }

            nextId = Advance(2, [nextId], [],
                decodeCos.AsSpan(index * 64, 64).ToArray(),
                decodeSin.AsSpan(index * 64, 64).ToArray(), 1, ref state);
        }

        throw new InvalidDataException("OvisOCR3 stopped before producing a complete structured response.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _session.IsValueCreated)
        {
            _session.Value.Dispose();
        }
    }

    internal static OcrExtensionBoxCandidate[] Parse(string reply, OcrJobInput input, int sourceOrder = 0)
    {
        using JsonDocument document = JsonDocument.Parse(reply);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("OvisOCR3 did not return a JSON array.");
        }

        List<OcrExtensionBoxCandidate> candidates = [];
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("bbox", out JsonElement coords) ||
                coords.ValueKind != JsonValueKind.Array || coords.GetArrayLength() != 4 ||
                !item.TryGetProperty("category", out JsonElement category) ||
                category.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("content", out JsonElement content) ||
                content.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(content.GetString()))
            {
                throw new InvalidDataException("OvisOCR3 returned an incomplete structured OCR item.");
            }

            int[] values = coords.EnumerateArray().Select(coordinate =>
                coordinate.ValueKind == JsonValueKind.Number && coordinate.TryGetInt32(out int number)
                    ? number
                    : -1).ToArray();
            if (values.Any(value => value < 0 || value > 1000) ||
                values[0] >= values[2] || values[1] >= values[3])
            {
                throw new InvalidDataException("OvisOCR3 returned an invalid bounding box.");
            }

            string kind = category.GetString()!;
            if (!Categories.Contains(kind))
            {
                throw new InvalidDataException("OvisOCR3 returned an unsupported box category.");
            }

            string text = content.GetString()!;
            NormalizedBBox local = new(values[0] / 1000.0, values[1] / 1000.0,
                (values[2] - values[0]) / 1000.0, (values[3] - values[1]) / 1000.0);
            NormalizedBBox bbox = VisionImaging.RegionBoxNormalized(input.Region, local);
            VisionTypedItem typed = new(kind == "seal" ? "image" : kind, null, text, text,
                kind is "display_formula" or "inline_formula" ? text : null,
                kind == "table" ? text : null, null, null, null, bbox, null);
            DocumentBoxSeed seed = VisionResultParser.ToSeed(typed, bbox, sourceOrder + candidates.Count + 1)
                                   ?? throw new InvalidDataException("OvisOCR3 returned an unsupported box category.");
            candidates.Add(new OcrExtensionBoxCandidate(input.Id, seed));
        }

        return candidates.ToArray();
    }

    private static int MaxNewTokens(ExtensionConfigSnapshot config)
    {
        if (!config.Values.TryGetValue("max-new-tokens", out string? value) || string.IsNullOrWhiteSpace(value))
        {
            return 4096;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) ||
            count is < 1 or > 8192)
        {
            throw new ArgumentOutOfRangeException(nameof(config), "OvisOCR3 max-new-tokens must be in [1, 8192].");
        }

        return count;
    }

    private InferenceSession CreateSession()
    {
        SessionOptions options = new()
        {
            IntraOpNumThreads = 4,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
        };
        using (options)
        {
            return new InferenceSession(Path.Combine(_modelDirectory, "ovisocr3_int4_single.onnx"), options);
        }
    }

    private float[] RunVision(OvisOcr3Image image)
    {
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> output =
            Run(0, image, [], [], [], [], 0, new State(0, [], [], ZeroConv, ZeroRec));
        float[] visionTokens = output.First(value => value.Name == "vision_tokens").AsTensor<float>().ToArray();
        if (visionTokens.Length != image.ImageTokenCount * 1024)
        {
            throw new InvalidDataException("The OvisOCR3 visual embeddings do not match the image grid.");
        }

        return visionTokens;
    }

    private float[] RunEmbeddings(long[] ids)
    {
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> output =
            Run(3, null, ids, [], [], [], 0, new State(0, [], [], ZeroConv, ZeroRec));
        float[] embeddings = output.First(value => value.Name == "emb_out").AsTensor<float>().ToArray();
        if (embeddings.Length != ids.Length * 1024)
        {
            throw new InvalidDataException("OvisOCR3 returned invalid token embeddings.");
        }

        return embeddings;
    }

    private int Advance(int mode, long[] ids, float[] embeds, float[] cos, float[] sin,
        int length, ref State state)
    {
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> output =
            Run(mode, null, ids, embeds, cos, sin, length, state);
        Tensor<float> logits = output.First(value => value.Name == "logits").AsTensor<float>();
        int sequenceLength = logits.Dimensions[1];
        if (sequenceLength == 0)
        {
            throw new InvalidDataException("OvisOCR3 produced empty decoding logits.");
        }

        float best = float.NegativeInfinity;
        int nextId = -1;
        for (int token = 0; token < 248320; token++)
        {
            float score = logits[0, sequenceLength - 1, token];
            if (score > best)
            {
                best = score;
                nextId = token;
            }
        }

        float[] key = output.First(value => value.Name == "present_key").AsTensor<float>().ToArray();
        float[] values = output.First(value => value.Name == "present_value").AsTensor<float>().ToArray();
        int pastLength = key.Length / (6 * 1 * 2 * 256);
        state = new State(pastLength, key, values,
            output.First(value => value.Name == "conv_state_out").AsTensor<float>().ToArray(),
            output.First(value => value.Name == "rec_state_out").AsTensor<float>().ToArray());
        return nextId;
    }

    private IDisposableReadOnlyCollection<DisposableNamedOnnxValue> Run(int mode, OvisOcr3Image? image,
        long[] ids, float[] embeds, float[] cos, float[] sin, int length, State state)
    {
        int patches = image?.GridHeight * image?.GridWidth ?? 0;
        List<NamedOnnxValue> inputs =
        [
            NamedOnnxValue.CreateFromTensor("mode", new DenseTensor<long>(new long[] { mode }, [])),
            NamedOnnxValue.CreateFromTensor("pixel_values",
                new DenseTensor<float>(image?.Pixels ?? [], [patches, 1536])),
            NamedOnnxValue.CreateFromTensor("pos_embeds",
                new DenseTensor<float>(image?.PositionEmbeddings ?? [], [patches, 768])),
            NamedOnnxValue.CreateFromTensor("position_ids",
                new DenseTensor<long>(image?.VisionPositionIds ?? [], [patches, 2])),
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids, [ids.Length])),
            NamedOnnxValue.CreateFromTensor("inputs_embeds",
                new DenseTensor<float>(embeds, [1, embeds.Length / 1024, 1024])),
            NamedOnnxValue.CreateFromTensor("cos", new DenseTensor<float>(cos, [1, length, 64])),
            NamedOnnxValue.CreateFromTensor("sin", new DenseTensor<float>(sin, [1, length, 64])),
            NamedOnnxValue.CreateFromTensor("past_key",
                new DenseTensor<float>(state.Key, [6, 1, 2, state.PastLength, 256])),
            NamedOnnxValue.CreateFromTensor("past_value",
                new DenseTensor<float>(state.Value, [6, 1, 2, state.PastLength, 256])),
            NamedOnnxValue.CreateFromTensor("conv_state",
                new DenseTensor<float>(state.Conv, [18, 1, 6144, 3])),
            NamedOnnxValue.CreateFromTensor("rec_state",
                new DenseTensor<float>(state.Rec, [18, 1, 16, 128, 128]))
        ];
        return _session.Value.Run(inputs);
    }

    private sealed record State(int PastLength, float[] Key, float[] Value, float[] Conv, float[] Rec);
}
