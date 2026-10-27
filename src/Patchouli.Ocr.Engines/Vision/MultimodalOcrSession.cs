using System.Text.Json;
using System.Globalization;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Documents;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.Ocr.Engines.Vision;
using SkiaSharp;

namespace Patchouli.Ocr.Engines;

/// <summary>
/// One bound multimodal (hosted vision) OCR session. This is the public facade the compiled
/// <c>ocr.multimodal</c> package uses: configuration comes exclusively from the run-pinned
/// <see cref="ExtensionConfigSnapshot"/> — provider/model/reasoning defaults resolve through the
/// host's OCR slot, never the translation or chat defaults. The issued input image already covers
/// exactly <see cref="OcrJobInput.Region"/>: a user-drawn crop or bounded logical page arrives
/// pre-cropped, is sent verbatim, and bypasses whole-page layout detection entirely — no layout
/// weights required and no second crop applied. Model geometry returned by the provider is
/// normalized to the sent image and mapped back through the input region baseline; the model's
/// text for a detected or explicit region is anchored to the already-known box and never
/// re-solves coordinates.
/// </summary>
/// <remarks>
/// <see cref="GetDefinition"/> and <see cref="CheckReadiness"/> never create ONNX sessions or LLM
/// calls; the layout detector is built lazily on the first full-page layout input and released by
/// <see cref="Dispose"/> after the job's calls have exited. Image bytes only ever travel as
/// request payloads — no source or cache paths appear in requests or diagnostics.
/// </remarks>
public sealed class MultimodalOcrSession : IDisposable
{
    /// <summary>Multimodal LLM engine id; kept equal to <c>OcrEngineIds.MultimodalLlm</c>.</summary>
    public const string MultimodalLlmEngineId = "multimodal-llm";

    // OcrAdapterKind.CloudApi value; this leaf assembly cannot reference Patchouli.Ocr.
    private const string CloudApiAdapter = "cloud_api";

    /// <summary>LLM capability domain; kept equal to <c>Patchouli.Extensions.Sdk.Domains.ocr</c>.</summary>
    internal const string LlmDomain = "ocr";

    private static readonly string[] ImageInputKinds =
        [NativeOcrInputKinds.PageImage, NativeOcrInputKinds.ImageFile, NativeOcrInputKinds.RegionImage];

    private const string RegionPrompt =
        "You are a strict OCR engine. Transcribe everything visible in this cropped region in " +
        "reading order. Preserve line breaks and the original language. Return ONLY JSON " +
        "{\"content\":[{\"type\":\"text\",\"markdown\":\"recognized text\"}]} listing each block " +
        "in order. Block types: \"text\", \"title\", \"list\", \"table\" (Markdown pipe table in " +
        "\"markdown\", or \"html\" for complex tables), \"equation\" (use \"latex\"), \"code\" " +
        "(use \"code\" and optional \"language\"). The region boundary is already known. Do not " +
        "output coordinates, boxes, confidence, or commentary. If there is no content, return " +
        "{\"content\":[]}\"}";

    private const string DirectPrompt =
        "You are a strict OCR engine. Extract every content block visible in the image.\n" +
        "Respond with a single JSON object and nothing else, exactly in this shape:\n" +
        "{\"boxes\":[{\"type\":\"text\",\"markdown\":\"the block text\",\"x\":0.0,\"y\":0.0," +
        "\"width\":0.0,\"height\":0.0,\"confidence\":0.0}]}\n" +
        "Rules:\n" +
        "- x and y are the top-left corner of the block's bounding box; width and height are its extents.\n" +
        "- All coordinates are normalized to [0,1] relative to the full image.\n" +
        "- List boxes in reading order (top-to-bottom, left-to-right).\n" +
        "- \"type\" is one of \"text\", \"title\", \"list\", \"table\", \"equation\", \"code\", " +
        "\"image\"; tables use \"markdown\" pipe tables or \"html\" for complex tables, equations " +
        "use \"latex\", code uses \"code\" plus optional \"language\".\n" +
        "- confidence is optional and must be within [0,1].\n" +
        "- If the image contains no recognizable content, respond with {\"boxes\":[]}.";

    /// <summary>
    /// Stride between two inputs' local candidate ordering. One input never emits this many
    /// items; the stride only keeps declared input order and in-input order comparable when the
    /// runtime merges candidates across inputs.
    /// </summary>
    private const int SourceOrderStride = 1 << 16;

    private readonly MultimodalOcrOptions _options;
    private readonly object _detectorGate = new();
    private IVisionLayoutDetector? _detector;
    private int _disposed;

    /// <summary>
    /// Binds the frozen job configuration. Resolving configuration validates it; the layout
    /// detector session and LLM calls stay lazy until recognition actually needs them.
    /// </summary>
    /// <exception cref="InvalidOperationException">The resolved configuration is invalid.</exception>
    public MultimodalOcrSession(ExtensionConfigSnapshot config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _options = MultimodalOcrOptions.FromSnapshot(config);
    }

    /// <summary>Test seam: a pre-built detector replaces the lazy real one. Owned by the session.</summary>
    internal MultimodalOcrSession(ExtensionConfigSnapshot config, IVisionLayoutDetector detector)
        : this(config)
    {
        _detector = detector;
    }

    /// <summary>Declarative engine metadata; pure constants, no sessions or IO.</summary>
    public static OcrEngineDefinition GetDefinition()
    {
        return new OcrEngineDefinition(
            MultimodalLlmEngineId,
            "Multimodal LLM OCR",
            CloudApiAdapter,
            "",
            ImageInputKinds,
            true,
            SupportsVerticalText: true,
            SupportsTableDetection: true,
            ModelDirectoryKind: null,
            Notes:
            "Online multimodal recognition with optional local PP-DocLayoutV3/DocLayout-YOLO " +
            "layout detection; explicit region inputs bypass detection and need no weights.");
    }

    /// <summary>
    /// Branch-neutral engine readiness probe: the frozen config must resolve and the OCR
    /// provider/model binding must exist (when host resources are supplied). No ONNX session is
    /// created, nothing is downloaded, no credential secret is read — and layout weights are
    /// deliberately NOT probed here: this engine accepts direct-mode jobs and region-scoped jobs
    /// whose inputs never touch the detector, so a missing layout model must not block them.
    /// Whole-page local-layout inputs validate the weights when detection is actually required
    /// (see <see cref="EnsureDetector"/>); callers that want a full-capability probe use
    /// <see cref="CheckLayoutReadiness"/>.
    /// </summary>
    public static Result CheckReadiness(ExtensionConfigSnapshot config, IExtensionResources? resources = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        MultimodalOcrOptions options;
        try
        {
            options = MultimodalOcrOptions.FromSnapshot(config);
        }
        catch (InvalidOperationException exception)
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, exception.Message);
        }

        if (resources is not null)
        {
            try
            {
                _ = resources.ResolveLlm(LlmDomain, options.ModelSelection);
            }
            catch (InvalidOperationException exception)
            {
                return Result.Failure(AppErrorCodes.ValidationFailed, exception.Message);
            }
        }

        return Result.Success();
    }

    /// <summary>
    /// Scope-aware companion to <see cref="CheckReadiness"/>: probes only the layout-detection
    /// branch — whether the pinned weights file is usable for the configured model. Succeeds
    /// unconditionally when local layout is disabled (the branch is unreachable). This is the
    /// probe a settings UI or a scope-aware runtime readiness check should call for the layout
    /// capability; it is still non-executing — no ONNX session is created.
    /// </summary>
    public static Result CheckLayoutReadiness(ExtensionConfigSnapshot config)
    {
        ArgumentNullException.ThrowIfNull(config);
        MultimodalOcrOptions options;
        try
        {
            options = MultimodalOcrOptions.FromSnapshot(config);
        }
        catch (InvalidOperationException exception)
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, exception.Message);
        }

        return options.UseLocalLayout
            ? VisionLayoutDetector.ValidateModel(options.LayoutModelId, options.LayoutModelPath ?? "")
            : Result.Success();
    }

    /// <summary>
    /// Recognizes one issued input and returns its typed box candidates in normalized page
    /// coordinates inside <paramref name="input"/>'s region. The issued image already covers
    /// exactly <see cref="OcrJobInput.Region"/>: an explicit region (user crop or bounded logical
    /// page) is sent verbatim — never re-rendered or re-cropped — and bypasses layout detection
    /// entirely, so it needs no layout weights. A full-page input uses local layout detection
    /// when configured, or the model's own boxes in direct mode.
    /// </summary>
    public Task<OcrExtensionBoxCandidate[]> RecognizeAsync(OcrJobPage page, OcrJobInput input,
        OcrInputImage image, IExtensionResources resources, CancellationToken cancellationToken)
    {
        return RecognizeAsync(page, input, image, resources, cancellationToken, null);
    }

    /// <summary>Recognizes an input with page-scoped layout, reading-order and region progress.</summary>
    public async Task<OcrExtensionBoxCandidate[]> RecognizeAsync(OcrJobPage page, OcrJobInput input,
        OcrInputImage image, IExtensionResources resources, CancellationToken cancellationToken,
        IProgress<ExtensionProgress>? progress)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(resources);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        ReadOnlyMemory<byte> imageBytes = image.Data;
        if (imageBytes.Length == 0)
        {
            throw new InvalidOperationException("The OCR input image is empty.");
        }

        ILlmCapability llm = resources.ResolveLlm(LlmDomain, _options.ModelSelection);
        string mediaType = NormalizeMediaType(input.ContentType);
        // Candidate ordering derives from the input's declared position in the page plus a
        // per-input local counter: parallel inputs or retries can interleave nothing, and the
        // merged order follows the host's declared input order exactly.
        int orderBase = InputOrdinal(page, input) * SourceOrderStride;

        // Region input: the materialized surface IS the crop; send the original bytes as the
        // payload and anchor the model's typed content to the region baseline.
        if (input.Kind == NativeOcrInputKinds.RegionImage || !VisionImaging.IsFullPage(input.Region))
        {
            ReportProgress(progress, page, input, "direct-ocr", 0, 1);
            OcrExtensionBoxCandidate[] candidates = await RecognizeRegionContentAsync(input, imageBytes.ToArray(),
                input.Region,
                mediaType, llm, orderBase, cancellationToken).ConfigureAwait(false);
            ReportProgress(progress, page, input, "direct-ocr", 1, 1);
            return candidates;
        }

        if (!_options.UseLocalLayout)
        {
            ReportProgress(progress, page, input, "direct-ocr", 0, 1);
            OcrExtensionBoxCandidate[] candidates = await RecognizeDirectAsync(input, imageBytes.ToArray(), mediaType,
                llm,
                orderBase, cancellationToken).ConfigureAwait(false);
            ReportProgress(progress, page, input, "direct-ocr", 1, 1);
            return candidates;
        }

        // Layout mode needs pixels for the detector, the numbered-region overlay and the crops;
        // decode once, EXIF-oriented, so every derived image shares one orientation.
        using SKBitmap decoded = NativeImageDecode.DecodeOriented(imageBytes.Span)
                                 ?? throw new InvalidOperationException(
                                     "Unable to decode the OCR input image bytes.");
        return await RecognizeWithLayoutAsync(page, input, decoded, imageBytes, llm, orderBase,
            cancellationToken, progress).ConfigureAwait(false);
    }

    /// <summary>The input's declared position in <paramref name="page"/>'s inputs, by id.</summary>
    private static int InputOrdinal(OcrJobPage page, OcrJobInput input)
    {
        for (int index = 0; index < page.Inputs.Count; index++)
        {
            if (page.Inputs[index].Id == input.Id)
            {
                return index;
            }
        }

        return 0;
    }

    /// <summary>Provider-facing media type for a verbatim input payload.</summary>
    private static string NormalizeMediaType(string? contentType)
    {
        return contentType?.Trim().ToLowerInvariant() is { Length: > 0 } media
               && media.StartsWith("image/", StringComparison.Ordinal)
            ? media
            : "image/png";
    }

    /// <summary>
    /// Direct mode: the model supplies both boxes and text for the whole surface. Box coordinates
    /// are normalized to the sent image; <paramref name="input"/>'s region is the page baseline.
    /// </summary>
    private async Task<OcrExtensionBoxCandidate[]> RecognizeDirectAsync(OcrJobInput input, byte[] png,
        string mediaType, ILlmCapability llm, int orderBase, CancellationToken cancellationToken)
    {
        string reply = await CompleteVisionAsync(llm, DirectPrompt, png, mediaType, cancellationToken)
            .ConfigureAwait(false);
        Result<IReadOnlyList<VisionTypedItem>> parsed = VisionResultParser.ParseDirect(reply);
        if (parsed.IsFailure)
        {
            throw new InvalidOperationException(parsed.ErrorMessage);
        }

        List<OcrExtensionBoxCandidate> candidates = new(parsed.Value.Count);
        int order = 0;
        foreach (VisionTypedItem item in parsed.Value)
        {
            NormalizedBBox local = item.BBox ?? new NormalizedBBox(0, 0, 1, 1);
            NormalizedBBox pageBox = VisionImaging.RegionBoxNormalized(input.Region, local);
            DocumentBoxSeed? seed = VisionResultParser.ToSeed(item, pageBox, orderBase + order);
            if (seed is not null)
            {
                order++;
                candidates.Add(new OcrExtensionBoxCandidate(input.Id, seed));
            }
        }

        return candidates.ToArray();
    }

    /// <summary>
    /// Region mode: the model supplies typed content only; every block is anchored to the known
    /// region box <paramref name="pageBox"/> (already in page coordinates).
    /// </summary>
    private async Task<OcrExtensionBoxCandidate[]> RecognizeRegionContentAsync(OcrJobInput input,
        byte[] png, NormalizedBBox pageBox, string mediaType, ILlmCapability llm, int orderBase,
        CancellationToken cancellationToken)
    {
        string reply = await CompleteVisionAsync(llm, RegionPrompt, png, mediaType, cancellationToken)
            .ConfigureAwait(false);
        Result<IReadOnlyList<VisionTypedItem>> parsed = VisionResultParser.ParseRegionContent(reply);
        if (parsed.IsFailure)
        {
            throw new InvalidOperationException(parsed.ErrorMessage);
        }

        List<OcrExtensionBoxCandidate> candidates = new(parsed.Value.Count);
        int order = 0;
        foreach (VisionTypedItem item in parsed.Value)
        {
            DocumentBoxSeed? seed = VisionResultParser.ToSeed(item, pageBox, orderBase + order);
            if (seed is not null)
            {
                order++;
                candidates.Add(new OcrExtensionBoxCandidate(input.Id, seed));
            }
        }

        return candidates.ToArray();
    }

    /// <summary>
    /// Layout mode: local ONNX detection supplies region geometry, the multimodal model orders the
    /// numbered regions (one correction attempt on an invalid permutation), and bounded-concurrency
    /// region calls supply typed content anchored to the detector's boxes. Missing weights produce
    /// an actionable error — never a silent switch to direct mode.
    /// </summary>
    private async Task<OcrExtensionBoxCandidate[]> RecognizeWithLayoutAsync(OcrJobPage page, OcrJobInput input,
        SKBitmap decoded, ReadOnlyMemory<byte> imageBytes, ILlmCapability llm, int orderBase,
        CancellationToken cancellationToken, IProgress<ExtensionProgress>? progress)
    {
        ReportProgress(progress, page, input, "layout-detection", 0, 1);
        IVisionLayoutDetector detector = EnsureDetector();
        byte[] surfacePng = VisionImaging.EncodePng(decoded);
        Result<IReadOnlyList<VisionLayoutRegion>> layout =
            await detector.DetectAsync(surfacePng, cancellationToken).ConfigureAwait(false);
        if (layout.IsFailure)
        {
            throw new InvalidOperationException(layout.ErrorMessage);
        }

        ReportProgress(progress, page, input, "layout-detection", 1, 1);
        if (layout.Value.Count == 0)
        {
            return [];
        }

        IReadOnlyList<VisionLayoutRegion> ordered = layout.Value;
        if (ordered.Count > 1)
        {
            ReportProgress(progress, page, input, "reading-order", 0, 1);
            using SKBitmap marked = decoded.Copy()
                                    ?? throw new InvalidOperationException(
                                        "Unable to prepare the reading-order image.");
            byte[] markedPng = VisionImaging.MarkRegionIds(marked, ordered);
            IReadOnlyList<int> regionOrder = await RequestReadingOrderAsync(llm, markedPng, ordered,
                cancellationToken).ConfigureAwait(false);
            ordered = regionOrder.Select(index => ordered[index]).ToArray();
            ReportProgress(progress, page, input, "reading-order", 1, 1);
        }

        byte[] surfaceBytes = imageBytes.ToArray();
        string?[] replies = new string?[ordered.Count];
        int completedRegions = 0;
        object progressGate = new();
        ReportProgress(progress, page, input, "region-ocr", 0, ordered.Count);
        // Regions are decoded+cropped per worker: a shared SKBitmap must not be read by
        // concurrent canvases, and region crops stay independent like the legacy adapter.
        await Parallel.ForEachAsync(Enumerable.Range(0, ordered.Count), new ParallelOptions
        {
            MaxDegreeOfParallelism = _options.Concurrency,
            CancellationToken = cancellationToken
        }, async (index, token) =>
        {
            using SKBitmap? again = NativeImageDecode.DecodeOriented(surfaceBytes)
                                    ?? throw new InvalidOperationException(
                                        "Unable to decode the OCR input image bytes.");
            byte[] cropPng = VisionImaging.CropToPngBytes(again, ordered[index].BBox);
            replies[index] = await CompleteVisionAsync(llm, RegionPrompt, cropPng, "image/png", token)
                .ConfigureAwait(false);
            lock (progressGate)
            {
                ReportProgress(progress, page, input, "region-ocr", ++completedRegions, replies.Length);
            }
        }).ConfigureAwait(false);

        List<OcrExtensionBoxCandidate> candidates = [];
        int order = 0;
        // `ordered` is the model-confirmed permutation; building candidates in that index order
        // keeps the emitted sequence identical to the logical reading order regardless of which
        // parallel region request completed first.
        for (int index = 0; index < ordered.Count; index++)
        {
            Result<IReadOnlyList<VisionTypedItem>> parsed =
                VisionResultParser.ParseRegionContent(replies[index]!);
            if (parsed.IsFailure)
            {
                throw new InvalidOperationException(parsed.ErrorMessage);
            }

            // Detector coordinates are surface-normalized; the input region is the page baseline.
            NormalizedBBox pageBox = VisionImaging.RegionBoxNormalized(input.Region,
                ordered[index].BBox);
            foreach (VisionTypedItem item in parsed.Value)
            {
                DocumentBoxSeed? seed = VisionResultParser.ToSeed(item, pageBox, orderBase + order);
                if (seed is not null)
                {
                    order++;
                    candidates.Add(new OcrExtensionBoxCandidate(input.Id, seed));
                }
            }
        }

        return candidates.ToArray();
    }

    private static void ReportProgress(IProgress<ExtensionProgress>? progress, OcrJobPage page,
        OcrJobInput input, string phase, int completed, int total)
    {
        try
        {
            progress?.Report(new ExtensionProgress(phase, completed, total,
                ParentPhase: "page-ocr", ExecutionId: input.Id.Value,
                ItemId: page.Page.PageIndex.ToString(CultureInfo.InvariantCulture)));
        }
        catch (Exception exception)
        {
            UnexpectedExceptionReporter.Report(exception, "ocr.multimodal-progress");
        }
    }

    /// <summary>
    /// The reading-order request: the model must return a complete permutation of the numbered
    /// region ids. One invalid reply gets exactly one corrected retry; a second invalid reply
    /// fails the input instead of guessing an order.
    /// </summary>
    private async Task<IReadOnlyList<int>> RequestReadingOrderAsync(ILlmCapability llm,
        byte[] markedPng, IReadOnlyList<VisionLayoutRegion> regions, CancellationToken cancellationToken)
    {
        string descriptors = JsonSerializer.Serialize(regions.Select((region, id) => new
        {
            id,
            label = VisionLayoutModels.Describe(region.Label),
            bbox = new[] { region.BBox.X, region.BBox.Y, region.BBox.Width, region.BBox.Height }
        }));
        string prompt = "Determine the logical reading order of the numbered document regions in this image. " +
                        "The regions and their bounding boxes are already detected; do not change them. " +
                        "Respect text direction, separate columns, headings, captions, and footnotes. " +
                        "Return ONLY JSON {\"order\":[0,1,...]} listing EVERY supplied region ID exactly once. " +
                        "Each region ID is printed in its rectangle on the image. " +
                        "Finish one text column before moving to the next; do not zigzag between unrelated columns. " +
                        $"There are exactly {regions.Count} regions. Required IDs: " +
                        JsonSerializer.Serialize(Enumerable.Range(0, regions.Count)) + ". " +
                        "Region bbox is normalized [x,y,width,height], origin top-left. Regions: " + descriptors;
        Result<IReadOnlyList<int>> order = Result<IReadOnlyList<int>>.Failure(
            AppErrorCodes.ValidationFailed, "No reading-order response was produced.");
        for (int attempt = 0; attempt < 2; attempt++)
        {
            string reply = await CompleteVisionAsync(llm, prompt, markedPng, "image/png",
                cancellationToken).ConfigureAwait(false);
            order = VisionReadingOrder.Parse(reply, regions.Count);
            if (order.IsSuccess)
            {
                return order.Value;
            }

            prompt += " Your previous response was invalid: " + reply +
                      ". Correct it: every required ID must appear exactly once; do not omit, " +
                      "duplicate, or invent IDs.";
        }

        throw new InvalidOperationException(order.ErrorMessage);
    }

    /// <summary>
    /// One provider round-trip: the request carries OCR domain semantics, the frozen model
    /// selection and reasoning effort via the bound capability, and the image as typed bytes.
    /// Errors surface as the capability's own classified exceptions; cancellation propagates.
    /// </summary>
    private async Task<string> CompleteVisionAsync(ILlmCapability llm, string prompt, byte[] png,
        string mediaType, CancellationToken cancellationToken)
    {
        LlmRequest request = new([
            new LlmMessage("user", prompt)
            {
                Images = [new LlmImage(png, mediaType, _options.ImageDetail)]
            }
        ])
        {
            ReasoningEffort = _options.ReasoningEffort
        };
        LlmResponse response = await llm.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        return response.Text;
    }

    /// <summary>
    /// Creates the layout detector on the first full-page layout input. This is the single point
    /// where the pinned weights file is validated: a missing or unusable model fails the input
    /// with the file-level diagnostic — never a silent switch to direct mode. Region inputs and
    /// direct mode never reach this path and need no weights.
    /// </summary>
    private IVisionLayoutDetector EnsureDetector()
    {
        lock (_detectorGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_detector is null)
            {
                Result validated =
                    VisionLayoutDetector.ValidateModel(_options.LayoutModelId,
                        _options.LayoutModelPath ?? "");
                if (validated.IsFailure)
                {
                    throw new InvalidOperationException(validated.ErrorMessage);
                }

                _detector = new VisionLayoutDetector(_options.LayoutModelId,
                    _options.LayoutModelPath ?? "");
            }

            return _detector;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            lock (_detectorGate)
            {
                _detector?.Dispose();
                _detector = null;
            }
        }
    }
}
