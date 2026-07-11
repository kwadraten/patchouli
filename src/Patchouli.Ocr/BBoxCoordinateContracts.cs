using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;

namespace Patchouli.Ocr;

public static class SourceBBoxCoordinateSystem
{
    public const string ImagePixels = "image_pixels";
    public const string NormalizedPage = "normalized_page";
    public const string EngineRaw = "engine_raw";
    public const string Unknown = "unknown";
}

public static class BBoxWarning
{
    public const string None = "none";
    public const string SourceChanged = "source_changed";
    public const string BasisStale = "bbox_basis_stale";
    public const string RotationUnsupported = "rotation_unsupported";
    public const string ClippedToPage = "clipped_to_page";
    public const string LowConfidence = "low_confidence";
    public const string UnknownBasis = "unknown_basis";
}

public static class BBoxErrorCodes
{
    public const string Missing = "bbox_missing";
    public const string Invalid = "bbox_invalid";
    public const string OutOfBounds = "bbox_out_of_bounds";
    public const string BasisMissing = "bbox_basis_missing";
    public const string BasisMismatch = "bbox_basis_mismatch";
    public const string TransformFailed = "bbox_coordinate_transform_failed";
    public const string RotationUnsupported = "rotation_unsupported";
}

public sealed record SourceBBox(
    double X,
    double Y,
    double Width,
    double Height,
    string CoordinateSystem,
    double? BasisWidth = null,
    double? BasisHeight = null,
    int Rotation = 0,
    string? EngineName = null,
    double? Confidence = null);

public sealed record BBoxConversionResult(
    bool IsSuccess,
    NormalizedBBox? NormalizedBBox,
    string? ErrorCode,
    string? Message,
    string? Warning);

public sealed record PageCoordinateBasis(
    PageId PageId,
    string CoordinateBasis,
    double? BasisWidth,
    double? BasisHeight,
    int Rotation,
    string RendererBasisVersion,
    string? SourceFileHash);

public interface IPageCoordinateService
{
    Task<BBoxConversionResult> ConvertToNormalizedPageAsync(PageId pageId, SourceBBox sourceBBox,
        CancellationToken cancellationToken = default);

    Result ValidateNormalizedBBox(NormalizedBBox bbox);

    Task<Result<PageCoordinateBasis>> GetPageCoordinateBasisAsync(PageId pageId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> DetectBBoxWarningsAsync(PageId pageId, FileAssetId? fileAssetId = null,
        CancellationToken cancellationToken = default);
}
