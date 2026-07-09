using Patchouli.Core.Results;

namespace Patchouli.Ocr;

public sealed class OcrModelPathValidator : IOcrModelPathValidator
{
    public Task<OcrEnvironmentCheckResult> ValidateModelPathAsync(string? modelPath, bool required, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            return Task.FromResult(Result(null, required ? OcrEnvironmentStatus.MissingModelPath : OcrEnvironmentStatus.Ready, !required,
                required ? "A model path or endpoint is required." : "No model path is required for this adapter.", required ? OcrRequiredAction.RebindModelPath : OcrRequiredAction.None));
        }

        var value = modelPath.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return Task.FromResult(Result(value, OcrEnvironmentStatus.Ready, true, "Endpoint syntax is valid; no network request was made.", OcrRequiredAction.None));
        }

        if (value.Contains("://", StringComparison.Ordinal))
        {
            return Task.FromResult(Result(value, OcrEnvironmentStatus.InvalidEndpoint, false, "The model endpoint must use http or https.", OcrRequiredAction.ConfigureEndpoint));
        }

        var exists = File.Exists(value) || Directory.Exists(value);
        return Task.FromResult(Result(value, exists ? OcrEnvironmentStatus.Ready : OcrEnvironmentStatus.ModelPathInaccessible, exists,
            exists ? "Local model path is accessible." : "Local model path does not exist or is inaccessible.", exists ? OcrRequiredAction.None : OcrRequiredAction.RebindModelPath));
    }

    private static OcrEnvironmentCheckResult Result(string? modelPath, string status, bool ready, string message, string action)
        => new("model-path-validator", "", modelPath, status, ready, message, action, []);
}

public sealed class OcrAdapterRegistry : IOcrAdapterRegistry
{
    private readonly Dictionary<string, IRealOcrAdapter> _adapters = new(StringComparer.Ordinal);

    public void RegisterAdapter(IRealOcrAdapter adapter) => _adapters[adapter.EngineId] = adapter;
    public IRealOcrAdapter? GetAdapter(string engineId) => _adapters.GetValueOrDefault(engineId);
    public IReadOnlyList<OcrEngineCapability> ListCapabilities() => _adapters.Values.Select(a => a.GetCapability()).OrderBy(a => a.EngineId, StringComparer.Ordinal).ToArray();
    public async Task<Result<OcrEnvironmentCheckResult>> CheckEngineAsync(string engineId, OcrPresetVersion presetVersion, CancellationToken cancellationToken = default)
    {
        var adapter = GetAdapter(engineId);
        return adapter is null
            ? Result<OcrEnvironmentCheckResult>.Failure(AppErrorCodes.NotFound, $"OCR adapter '{engineId}' is not registered.")
            : Result<OcrEnvironmentCheckResult>.Success(await adapter.CheckEnvironmentAsync(presetVersion, cancellationToken));
    }
}

public sealed class MockOcrAdapter : IRealOcrAdapter
{
    public string EngineId => OcrEngineIds.Mock;
    public string DisplayName => "Mock OCR";
    public string Kind => OcrAdapterKind.Mock;
    public OcrEngineCapability GetCapability() => new(EngineId, DisplayName, false, false, false, false, false, false, false, false, false, [], "Produces deterministic test text only.");
    public Task<OcrEnvironmentCheckResult> CheckEnvironmentAsync(OcrPresetVersion presetVersion, CancellationToken cancellationToken = default) => Task.FromResult(new OcrEnvironmentCheckResult(EngineId, presetVersion.ModelId, presetVersion.ModelPath, OcrEnvironmentStatus.Ready, true, "Mock OCR is ready for workflow tests.", OcrRequiredAction.None, []));
    public Task<Result> ValidatePresetAsync(OcrPresetVersion presetVersion, CancellationToken cancellationToken = default) => Task.FromResult(Result.Success());
    public Task<Result> ValidateInputAsync(OcrInputDescriptor input, CancellationToken cancellationToken = default) => Task.FromResult(Result.Success());
    public Task<Result<OcrEnginePageResult>> RunPageAsync(OcrInputDescriptor input, OcrPresetVersion presetVersion, CancellationToken cancellationToken = default) => Task.FromResult(Result<OcrEnginePageResult>.Failure(AppErrorCodes.UnsupportedOperation, "Mock OCR uses the existing page-based test engine."));
}

public sealed class LocalPlaceholderOcrAdapter : IRealOcrAdapter
{
    private readonly IOcrModelPathValidator _validator;
    public LocalPlaceholderOcrAdapter(IOcrModelPathValidator validator) => _validator = validator;
    public string EngineId => OcrEngineIds.LocalPlaceholder;
    public string DisplayName => "Local OCR placeholder (not implemented)";
    public string Kind => OcrAdapterKind.LocalProcess;
    public OcrEngineCapability GetCapability() => new(EngineId, DisplayName, true, false, true, false, true, true, true, false, true, [OcrInputKinds.PageImage, OcrInputKinds.ImageFile, OcrInputKinds.RegionImage], "Contract-only adapter; it never invokes a real OCR engine in this Alpha.");
    public async Task<OcrEnvironmentCheckResult> CheckEnvironmentAsync(OcrPresetVersion presetVersion, CancellationToken cancellationToken = default)
    {
        var result = await _validator.ValidateModelPathAsync(presetVersion.ModelPath, required: true, cancellationToken);
        return result with { EngineId = EngineId, ModelId = presetVersion.ModelId };
    }
    public Task<Result> ValidatePresetAsync(OcrPresetVersion presetVersion, CancellationToken cancellationToken = default) => Task.FromResult(Result.Success());
    public Task<Result> ValidateInputAsync(OcrInputDescriptor input, CancellationToken cancellationToken = default)
        => Task.FromResult(input.InputKind is OcrInputKinds.PageImage or OcrInputKinds.ImageFile or OcrInputKinds.RegionImage ? Result.Success() : Result.Failure(AppErrorCodes.ValidationFailed, "The local placeholder does not support this OCR input kind."));
    public Task<Result<OcrEnginePageResult>> RunPageAsync(OcrInputDescriptor input, OcrPresetVersion presetVersion, CancellationToken cancellationToken = default)
        => Task.FromResult(Result<OcrEnginePageResult>.Failure(AppErrorCodes.UnsupportedOperation, "Real OCR adapters are not implemented in this Alpha."));
}

public sealed class MinerUOcrAdapter : IRealOcrAdapter
{
    public string EngineId => OcrEngineIds.MinerU;
    public string DisplayName => "MinerU OCR";
    public string Kind => OcrAdapterKind.CloudApi;
    public OcrEngineCapability GetCapability() => new(EngineId, DisplayName, false, true, false, true, false, true, true, true, false, [OcrInputKinds.PdfPage], "Runs as a document-level OCR preset and imports MinerU result zips into Patchouli layout revisions.");
    public Task<OcrEnvironmentCheckResult> CheckEnvironmentAsync(OcrPresetVersion presetVersion, CancellationToken cancellationToken = default) =>
        Task.FromResult(new OcrEnvironmentCheckResult(EngineId, presetVersion.ModelId, presetVersion.ModelPath, OcrEnvironmentStatus.Ready, true, "MinerU preset is ready; the API token is checked when the run starts.", OcrRequiredAction.None, []));
    public Task<Result> ValidatePresetAsync(OcrPresetVersion presetVersion, CancellationToken cancellationToken = default) => Task.FromResult(Result.Success());
    public Task<Result> ValidateInputAsync(OcrInputDescriptor input, CancellationToken cancellationToken = default) =>
        Task.FromResult(input.InputKind == OcrInputKinds.PdfPage ? Result.Success() : Result.Failure(AppErrorCodes.ValidationFailed, "MinerU OCR expects direct PDF input."));
    public Task<Result<OcrEnginePageResult>> RunPageAsync(OcrInputDescriptor input, OcrPresetVersion presetVersion, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<OcrEnginePageResult>.Failure(AppErrorCodes.UnsupportedOperation, "MinerU OCR runs through RunPresetOnDocumentAsync."));
}

public sealed class MultimodalLlmOcrAdapter : IRealOcrAdapter
{
    public string EngineId => OcrEngineIds.MultimodalLlm;
    public string DisplayName => "Multimodal LLM OCR";
    public string Kind => OcrAdapterKind.CloudApi;
    public OcrEngineCapability GetCapability() => new(EngineId, DisplayName, false, true, true, false, true, true, false, true, false, [OcrInputKinds.PageImage, OcrInputKinds.RegionImage], "Accepts page or region images from a multimodal LLM endpoint and must normalize output into the MinerU-compatible layout pipeline before adoption.");
    public Task<OcrEnvironmentCheckResult> CheckEnvironmentAsync(OcrPresetVersion presetVersion, CancellationToken cancellationToken = default)
    {
        var ready = !string.IsNullOrWhiteSpace(presetVersion.ModelPath);
        return Task.FromResult(new OcrEnvironmentCheckResult(EngineId, presetVersion.ModelId, presetVersion.ModelPath, ready ? OcrEnvironmentStatus.Ready : OcrEnvironmentStatus.InvalidEndpoint, ready, ready ? "LLM OCR endpoint/model is configured; credentials are checked when the run starts." : "Configure a multimodal LLM OCR endpoint/model.", ready ? OcrRequiredAction.None : OcrRequiredAction.ConfigureEndpoint, []));
    }
    public Task<Result> ValidatePresetAsync(OcrPresetVersion presetVersion, CancellationToken cancellationToken = default) =>
        Task.FromResult(string.IsNullOrWhiteSpace(presetVersion.ModelPath) ? Result.Failure(AppErrorCodes.ValidationFailed, "Multimodal LLM OCR requires an endpoint or model id.") : Result.Success());
    public Task<Result> ValidateInputAsync(OcrInputDescriptor input, CancellationToken cancellationToken = default) =>
        Task.FromResult(input.InputKind is OcrInputKinds.PageImage or OcrInputKinds.RegionImage ? Result.Success() : Result.Failure(AppErrorCodes.ValidationFailed, "Multimodal LLM OCR expects page or region images."));
    public Task<Result<OcrEnginePageResult>> RunPageAsync(OcrInputDescriptor input, OcrPresetVersion presetVersion, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<OcrEnginePageResult>.Failure(AppErrorCodes.UnsupportedOperation, "Multimodal LLM OCR transport is not configured yet; provider output must enter through the MinerU-compatible layout importer."));
}
