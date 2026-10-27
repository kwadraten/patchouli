using Patchouli.Core.Documents;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;

namespace Patchouli.Extensions.Contracts;

/// <summary>
/// Identity constants for PDF text-layer providers. Provider ids name <em>text-layer</em>
/// extraction schemes; they are not image OCR <c>EngineId</c> values. The image engine remains
/// a separately selected runtime concern used only for explicitly requested regions.
/// </summary>
public static class PdfTextLayerProviderIds
{
    /// <summary>Host-only sentinel for "no PDF text-layer provider". Never an extension.</summary>
    public const string Disabled = "disabled";

    public const string NativeXyCut = "native-xycut";
    public const string NativeGapTree = "native-gaptree";
    public const string VisualGuided = "visual-guided";
}

/// <summary>Extension package ids for the built-in PDF text-layer providers.</summary>
public static class PdfTextLayerPackageIds
{
    public const string NativeXyCut = "pdf-text.native-xycut";
    public const string NativeGapTree = "pdf-text.native-gaptree";
    public const string VisualGuided = "pdf-text.visual-guided";
}

/// <summary>
/// The writing direction of a local text region. The inline axis is the direction characters
/// advance within a line; the block axis is the direction lines advance within a region.
/// </summary>
public enum WritingDirection
{
    /// <summary>Inline +x, block +y — Latin-style horizontal text.</summary>
    HorizontalLtr = 0,

    /// <summary>Inline −x, block +y — Hebrew/Arabic-style horizontal text.</summary>
    HorizontalRtl = 1,

    /// <summary>Inline +y (down), block −x (columns advance right→left) — CJK vertical.</summary>
    VerticalRl = 2,

    /// <summary>Inline +y, block +x — Mongolian-style vertical text.</summary>
    VerticalLr = 3,

    /// <summary>The region mixes local frames or rotations; members carry per-glyph anchors.</summary>
    Mixed = 4
}

/// <summary>
/// A region's writing coordinate frame. <see cref="Direction"/> selects the axis transform used
/// to project glyph geometry into (inline, block) algorithm space;
/// <see cref="RotationDegrees"/> is the display-space clockwise rotation of the frame's inline
/// axis in degrees (0 for the axis-aligned directions above). Region-local frames allow a page
/// to carry mixed rotations — e.g. rotated side-notes or vertical CJK beside horizontal Latin.
/// </summary>
public readonly record struct WritingFrame(WritingDirection Direction, double RotationDegrees = 0)
{
    public static WritingFrame HorizontalLtr => new(WritingDirection.HorizontalLtr);
    public static WritingFrame HorizontalRtl => new(WritingDirection.HorizontalRtl);
    public static WritingFrame VerticalRl => new(WritingDirection.VerticalRl);
    public static WritingFrame VerticalLr => new(WritingDirection.VerticalLr);
    public static WritingFrame MixedFrame => new(WritingDirection.Mixed);
}

/// <summary>
/// Provider-neutral characterization of one native PDF text character. The character index is
/// the original source anchor: it never changes across segmentation, ordering or RTL handling,
/// so any derived box can be traced back to the exact glyphs it came from.
/// </summary>
[Flags]
public enum PdfGlyphFlags
{
    None = 0,

    /// <summary>PDFium reports the character as synthesized whitespace/return, not page content.</summary>
    Generated = 1,

    /// <summary>PDFium reports the font's Unicode map as broken for this character.</summary>
    UnicodeMapError = 2,

    /// <summary>PDFium reports the character as a soft/hard hyphen line-break artifact.</summary>
    Hyphen = 4,

    /// <summary>No usable Unicode scalar could be produced; <c>PdfTextGlyph.Text</c> is empty.</summary>
    NoUnicodeMapping = 8,

    /// <summary>
    /// The index is the low-surrogate half of a supplementary-plane scalar whose text is
    /// carried by the preceding index. Continuation indexes keep their native anchor and are
    /// trustworthy — their empty <c>PdfTextGlyph.Text</c> is structural, not unconvertible.
    /// </summary>
    SurrogateContinuation = 16
}

public enum PdfGlyphOrientation
{
    Horizontal,
    Vertical,
    Rotated
}

/// <summary>
/// One native character with provider-neutral geometry. All coordinates are normalized to the
/// display page ([0,1], top-left origin, after page rotation is applied), matching
/// <see cref="CoordinateBasis.NormalizedPage"/> so results can flow into
/// <see cref="DocumentBoxSeed"/> unchanged.
/// </summary>
public sealed record PdfTextGlyph(
    int NativeIndex,
    string Text,
    NormalizedBBox BBox,
    double BaselineX,
    double BaselineY,
    double AngleDegrees,
    PdfGlyphOrientation Orientation,
    string? FontName,
    double FontSize,
    PdfGlyphFlags Flags = PdfGlyphFlags.None);

public enum PdfTextLayerDiagnosticSeverity
{
    Information,
    Warning,
    Error
}

/// <summary>Stable diagnostic codes for the PDF text-layer pipeline.</summary>
public static class PdfTextLayerDiagnosticCodes
{
    public const string PageImageUnavailable = "page_image_unavailable";
    public const string LayoutDetectorUnavailable = "layout_detector_unavailable";
    public const string LayoutDetectionFailed = "layout_detection_failed";
    public const string LayoutModelMissing = "layout_model_missing";
    public const string GlyphsOutsideLayout = "glyphs_outside_layout_regions";
    public const string UnaccountedGlyphs = "unaccounted_glyphs";
    public const string UnicodeUnconvertible = "unicode_unconvertible_glyphs";
    public const string PageRotationMismatch = "page_rotation_mismatch";
    public const string CutLine = "cut_line";
    public const string Gap = "gap";
    public const string WritingFrameDetected = "writing_frame_detected";
    public const string NativeTextMissing = "native_text_missing";

    /// <summary>Native glyphs exist but are unconvertible and the selected scheme does not supplement them.</summary>
    public const string NativeTextUnusable = "native_text_unusable";

    /// <summary>The persisted page baseline disagrees with the source's native page geometry.</summary>
    public const string SourceBasisMismatch = "source_basis_mismatch";

    /// <summary>PDFium's effective page box differs from the media box the transform assumes.</summary>
    public const string PageBoxMismatch = "page_box_mismatch";

    /// <summary>The persisted render basis predates page rotation; the host basis was reconciled.</summary>
    public const string RotationBasisReconciled = "rotation_basis_reconciled";

    /// <summary>The persisted page baseline was written against older source bytes and was ignored.</summary>
    public const string PageBasisStale = "page_basis_stale";

    /// <summary>Unconvertible glyphs were reported as a bounded region request.</summary>
    public const string UntrustedGlyphs = "untrusted_glyphs";

    /// <summary>A supplied layout region carried degenerate or out-of-range geometry.</summary>
    public const string InvalidRegion = "invalid_region";

    /// <summary>A text region's trusted native coverage is below the configured minimum.</summary>
    public const string LowNativeCoverage = "low_native_coverage";

    /// <summary>
    /// A native glyph index was claimed by both a produced box and a required-OCR region —
    /// ownership must be exactly once, so the conflict is surfaced rather than deduplicated.
    /// </summary>
    public const string DuplicateGlyphOwnership = "duplicate_glyph_ownership";
}

public sealed record PdfTextLayerDiagnostic(
    string Code,
    string Message,
    PdfTextLayerDiagnosticSeverity Severity = PdfTextLayerDiagnosticSeverity.Information,
    NormalizedBBox? Region = null);

/// <summary>
/// A raster of the display page (page rotation already applied) for providers that legitimately
/// need pixels — e.g. visual-guided layout detection. Native-only providers never receive one.
/// </summary>
public sealed record PdfTextPageImage(
    byte[] PngBytes,
    int WidthPixels,
    int HeightPixels,
    int Dpi);

/// <summary>A provider-neutral layout region reported by the host's shared detector.</summary>
public sealed record PdfTextLayoutRegion(
    string Label,
    NormalizedBBox BBox,
    double Confidence,
    int? Order = null);

/// <summary>
/// Frozen page geometry and source-basis facts the host verified before calling the provider.
/// <see cref="SourceFileHash"/> is the validated BLAKE3 of the file bytes that produced
/// <see cref="PdfTextLayerContext.Glyphs"/>.
/// </summary>
public sealed record PdfTextPageGeometry(
    double WidthPoints,
    double HeightPoints,
    int RotationDegrees,
    string? SourceFileHash = null);

/// <summary>
/// The frozen input a PDF text-layer provider parses: page identity, the provider-neutral
/// native glyph stream (in native index order), verified page geometry and a configuration
/// snapshot. Optional lazy delegates are supplied only when the selected provider declared the
/// corresponding requirement, so a native-only provider can never silently consume pixels.
/// </summary>
public sealed record PdfTextLayerContext(
    ExtensionPageIdentity Page,
    IReadOnlyList<PdfTextGlyph> Glyphs,
    PdfTextPageGeometry Geometry,
    ExtensionConfigSnapshot Config,
    NormalizedBBox? Region = null,
    IReadOnlyList<PdfTextLayerDiagnostic>? ExtractionDiagnostics = null,
    IProgress<ExtensionProgress>? Progress = null)
{
    /// <summary>
    /// Lazily renders the display page to PNG at the requested DPI. Supplied only when
    /// <see cref="PdfTextLayerDefinition.RequiresPageImage"/> is true.
    /// </summary>
    public Func<int, CancellationToken, ValueTask<PdfTextPageImage>>? ReadPageImageAsync { get; init; }

    /// <summary>
    /// Runs the host's shared document layout detector against a provider-rendered page image.
    /// The provider passes the model id and resolved model path from its own configuration, so
    /// the visual scheme stays provider-selected; the host supplies the actual detector
    /// implementation. Supplied only when
    /// <see cref="PdfTextLayerDefinition.RequiresLayoutModel"/> is true.
    /// </summary>
    public Func<PdfTextPageImage, string, string, CancellationToken,
        ValueTask<Result<IReadOnlyList<PdfTextLayoutRegion>>>>? DetectLayoutAsync { get; init; }
}

/// <summary>
/// A contiguous run of native text-index values, in native order. Anchors preserve the exact
/// original characters behind a produced box or an OCR-required region without reversing or
/// rewriting the underlying glyph stream.
/// </summary>
public sealed record PdfTextAnchor(int StartCharIndex, int CharCount);

/// <summary>Why a region must be handed to the image OCR engine.</summary>
public static class PdfTextOcrReasons
{
    /// <summary>The region's layout calls for text but the native layer has none.</summary>
    public const string MissingText = "missing_text";

    /// <summary>Native glyphs exist but are not usable (broken encoding, unmapped chars).</summary>
    public const string UntrustworthyText = "untrustworthy_native_text";

    /// <summary>The region is non-textual content (figure, table, formula, seal, chart).</summary>
    public const string NonTextualContent = "non_textual_content";

    /// <summary>The native characters carry explicit Unicode-map errors.</summary>
    public const string UnicodeMapError = "unicode_map_error";
}

/// <summary>
/// An explicit region the selected image OCR engine must recognize. Region requests are how a
/// provider asks for OCR — never a whole-page fallback: missing-text fallback is a host
/// orchestration policy emitted as a full-page job input, not a provider-declared region.
/// </summary>
public sealed record PdfTextRequiredRegion(
    NormalizedBBox Region,
    string Reason,
    string? Detail = null,
    IReadOnlyList<PdfTextAnchor>? Anchors = null);

/// <summary>A typed box candidate plus the original characters that produced it.</summary>
public sealed record PdfTextLayerBox(
    DocumentBoxSeed Box,
    IReadOnlyList<PdfTextAnchor> Anchors);

/// <summary>
/// Coverage accounting for the parse: every supplied glyph must land in exactly one box anchor
/// or one required-region anchor so nothing is silently dropped or OCR'd twice.
/// </summary>
public sealed record PdfTextLayerStatistics(
    int SuppliedGlyphCount,
    int AccountedGlyphCount,
    int TextBoxCount);

public sealed record PdfTextLayerResult(
    IReadOnlyList<PdfTextLayerBox> Boxes,
    IReadOnlyList<PdfTextRequiredRegion> RequiredOcrRegions,
    IReadOnlyList<PdfTextLayerDiagnostic> Diagnostics,
    PdfTextLayerStatistics? Statistics = null);

/// <summary>
/// Provider identity and resource requirements. <see cref="ProviderId"/> is a text-layer
/// provider id (see <see cref="PdfTextLayerProviderIds"/>), independent of image OCR engine ids.
/// </summary>
public sealed record PdfTextLayerDefinition(
    string ProviderId,
    string DisplayName,
    int ConfigurationVersion = 1,
    bool RequiresPageImage = false,
    bool RequiresLayoutModel = false,
    IReadOnlyDictionary<string, string>? DefaultConfig = null,
    string? Notes = null);

/// <summary>
/// The PDF text-layer extension contract. Runtime supplies a frozen per-page context built from
/// verified source bytes; the provider owns segmentation, direction-aware ordering and typed
/// result generation. Provider exports implement this interface plus
/// <see cref="IExtensionSettingsDefinition"/> directly until the runtime adds a factory.
/// </summary>
public interface IPdfTextLayerExtension : IExtensionDefinition
{
    PdfTextLayerDefinition ProviderDefinition { get; }

    /// <summary>
    /// Parses one page's native glyph stream into typed box candidates and explicit OCR-region
    /// requests. Implementations must return a failure — never a synthesized whole-page OCR
    /// request — when the selected scheme cannot run (e.g. missing native text for a native
    /// provider, missing layout model for visual-guided). Whether a missing-text page then falls
    /// back to whole-page image OCR is the host's declared policy, not this contract's concern.
    /// </summary>
    Task<Result<PdfTextLayerResult>> ParseAsync(
        PdfTextLayerContext context,
        IExtensionResources resources,
        CancellationToken cancellationToken = default);
}
