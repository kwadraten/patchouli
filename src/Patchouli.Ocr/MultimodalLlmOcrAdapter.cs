using System.Text.Json;
using Patchouli.Core.Credentials;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Llm;

namespace Patchouli.Ocr;

/// <summary>
/// Runtime seam for <see cref="MultimodalLlmOcrAdapter"/>: resolves the OCR provider/model selection, builds
/// vision-capable chat clients and exposes the metadata-only provider readiness snapshot. Tests substitute
/// this interface so no network call and no credential store is ever touched.
/// </summary>
public interface IMultimodalLlmOcrRuntime
{
    /// <summary>The default OCR provider/model pair, taken from <see cref="LlmAppSettings.OcrSelection"/>.</summary>
    (string ProviderId, string Model) ResolveOcrSelection();

    /// <summary>Builds a chat client for one provider/model, or fails with an <see cref="LlmFailureCodes"/> code.</summary>
    Task<Result<ILlmChatClient>> CreateClientAsync(string providerId, string model,
        CancellationToken cancellationToken = default);

    /// <summary>Metadata-only readiness for every catalog provider; no network request is made.</summary>
    Task<IReadOnlyList<LlmProviderReadiness>> InspectProvidersAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Default runtime backed by <see cref="LlmProviderClientFactory"/>: provider resolution and readiness are
/// real (catalog + stored-key metadata) but never read the secret body over the network.
/// </summary>
public sealed class MultimodalLlmOcrRuntime : IMultimodalLlmOcrRuntime
{
    private readonly Func<LlmAppSettings> _settingsProvider;
    private readonly ICredentialStore _credentialStore;

    public MultimodalLlmOcrRuntime(Func<LlmAppSettings> settingsProvider, ICredentialStore credentialStore)
    {
        _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
    }

    public (string ProviderId, string Model) ResolveOcrSelection()
    {
        return _settingsProvider().OcrSelection;
    }

    public async Task<Result<ILlmChatClient>> CreateClientAsync(string providerId, string model,
        CancellationToken cancellationToken = default)
    {
        Result<LlmProviderRuntimeSettings> resolved = await LlmProviderClientFactory.ResolveAsync(
            _settingsProvider(), providerId, model, _credentialStore, cancellationToken);
        if (resolved.IsFailure)
        {
            return Result<ILlmChatClient>.Failure(resolved.ErrorCode!, resolved.ErrorMessage!);
        }

        if (resolved.Value.AuthenticationMode == LlmAuthenticationModes.Subscription)
        {
            return Result<ILlmChatClient>.Failure(LlmFailureCodes.UnsupportedInput,
                "This subscription backend accepts text only; choose an API provider for OCR.");
        }

        return Result<ILlmChatClient>.Success(
            new LlmChatClient(resolved.Value, LlmClientFactory.CreateTransport(resolved.Value)));
    }

    public async Task<IReadOnlyList<LlmProviderReadiness>> InspectProvidersAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<LlmProviderReadiness> providers =
            await LlmProviderClientFactory.InspectAsync(_settingsProvider(), _credentialStore, cancellationToken);
        return providers.Select(provider => provider.SupportsVision
            ? provider
            : provider with
            {
                IsConfigured = false,
                Diagnostic = "This subscription backend accepts text only; choose an API provider for OCR."
            }).ToArray();
    }
}

/// <summary>
/// Multimodal LLM OCR over the existing page pipeline: the page/region image produced by the render service
/// travels through <see cref="ILlmChatClient.CompleteVisionAsync"/>, the model's structured reply is
/// normalized into <see cref="OcrEngineTextBox"/>es (the exact shape the other real adapters produce), and the
/// OCR run engine turns that into an <see cref="OcrDocumentTreeCandidate"/> that enters the tree
/// exclusively through the unified import/commit channel. This adapter never writes document_boxes itself.
/// </summary>
public sealed class MultimodalLlmOcrAdapter : IRealOcrAdapter
{
    private const string ConversationKeyPrefix = "patchouli.ocr.multimodal";

    private const string Prompt =
        "You are a strict OCR engine. Extract every text line visible in the image.\n" +
        "Respond with a single JSON object and nothing else, exactly in this shape:\n" +
        "{\"boxes\":[{\"text\":\"the line text\",\"x\":0.0,\"y\":0.0,\"width\":0.0,\"height\":0.0," +
        "\"confidence\":0.0}]}\n" +
        "Rules:\n" +
        "- x and y are the top-left corner of the line's bounding box; width and height are its extents.\n" +
        "- All coordinates are normalized to [0,1] relative to the full image.\n" +
        "- List boxes in reading order (top-to-bottom, left-to-right).\n" +
        "- confidence is optional and must be within [0,1].\n" +
        "- If the image contains no text, respond with {\"boxes\":[]}.";

    private readonly IMultimodalLlmOcrRuntime _runtime;

    /// <summary>
    /// Host-facing default. Until the host wires the app's settings and credential store through
    /// <see cref="MultimodalLlmOcrRuntime"/>, the adapter still runs the real readiness path, it simply sees
    /// no stored key and therefore reports a missing credential instead of pretending to be ready.
    /// </summary>
    public MultimodalLlmOcrAdapter()
        : this(new MultimodalLlmOcrRuntime(() => LlmAppSettings.Default(), UnavailableCredentialStore.Instance))
    {
    }

    public MultimodalLlmOcrAdapter(IMultimodalLlmOcrRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public string EngineId => OcrEngineIds.MultimodalLlm;
    public string DisplayName => "Multimodal LLM OCR";
    public string Kind => OcrAdapterKind.CloudApi;

    public OcrEngineCapability GetCapability()
    {
        return new OcrEngineCapability(EngineId, DisplayName, false, true, true, false, true, true, false, true, false,
            [OcrInputKinds.PageImage, OcrInputKinds.RegionImage],
            "Accepts page or region images from a multimodal LLM endpoint and must normalize output into the MinerU-compatible layout pipeline before commit.");
    }

    public async Task<OcrEnvironmentCheckResult> CheckEnvironmentAsync(OcrPresetVersion presetVersion,
        CancellationToken cancellationToken = default)
    {
        return await CheckCoreAsync(presetVersion, cancellationToken);
    }

    public async Task<Result> ValidatePresetAsync(OcrPresetVersion presetVersion,
        CancellationToken cancellationToken = default)
    {
        OcrEnvironmentCheckResult check = await CheckCoreAsync(presetVersion, cancellationToken);
        return check.IsReady
            ? Result.Success()
            : Result.Failure(AppErrorCodes.ValidationFailed, check.Message);
    }

    public Task<Result> ValidateInputAsync(OcrInputDescriptor input, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(input.InputKind is OcrInputKinds.PageImage or OcrInputKinds.RegionImage
            ? Result.Success()
            : Result.Failure(AppErrorCodes.ValidationFailed, "Multimodal LLM OCR expects page or region images."));
    }

    public async Task<Result<OcrEnginePageResult>> RunPageAsync(OcrInputDescriptor input,
        OcrPresetVersion presetVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(input.ImagePath) || !File.Exists(input.ImagePath))
        {
            return Result<OcrEnginePageResult>.Failure(AppErrorCodes.NotFound,
                "A rendered page image is required for multimodal LLM OCR.");
        }

        (string providerId, string model) = ResolveSelection(presetVersion);
        if (string.IsNullOrWhiteSpace(model))
        {
            return Result<OcrEnginePageResult>.Failure(LlmFailureCodes.ModelNotFound,
                $"Provider '{providerId}' has no OCR model configured.");
        }

        Result<ILlmChatClient> client = await _runtime.CreateClientAsync(providerId, model, cancellationToken);
        if (client.IsFailure)
        {
            // The failure code is a LlmFailureCodes value; the queue classifies it through OcrRetryPolicy.
            return Result<OcrEnginePageResult>.Failure(client.ErrorCode!, client.ErrorMessage!);
        }

        byte[] imageBytes;
        try
        {
            imageBytes = await File.ReadAllBytesAsync(input.ImagePath, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception, "ocr-multimodal-llm"))
        {
            return Result<OcrEnginePageResult>.Failure(AppErrorCodes.InvalidState,
                $"The rendered page image could not be read: {exception.Message}");
        }

        string? mimeType = ResolveMimeType(input.ImagePath, imageBytes);
        if (mimeType is null)
        {
            return Result<OcrEnginePageResult>.Failure(AppErrorCodes.InvalidState,
                $"The rendered page image '{input.ImagePath}' is not a recognized image format.");
        }

        string prompt = BuildPrompt(input);
        string dataUri = $"data:{mimeType};base64,{Convert.ToBase64String(imageBytes)}";
        Result<LlmChatCompletion> completion = await client.Value.CompleteVisionAsync(
            $"{ConversationKeyPrefix}:{input.PageId}",
            new LlmChatRequest(LlmChatHistory.Create(prompt)),
            new LlmVisionInput(dataUri, mimeType, prompt, "high"),
            cancellationToken);
        if (completion.IsFailure)
        {
            return Result<OcrEnginePageResult>.Failure(completion.ErrorCode!, completion.ErrorMessage!);
        }

        Result<IReadOnlyList<OcrEngineTextBox>> boxes =
            MultimodalLlmVisionResultParser.Parse(completion.Value.Text);
        if (boxes.IsFailure)
        {
            return Result<OcrEnginePageResult>.Failure(boxes.ErrorCode!, boxes.ErrorMessage!);
        }

        NormalizedBBox pageBBox = input.RegionBBox ?? new NormalizedBBox(0, 0, 1, 1);
        string? text = boxes.Value.Count == 0
            ? null
            : string.Join('\n', boxes.Value.Select(box => box.Text));
        return Result<OcrEnginePageResult>.Success(new OcrEnginePageResult(
            input.PageId,
            true,
            text,
            pageBBox,
            null,
            null,
            null,
            boxes.Value));
    }

    private (string ProviderId, string Model) ResolveSelection(OcrPresetVersion presetVersion)
    {
        (string providerId, string defaultModel) = _runtime.ResolveOcrSelection();
        string model = string.IsNullOrWhiteSpace(presetVersion.ModelId) ? defaultModel : presetVersion.ModelId.Trim();
        return (providerId, model);
    }

    private async Task<OcrEnvironmentCheckResult> CheckCoreAsync(OcrPresetVersion presetVersion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        (string providerId, string model) = ResolveSelection(presetVersion);
        IReadOnlyList<LlmProviderReadiness> readiness = await _runtime.InspectProvidersAsync(cancellationToken);
        LlmProviderReadiness? target = readiness.FirstOrDefault(provider =>
            string.Equals(provider.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return new OcrEnvironmentCheckResult(EngineId, model, presetVersion.ModelPath,
                OcrEnvironmentStatus.InvalidEndpoint, false,
                $"Unknown LLM provider '{providerId}'. Pick an OCR provider in Settings > LLM and Translation.",
                OcrRequiredAction.ConfigureEndpoint, []);
        }

        if (!target.HasCredential)
        {
            return new OcrEnvironmentCheckResult(EngineId, model, presetVersion.ModelPath,
                OcrEnvironmentStatus.MissingCredential, false,
                target.Diagnostic, OcrRequiredAction.ConfigureCredential, []);
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            return new OcrEnvironmentCheckResult(EngineId, model, presetVersion.ModelPath,
                OcrEnvironmentStatus.NotConfigured, false,
                $"No OCR model is configured for provider '{providerId}'.",
                OcrRequiredAction.ConfigureEndpoint, []);
        }

        if (!target.IsConfigured)
        {
            return new OcrEnvironmentCheckResult(EngineId, model, presetVersion.ModelPath,
                OcrEnvironmentStatus.NotConfigured, false,
                target.Diagnostic, OcrRequiredAction.ConfigureEndpoint, []);
        }

        return new OcrEnvironmentCheckResult(EngineId, model, presetVersion.ModelPath,
            OcrEnvironmentStatus.Ready, true,
            $"Multimodal LLM OCR is ready on provider '{providerId}' with model '{model}'.",
            OcrRequiredAction.None, []);
    }

    private static string BuildPrompt(OcrInputDescriptor input)
    {
        if (input.InputKind != OcrInputKinds.RegionImage || input.RegionBBox is not { } region)
        {
            return Prompt;
        }

        return Prompt +
               $"\nOnly recognize text inside the normalized rectangle x={region.X}, y={region.Y}, " +
               $"width={region.Width}, height={region.Height} of the image; " +
               "still report coordinates relative to the full image.";
    }

    private static string? ResolveMimeType(string path, byte[] bytes)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".png":
                return "image/png";
            case ".jpg":
            case ".jpeg":
                return "image/jpeg";
            case ".webp":
                return "image/webp";
        }

        if (bytes.Length >= 4 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return "image/png";
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (bytes.Length >= 12 && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
        {
            return "image/webp";
        }

        return null;
    }

    private sealed class UnavailableCredentialStore : ICredentialStore
    {
        public static UnavailableCredentialStore Instance { get; } = new();

        public Task<Result<ProviderCredentialMetadata>> SaveAsync(string providerId, string displayName,
            string secretValue, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<ProviderCredentialMetadata>.Failure(AppErrorCodes.UnsupportedOperation,
                "The multimodal LLM OCR adapter has no writable credential store."));
        }

        public Task<Result<string>> GetActiveSecretForProviderAsync(string providerId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<string>.Failure(AppErrorCodes.NotFound,
                "The multimodal LLM OCR adapter has no credential store; configure one in Settings."));
        }

        public Task<Result> RemoveAsync(string providerId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Failure(AppErrorCodes.UnsupportedOperation,
                "The multimodal LLM OCR adapter has no writable credential store."));
        }

        public Task<Result<IReadOnlyList<ProviderCredentialMetadata>>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ProviderCredentialMetadata> empty = [];
            return Task.FromResult(Result<IReadOnlyList<ProviderCredentialMetadata>>.Success(empty));
        }
    }
}

/// <summary>
/// Normalizes the vision model's reply into the engine text-box shape. The model is asked for one JSON
/// object with a "boxes" array; anything else is an adapter-level failure with a stable code, never a
/// guessed partial result.
/// </summary>
internal static class MultimodalLlmVisionResultParser
{
    public static Result<IReadOnlyList<OcrEngineTextBox>> Parse(string content)
    {
        int start = content.IndexOf('{');
        int end = content.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return Failure("The multimodal LLM response did not contain a JSON object.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(content.Substring(start, end - start + 1));
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (!property.Name.Equals("boxes", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                JsonElement boxes = property.Value;
                if (boxes.ValueKind != JsonValueKind.Array)
                {
                    return Failure("The multimodal LLM response carried a non-array \"boxes\" value.");
                }

                return Result<IReadOnlyList<OcrEngineTextBox>>.Success(ReadBoxes(boxes));
            }

            return Failure("The multimodal LLM response did not contain a \"boxes\" array.");
        }
        catch (JsonException)
        {
            return Failure("The multimodal LLM response was not valid JSON.");
        }
    }

    private static IReadOnlyList<OcrEngineTextBox> ReadBoxes(JsonElement boxes)
    {
        List<OcrEngineTextBox> result = [];
        foreach (JsonElement item in boxes.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? text = null;
            double? x = null;
            double? y = null;
            double? width = null;
            double? height = null;
            double? confidence = null;
            foreach (JsonProperty property in item.EnumerateObject())
            {
                if (property.Name.Equals("text", StringComparison.OrdinalIgnoreCase))
                {
                    text = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                }
                else if (property.Name.Equals("x", StringComparison.OrdinalIgnoreCase))
                {
                    x = ReadDouble(property.Value);
                }
                else if (property.Name.Equals("y", StringComparison.OrdinalIgnoreCase))
                {
                    y = ReadDouble(property.Value);
                }
                else if (property.Name.Equals("width", StringComparison.OrdinalIgnoreCase))
                {
                    width = ReadDouble(property.Value);
                }
                else if (property.Name.Equals("height", StringComparison.OrdinalIgnoreCase))
                {
                    height = ReadDouble(property.Value);
                }
                else if (property.Name.Equals("confidence", StringComparison.OrdinalIgnoreCase))
                {
                    confidence = ReadDouble(property.Value);
                }
            }

            if (string.IsNullOrWhiteSpace(text) || x is null || y is null || width is null || height is null)
            {
                continue;
            }

            double clampedX = Math.Clamp(x.Value, 0.0, 1.0);
            double clampedY = Math.Clamp(y.Value, 0.0, 1.0);
            double clampedWidth = Math.Clamp(width.Value, 0.0, 1.0 - clampedX);
            double clampedHeight = Math.Clamp(height.Value, 0.0, 1.0 - clampedY);
            result.Add(new OcrEngineTextBox(
                text.Trim(),
                new NormalizedBBox(clampedX, clampedY, clampedWidth, clampedHeight),
                confidence is null ? null : Math.Clamp(confidence.Value, 0.0, 1.0)));
        }

        return result;
    }

    private static double? ReadDouble(JsonElement value)
    {
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double parsed) ? parsed : null;
    }

    private static Result<IReadOnlyList<OcrEngineTextBox>> Failure(string message)
    {
        return Result<IReadOnlyList<OcrEngineTextBox>>.Failure(AppErrorCodes.InvalidState, message);
    }
}
