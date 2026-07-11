using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;

namespace Patchouli.Ocr;

public static class OcrAdapterKind
{
    public const string Mock = "mock";
    public const string LocalProcess = "local_process";
    public const string LocalLibrary = "local_library";
    public const string CloudApi = "cloud_api";
}

public static class OcrEnvironmentStatus
{
    public const string Ready = "ready";
    public const string MissingModelPath = "missing_model_path";
    public const string ModelPathInaccessible = "model_path_inaccessible";
    public const string MissingExecutable = "missing_executable";
    public const string MissingCredential = "missing_credential";
    public const string InvalidEndpoint = "invalid_endpoint";
    public const string UnsupportedPlatform = "unsupported_platform";
    public const string NotConfigured = "not_configured";
    public const string UnknownError = "unknown_error";
}

public static class OcrRequiredAction
{
    public const string None = "none";
    public const string RebindModelPath = "rebind_model_path";
    public const string InstallEngine = "install_engine";
    public const string ConfigureCredential = "configure_credential";
    public const string ConfigureEndpoint = "configure_endpoint";
    public const string ChooseDifferentPreset = "choose_different_preset";
}

public static class OcrInputKinds
{
    public const string PageImage = "page_image";
    public const string PdfPage = "pdf_page";
    public const string ImageFile = "image_file";
    public const string RegionImage = "region_image";
}

public sealed record OcrEngineCapability(
    string EngineId,
    string DisplayName,
    bool SupportsLocalModel,
    bool SupportsRemoteEndpoint,
    bool SupportsPageImage,
    bool SupportsPdfDirectInput,
    bool SupportsRegionOcr,
    bool SupportsVerticalText,
    bool SupportsTableDetection,
    bool RequiresCredential,
    bool RequiresModelPath,
    IReadOnlyList<string> SupportedInputKinds,
    string Notes);

public sealed record OcrEnvironmentCheckResult(
    string EngineId,
    string ModelId,
    string? ModelPath,
    string Status,
    bool IsReady,
    string Message,
    string RequiredAction,
    IReadOnlyList<string> Warnings);

public sealed record OcrInputDescriptor(
    PageId PageId,
    DocumentInstanceId DocumentInstanceId,
    string InputKind,
    string? ImagePath,
    string? PdfPath,
    NormalizedBBox? RegionBBox,
    string SourceFileStatus,
    string? Warning);

public interface IRealOcrAdapter
{
    string EngineId { get; }
    string DisplayName { get; }
    string Kind { get; }
    OcrEngineCapability GetCapability();

    Task<OcrEnvironmentCheckResult> CheckEnvironmentAsync(OcrPresetVersion presetVersion,
        CancellationToken cancellationToken = default);

    Task<Result> ValidatePresetAsync(OcrPresetVersion presetVersion, CancellationToken cancellationToken = default);
    Task<Result> ValidateInputAsync(OcrInputDescriptor input, CancellationToken cancellationToken = default);

    Task<Result<OcrEnginePageResult>> RunPageAsync(OcrInputDescriptor input, OcrPresetVersion presetVersion,
        CancellationToken cancellationToken = default);
}

public interface IOcrModelPathValidator
{
    Task<OcrEnvironmentCheckResult> ValidateModelPathAsync(string? modelPath, bool required,
        CancellationToken cancellationToken = default);
}

public interface IOcrAdapterRegistry
{
    void RegisterAdapter(IRealOcrAdapter adapter);
    IRealOcrAdapter? GetAdapter(string engineId);
    IReadOnlyList<OcrEngineCapability> ListCapabilities();

    Task<Result<OcrEnvironmentCheckResult>> CheckEngineAsync(string engineId, OcrPresetVersion presetVersion,
        CancellationToken cancellationToken = default);
}
