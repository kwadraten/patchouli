using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;

namespace Patchouli.Core.View;

/// <summary>Which image the view call returns for a box/evidence URI.</summary>
public enum PageImageViewContext
{
    /// <summary>Return the whole page containing the box.</summary>
    Page = 0,

    /// <summary>Return the box bbox crop with a small margin.</summary>
    Region = 1
}

/// <summary>Issue #11: request to read one source image for model text-image collation.</summary>
/// <param name="DocumentInstanceId">Source document instance (translation URIs resolve to this).</param>
/// <param name="PageIndex1Based">One-based physical PDF page index.</param>
/// <param name="RevisionId">Tree revision; null pins to the actually-used revision of this call.</param>
/// <param name="BoxId">Box id; null addresses the whole page.</param>
/// <param name="IsTranslation">True when the caller addressed the translations/ tree.</param>
/// <param name="Context">Page returns the whole page; region returns the bbox crop.</param>
/// <param name="MaxEdge">Longest-edge pixel cap for the returned image.</param>
public sealed record PageImageViewRequest(
    DocumentInstanceId DocumentInstanceId,
    int PageIndex1Based,
    DocumentTreeRevisionId? RevisionId,
    DocumentBoxId? BoxId,
    bool IsTranslation,
    PageImageViewContext Context,
    int MaxEdge)
{
    public const int DefaultMaxEdge = 2048;

    public Result Validate()
    {
        if (PageIndex1Based < 1)
        {
            return Result.Failure(AppErrorCodes.InvalidArgument, "Page index must be one-based.");
        }

        if (MaxEdge is < 64 or > 8192)
        {
            return Result.Failure(AppErrorCodes.InvalidArgument, "max_edge must be between 64 and 8192.");
        }

        return Result.Success();
    }
}

/// <summary>One rendered source image with its reproducible metadata.</summary>
/// <param name="CanonicalUri">Canonical URI pinned to the actually-used revision.</param>
/// <param name="ViewType">page | box | translation_page | translation_box.</param>
/// <param name="MimeType">Always image/png in v1.</param>
/// <param name="Width">Actual image width in pixels.</param>
/// <param name="Height">Actual image height in pixels.</param>
/// <param name="CropRange">Actual crop range in page-normalized coordinates (reproducible).</param>
/// <param name="ImageIndex">Index of the image content block; v1 fixed at 0.</param>
/// <param name="UsedRevision">The tree revision actually used for this call.</param>
/// <param name="PngBytes">Encoded PNG bytes (never truncated).</param>
public sealed record PageImageViewResult(
    string CanonicalUri,
    string ViewType,
    string MimeType,
    int Width,
    int Height,
    NormalizedBBox CropRange,
    int ImageIndex,
    string UsedRevision,
    byte[] PngBytes);

/// <summary>Stable error codes for page image view failures.</summary>
public static class PageImageViewErrorCodes
{
    public const string TargetNotFound = "view_target_not_found";
    public const string StaleRevision = "view_stale_revision";
    public const string StaleCoordinates = "view_stale_coordinates";
    public const string SourceMissing = "view_source_missing";
    public const string SourceChanged = "view_source_changed";
    public const string RenderFailed = "view_render_failed";
    public const string ImageTooLarge = "view_image_too_large";
    public const string PageSpecificationRequired = "view_page_specification_required";
}

/// <summary>View type vocabulary for <see cref="PageImageViewResult.ViewType"/>.</summary>
public static class PageImageViewTypes
{
    public const string Page = "page";
    public const string Box = "box";
    public const string TranslationPage = "translation_page";
    public const string TranslationBox = "translation_box";
}
