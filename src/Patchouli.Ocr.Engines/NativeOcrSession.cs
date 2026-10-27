using System.Globalization;
using System.Text.Json;
using Patchouli.Core.Documents;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.Infrastructure.Ocr.NdlKoten;
using Patchouli.Infrastructure.Ocr.NdlLite;
using Patchouli.Infrastructure.Ocr.RapidOcr;
using SkiaSharp;

namespace Patchouli.Ocr.Engines;

/// <summary>
/// OCR input-kind constants mirrored from <c>Patchouli.Ocr.OcrInputKinds</c> (this leaf assembly
/// cannot reference Patchouli.Ocr). Internal so it can never collide with the domain or contract
/// namespaces; the values are pinned by unit tests against the source class.
/// </summary>
internal static class NativeOcrInputKinds
{
    public const string PageImage = "page_image";
    public const string PdfPage = "pdf_page";
    public const string ImageFile = "image_file";
    public const string RegionImage = "region_image";
}

/// <summary>
/// One bound native OCR engine session. This is the public facade compiled extension packages
/// use to reach the ONNX-backed engines shipped in this support assembly: configuration comes
/// exclusively from the run-pinned <see cref="ExtensionConfigSnapshot"/> (typed keys overlay
/// the legacy <c>parameters-json</c> payload, which overlays upstream defaults), image input
/// arrives as <see cref="OcrInputImage"/> bytes decoded in memory with EXIF orientation applied,
/// and crop-local recognition geometry is mapped back into <see cref="OcrJobInput.Region"/>
/// normalized page coordinates. Model weights stay in the configured model directory and are
/// never copied into a session or package.
/// </summary>
/// <remarks>
/// <see cref="GetDefinition"/> and <see cref="CheckReadiness"/> never create ONNX sessions;
/// the pipeline is built lazily on the first <see cref="Recognize"/> call and held until
/// <see cref="Dispose"/> runs after the job's calls have exited.
/// </remarks>
public sealed class NativeOcrSession : IDisposable
{
    /// <summary>RapidOCR engine id; kept equal to <c>Patchouli.Ocr.OcrEngineIds.RapidOcr</c>.</summary>
    public const string RapidOcrEngineId = "rapidocr";

    /// <summary>NDLOCR-Lite engine id; kept equal to <c>Patchouli.Ocr.OcrEngineIds.NdlLite</c>.</summary>
    public const string NdlOcrLiteEngineId = "ndlocr-lite";

    /// <summary>NDLKotenOCR-Lite engine id; kept equal to <c>Patchouli.Ocr.OcrEngineIds.NdlKoten</c>.</summary>
    public const string NdlKotenLiteEngineId = "ndl-koten";

    // OcrAdapterKind.LocalLibrary / CloudApi values; this leaf assembly cannot reference
    // Patchouli.Ocr, so the literals are mirrored and pinned by unit tests.
    private const string LocalLibraryAdapter = "local_library";

    private static readonly string[] ImageInputKinds =
        [NativeOcrInputKinds.PageImage, NativeOcrInputKinds.ImageFile, NativeOcrInputKinds.RegionImage];

    private readonly string _engineId;
    private readonly string _modelsDirectory;
    private readonly RapidOcrParameters _rapidParameters;
    private readonly object _pipelineGate = new();
    private IDisposable? _pipeline;
    private int _sourceOrder;
    private int _disposed;

    /// <summary>
    /// Binds one engine to the frozen job configuration. Resolving configuration validates it;
    /// model files and ONNX sessions are not touched until the first recognition call.
    /// </summary>
    /// <exception cref="InvalidOperationException">The engine id is unknown or the resolved configuration is invalid.</exception>
    public NativeOcrSession(string engineId, ExtensionConfigSnapshot config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engineId);
        ArgumentNullException.ThrowIfNull(config);
        _engineId = NormalizeEngineId(engineId);
        ResolvedNativeConfig resolved = Resolve(_engineId, config.Values);
        _modelsDirectory = resolved.ModelsDirectory;
        _rapidParameters = resolved.RapidParameters;
    }

    /// <summary>Declarative engine metadata; pure constants, no sessions or IO.</summary>
    public static OcrEngineDefinition GetDefinition(string engineId)
    {
        return NormalizeEngineId(engineId) switch
        {
            RapidOcrEngineId => new OcrEngineDefinition(
                RapidOcrEngineId,
                "RapidOCR",
                LocalLibraryAdapter,
                "rapidocr-default",
                ImageInputKinds,
                RequiresModelPath: true,
                SupportsVerticalText: true,
                ModelDirectoryKind: "rapidocr-models",
                Notes: RapidOcrModelFiles.Attribution,
                ModelResources: RapidOcrModelFiles.Files.Select(file =>
                    new OcrModelResource(file.FileName, "RapidOCR/v3.9.2", file.Sha256)).ToArray()),
            NdlOcrLiteEngineId => new OcrEngineDefinition(
                NdlOcrLiteEngineId,
                "NDLOCR-Lite",
                LocalLibraryAdapter,
                "ndlocr-lite-default",
                ImageInputKinds,
                RequiresModelPath: true,
                SupportsVerticalText: true,
                ModelDirectoryKind: "ndlocr-lite-models",
                Notes: NdlLiteModelFiles.Attribution,
                ModelResources: NdlLiteModelFiles.Files.Select(file =>
                    new OcrModelResource(file.RelativePath, NdlLiteModelFiles.UpstreamCommit, file.Sha256)).ToArray()),
            NdlKotenLiteEngineId => new OcrEngineDefinition(
                NdlKotenLiteEngineId,
                "NDL古典籍OCR-Lite",
                LocalLibraryAdapter,
                "ndl-koten-default",
                ImageInputKinds,
                RequiresModelPath: true,
                SupportsVerticalText: true,
                ModelDirectoryKind: "ndl-koten-models",
                Notes: NdlKotenModelFiles.Attribution,
                ModelResources: NdlKotenModelFiles.Files.Select(file =>
                    new OcrModelResource(file.RelativePath)).ToArray()),
            _ => throw new ArgumentException($"Unknown native OCR engine id '{engineId}'.", nameof(engineId))
        };
    }

    /// <summary>
    /// Non-executing readiness probe: resolves the configured model directory and checks the
    /// engine's manifest files exist at their pinned sizes. No ONNX session is created, nothing
    /// is downloaded, and a missing or incomplete model set is an actionable failure, never a
    /// silent fallback to another model.
    /// </summary>
    public static Result CheckReadiness(string engineId, ExtensionConfigSnapshot config)
    {
        ArgumentNullException.ThrowIfNull(config);
        string normalized;
        try
        {
            normalized = NormalizeEngineId(engineId);
        }
        catch (ArgumentException exception)
        {
            return Result.Failure(AppErrorCodes.InvalidArgument, exception.Message);
        }

        ResolvedNativeConfig resolved;
        try
        {
            resolved = Resolve(normalized, config.Values);
        }
        catch (InvalidOperationException exception)
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, exception.Message);
        }

        string displayName = GetDefinition(normalized).DisplayName;
        if (string.IsNullOrWhiteSpace(resolved.ModelsDirectory))
        {
            return Result.Failure(AppErrorCodes.NotFound,
                $"{displayName} requires a model directory. Set the preset's model path or download the models in Settings > Local Files.");
        }

        if (!Directory.Exists(resolved.ModelsDirectory))
        {
            return Result.Failure(AppErrorCodes.NotFound,
                $"The {displayName} model directory '{resolved.ModelsDirectory}' does not exist or is inaccessible. Download the models in Settings > Local Files.");
        }

        return normalized switch
        {
            RapidOcrEngineId => CheckFiles(displayName, resolved.ModelsDirectory,
                RapidOcrModelFiles.GetMissing(resolved.ModelsDirectory, resolved.RapidParameters.UseCls)
                    .Select(static file => file.FileName)),
            NdlOcrLiteEngineId => CheckFiles(displayName, resolved.ModelsDirectory,
                NdlLiteModelFiles.GetMissing(resolved.ModelsDirectory)
                    .Select(static file => file.RelativePath)),
            _ => CheckFiles(displayName, resolved.ModelsDirectory,
                NdlKotenModelFiles.Files
                    .Where(entry => !File.Exists(NdlKotenModelFiles.GetLocalPath(resolved.ModelsDirectory, entry)) ||
                                    new FileInfo(NdlKotenModelFiles.GetLocalPath(resolved.ModelsDirectory, entry))
                                        .Length !=
                                    entry.ExpectedBytes)
                    .Select(static entry => entry.RelativePath))
        };
    }

    /// <summary>
    /// Recognizes one issued input's image bytes and returns that input's typed box candidates
    /// in normalized page coordinates inside <paramref name="input"/>'s region. The supplied
    /// raster already covers exactly that region, so it is recognized in full and detection
    /// geometry is mapped once into the region — never cropped a second time.
    /// </summary>
    public OcrExtensionBoxCandidate[] Recognize(OcrJobPage page, OcrJobInput input, OcrInputImage image,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(image);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        return _engineId switch
        {
            RapidOcrEngineId => RecognizeRapidOcr(input, image),
            NdlOcrLiteEngineId => RecognizeNdlLite(input, image),
            _ => RecognizeNdlKoten(input, image)
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _pipeline?.Dispose();
            _pipeline = null;
        }
    }

    private OcrExtensionBoxCandidate[] RecognizeRapidOcr(OcrJobInput input, OcrInputImage image)
    {
        RapidOcrImage? decoded = RapidOcrImage.TryDecode(image.Data.Span)
                                 ?? throw new InvalidOperationException("Unable to decode the OCR input image bytes.");
        RapidOcrPipelineResult result = RapidPipeline.Run(decoded);
        List<OcrExtensionBoxCandidate> candidates = new(result.Lines.Count);
        foreach (RapidOcrPipelineLine line in result.Lines)
        {
            (double minX, double minY, double maxX, double maxY) = PointsBounds(line.Points);
            NormalizedBBox bbox = RegionBox(input.Region, minX, minY, maxX - minX, maxY - minY,
                decoded.Width, decoded.Height);
            candidates.Add(TextCandidate(input, bbox, line.Text, line.Score));
        }

        return candidates.ToArray();
    }

    private OcrExtensionBoxCandidate[] RecognizeNdlLite(OcrJobInput input, OcrInputImage image)
    {
        using SKBitmap decoded = NativeImageDecode.DecodeOriented(image.Data.Span)
                                 ?? throw new InvalidOperationException("Unable to decode the OCR input image bytes.");
        NdlLitePageResult result = NdlLitePipeline.Run(decoded);
        List<OcrExtensionBoxCandidate> candidates = new(result.Lines.Count);
        foreach (NdlLiteLine line in result.Lines)
        {
            NdlLiteBox box = line.Box;
            NormalizedBBox bbox = RegionBox(input.Region, box.X0, box.Y0, box.X1 - box.X0,
                box.Y1 - box.Y0, decoded.Width, decoded.Height);
            candidates.Add(TextCandidate(input, bbox, line.Text, line.Confidence));
        }

        return candidates.ToArray();
    }

    private OcrExtensionBoxCandidate[] RecognizeNdlKoten(OcrJobInput input, OcrInputImage image)
    {
        using SKBitmap decoded = NativeImageDecode.DecodeOriented(image.Data.Span)
                                 ?? throw new InvalidOperationException("Unable to decode the OCR input image bytes.");
        NdlKotenPageResult result = NdlKotenPipeline.Run(decoded);
        List<OcrExtensionBoxCandidate> candidates = new(result.Lines.Count);
        foreach (NdlKotenLine line in result.Lines)
        {
            Box box = line.Box;
            NormalizedBBox bbox = RegionBox(input.Region, box.X0, box.Y0, box.X1 - box.X0,
                box.Y1 - box.Y0, decoded.Width, decoded.Height);
            candidates.Add(TextCandidate(input, bbox, line.Text, line.Confidence));
        }

        return candidates.ToArray();
    }

    private OcrExtensionBoxCandidate TextCandidate(OcrJobInput input, NormalizedBBox bbox, string text,
        double confidence)
    {
        int order = Interlocked.Increment(ref _sourceOrder);
        DocumentBoxSeed seed = new(null, null, order, DocumentBoxType.Text, null, bbox,
            new TextBoxPayload(text), Confidence: Math.Clamp(confidence, 0.0, 1.0));
        return new OcrExtensionBoxCandidate(input.Id, seed);
    }

    private RapidOcrPipeline RapidPipeline => (RapidOcrPipeline)EnsurePipeline();

    private NdlLiteOcrPipeline NdlLitePipeline => (NdlLiteOcrPipeline)EnsurePipeline();

    private NdlKotenOcrPipeline NdlKotenPipeline => (NdlKotenOcrPipeline)EnsurePipeline();

    /// <summary>
    /// Builds the ONNX pipeline once, on first use, wrapped in an actionable model-availability
    /// message — an unreadable or absent weight file never falls back to another model.
    /// </summary>
    private IDisposable EnsurePipeline()
    {
        lock (_pipelineGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_pipeline is not null)
            {
                return _pipeline;
            }

            try
            {
                _pipeline = _engineId switch
                {
                    RapidOcrEngineId => new RapidOcrPipeline(_modelsDirectory, _rapidParameters),
                    NdlOcrLiteEngineId => new NdlLiteOcrPipeline(_modelsDirectory),
                    _ => new NdlKotenOcrPipeline(_modelsDirectory)
                };
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"The {GetDefinition(_engineId).DisplayName} model set under '{_modelsDirectory}' " +
                    $"could not be loaded: {error.Message} Verify the model path and complete the " +
                    "download in Settings > Local Files.", error);
            }

            return _pipeline;
        }
    }

    private static string NormalizeEngineId(string engineId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engineId);
        return engineId.Trim().ToLowerInvariant() switch
        {
            RapidOcrEngineId => RapidOcrEngineId,
            NdlOcrLiteEngineId => NdlOcrLiteEngineId,
            NdlKotenLiteEngineId => NdlKotenLiteEngineId,
            _ => throw new ArgumentException(
                $"'{engineId}' is not a native ONNX OCR engine (expected one of: {RapidOcrEngineId}, " +
                $"{NdlOcrLiteEngineId}, {NdlKotenLiteEngineId}).", nameof(engineId))
        };
    }

    private sealed record ResolvedNativeConfig(string ModelsDirectory, RapidOcrParameters RapidParameters);

    /// <summary>
    /// Resolves the effective engine configuration from a frozen snapshot. Explicit extension
    /// values win over legacy flat scalars, which win over the <c>parameters-json</c> object,
    /// which itself overlays the upstream defaults — the same layering
    /// <c>OcrPresetConfiguration.TryResolveImageConfig</c> applies when it builds the snapshot,
    /// so a migrated preset loses no configured value and a schema default never substitutes for
    /// one. Explicit but malformed values fail validation; they never fall back to a default.
    /// </summary>
    private static ResolvedNativeConfig Resolve(string engineId, IReadOnlyDictionary<string, string> values)
    {
        string parametersJson = GetValue(values, "parameters-json") ?? "";
        RequireParametersObject(parametersJson);
        if (engineId == RapidOcrEngineId)
        {
            RapidOcrParameters parameters = RapidOcrParameters.FromJson(parametersJson);
            parameters = (parameters with
            {
                TextScore = ReadDouble(values, "text-score", "textScore", parameters.TextScore),
                UseDet = ReadBool(values, "use-det", "useDet", parameters.UseDet),
                UseCls = ReadBool(values, "use-cls", "useCls", parameters.UseCls),
                UseRec = ReadBool(values, "use-rec", "useRec", parameters.UseRec),
                UsePreprocessImg = ReadBool(values, "use-preprocess-img", "usePreprocessImg",
                    parameters.UsePreprocessImg),
                MinSideLen = ReadInt(values, "min-side-len", "minSideLen", parameters.MinSideLen),
                MaxSideLen = ReadInt(values, "max-side-len", "maxSideLen", parameters.MaxSideLen),
                UseVerticalPadding = ReadBool(values, "use-vertical-padding", "useVerticalPadding",
                    parameters.UseVerticalPadding),
                MinHeight = ReadInt(values, "min-height", "minHeight", parameters.MinHeight),
                WidthHeightRatio = ReadInt(values, "width-height-ratio", "widthHeightRatio",
                    parameters.WidthHeightRatio),
                ReturnWordBox = ReadBool(values, "return-word-box", "returnWordBox",
                    parameters.ReturnWordBox),
                ReturnSingleCharBox = ReadBool(values, "return-single-char-box", "returnSingleCharBox",
                    parameters.ReturnSingleCharBox),
                ModelRootDir = ReadNullableString(values, "model-root-dir", "modelRootDir",
                    parameters.ModelRootDir),
                RecKeysPath = ReadNullableString(values, "rec-keys-path", "recKeysPath",
                    parameters.RecKeysPath),
                IntraOpNumThreads = ReadInt(values, "intra-op-num-threads", "intraOpNumThreads",
                    parameters.IntraOpNumThreads),
                InterOpNumThreads = ReadInt(values, "inter-op-num-threads", "interOpNumThreads",
                    parameters.InterOpNumThreads)
            }).Normalize();

            if (!parameters.RequiresDetectionAndRecognition)
            {
                throw new InvalidOperationException(
                    "RapidOCR requires useDet and useRec to produce text boxes.");
            }

            IReadOnlyList<string> unsupported = parameters.GetUnsupportedOptions();
            if (unsupported.Count > 0)
            {
                throw new InvalidOperationException(
                    $"RapidOCR does not support these options: {string.Join(", ", unsupported)}.");
            }

            // A nonempty frozen preset path is authoritative. Historical engine-specific aliases
            // remain usable only when no canonical path was pinned by the host.
            string? modelsDirectory = FirstNonEmpty(GetValue(values, "model-path"),
                parameters.ModelRootDir, GetValue(values, "modelPath"),
                ReadJsonString(parametersJson, "modelPath"));
            return new ResolvedNativeConfig(modelsDirectory ?? "", parameters);
        }

        string directory = FirstNonEmpty(GetValue(values, "model-path"), GetValue(values, "modelPath"),
            ReadJsonString(parametersJson, "modelPath")) ?? "";
        return new ResolvedNativeConfig(directory, RapidOcrParameters.Default);
    }

    private static Result CheckFiles(string displayName, string modelsDirectory, IEnumerable<string> missing)
    {
        string[] missingNames = missing.ToArray();
        return missingNames.Length == 0
            ? Result.Success()
            : Result.Failure(AppErrorCodes.NotFound,
                $"{displayName} model files are missing or incomplete under '{modelsDirectory}' " +
                $"({string.Join(", ", missingNames)}). Download them in Settings > Local Files.");
    }

    private static string? GetValue(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out string? value) ? value : null;
    }

    private static string? FirstNonEmpty(params string?[] candidates)
    {
        foreach (string? candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate.Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// The raw value a snapshot actually carries for one option: the explicit kebab-case
    /// extension key wins, then the legacy flat camelCase scalar that
    /// <c>OcrPresetConfiguration.CopyLegacyScalars</c> copies verbatim. Absent or whitespace
    /// returns null so the caller can fall through to <c>parameters-json</c>/defaults — only a
    /// real configured value is treated as explicit.
    /// </summary>
    private static string? ExplicitValue(IReadOnlyDictionary<string, string> values, string key,
        string legacyKey)
    {
        string? value = GetValue(values, key);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }

        value = GetValue(values, legacyKey);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>An explicit non-empty <c>parameters-json</c> must be a real JSON object.</summary>
    /// <exception cref="InvalidOperationException">The payload is not parseable or not an object.</exception>
    private static void RequireParametersObject(string parametersJson)
    {
        if (string.IsNullOrWhiteSpace(parametersJson))
        {
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(parametersJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    "OCR 'parameters-json' must be a JSON object holding engine parameters.");
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"OCR 'parameters-json' is not valid JSON: {exception.Message}", exception);
        }
    }

    private static bool ReadBool(IReadOnlyDictionary<string, string> values, string key,
        string legacyKey, bool fallback)
    {
        string? value = ExplicitValue(values, key, legacyKey);
        if (value is null)
        {
            return fallback;
        }

        return value.ToLowerInvariant() switch
        {
            "true" or "1" => true,
            "false" or "0" => false,
            _ => throw new InvalidOperationException(
                $"OCR option '{key}' expects true/false but was configured as '{value}'.")
        };
    }

    private static int ReadInt(IReadOnlyDictionary<string, string> values, string key,
        string legacyKey, int fallback)
    {
        string? value = ExplicitValue(values, key, legacyKey);
        if (value is null)
        {
            return fallback;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"OCR option '{key}' expects an integer but was configured as '{value}'.");
    }

    private static double ReadDouble(IReadOnlyDictionary<string, string> values, string key,
        string legacyKey, double fallback)
    {
        string? value = ExplicitValue(values, key, legacyKey);
        if (value is null)
        {
            return fallback;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"OCR option '{key}' expects a number but was configured as '{value}'.");
    }

    private static string? ReadNullableString(IReadOnlyDictionary<string, string> values, string key,
        string legacyKey, string? fallback)
    {
        // Only a real configured value overrides the fallback; a snapshot never carries
        // whitespace-only entries (CopyLegacyScalars drops them), so absent stays absent.
        string? value = ExplicitValue(values, key, legacyKey);
        return value is null ? fallback : value;
    }

    private static string? ReadJsonString(string json, string property)
    {
        // Only called after RequireParametersObject validated the payload.
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(json);
        foreach (JsonProperty member in document.RootElement.EnumerateObject())
        {
            if (string.Equals(member.Name, property, StringComparison.OrdinalIgnoreCase) &&
                member.Value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(member.Value.GetString()))
            {
                return member.Value.GetString()!.Trim();
            }
        }

        return null;
    }

    /// <summary>Maps a raster pixel rectangle into the input region's page coordinates.</summary>
    internal static NormalizedBBox RegionBox(NormalizedBBox region, double x0, double y0, double width,
        double height, int rasterWidth, int rasterHeight)
    {
        double scaleX = region.Width / Math.Max(1, rasterWidth);
        double scaleY = region.Height / Math.Max(1, rasterHeight);
        return ClampNormalized(
            region.X + x0 * scaleX,
            region.Y + y0 * scaleY,
            width * scaleX,
            height * scaleY);
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) PointsBounds(
        IReadOnlyList<RapidOcrPoint> points)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (RapidOcrPoint point in points)
        {
            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);
        }

        return (minX, minY, maxX, maxY);
    }

    internal static NormalizedBBox ClampNormalized(double x, double y, double width, double height)
    {
        const double epsilon = 1e-6;
        x = Math.Clamp(x, 0.0, 1.0 - epsilon);
        y = Math.Clamp(y, 0.0, 1.0 - epsilon);
        // A degenerate box still needs positive extent so it passes host validation.
        width = Math.Clamp(width, epsilon, 1.0 - x);
        height = Math.Clamp(height, epsilon, 1.0 - y);
        return new NormalizedBBox(x, y, width, height);
    }
}

/// <summary>
/// Decodes hosted image bytes into an EXIF-oriented BGRA bitmap. Decoding happens entirely in
/// memory; no temporary image files are written.
/// </summary>
internal static class NativeImageDecode
{
    public static SKBitmap? DecodeOriented(ReadOnlySpan<byte> data)
    {
        try
        {
            using SKMemoryStream stream = new(data.ToArray());
            using SKCodec? codec = SKCodec.Create(stream);
            if (codec is null)
            {
                return null;
            }

            using SKBitmap? raw = SKBitmap.Decode(codec);
            if (raw is null)
            {
                return null;
            }

            return ApplyOrigin(raw, codec.EncodedOrigin);
        }
        catch (Exception error) when (error is IOException or ArgumentException
                                          or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    internal static SKBitmap ApplyOrigin(SKBitmap raw, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
        {
            return raw.Copy(SKColorType.Bgra8888) ?? raw.Copy();
        }

        bool swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int width = swap ? raw.Height : raw.Width;
        int height = swap ? raw.Width : raw.Height;
        SKBitmap oriented = new(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using SKCanvas canvas = new(oriented);
        canvas.Clear();
        canvas.Translate(width / 2f, height / 2f);
        switch (origin)
        {
            case SKEncodedOrigin.TopRight:
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.BottomRight:
                canvas.RotateDegrees(180);
                break;
            case SKEncodedOrigin.BottomLeft:
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.LeftTop:
                canvas.Scale(1, -1);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.RightTop:
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.RightBottom:
                canvas.Scale(-1, 1);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.LeftBottom:
                canvas.RotateDegrees(-90);
                break;
        }

        canvas.Translate(-raw.Width / 2f, -raw.Height / 2f);
        canvas.DrawBitmap(raw, 0, 0);
        return oriented;
    }
}
