using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.Extensions.Sdk;

namespace Patchouli.Ocr.Engines.PdfText;

/// <summary>
/// A compiled-package provider: pins the analyzer, maps the package's attributed settings
/// record onto neutral parse options, and exposes the settings type/schema expected by
/// <see cref="IExtensionSettingsDefinition"/>. Package source news this up directly — no
/// factory dependency on <c>Patchouli.Extensions.dll</c>.
/// </summary>
public sealed class PdfTextLayerExtension<TSettings> : IPdfTextLayerExtension, IExtensionSettingsDefinition
{
    private readonly IPdfTextLayerAnalyzer _analyzer;
    private readonly Func<TSettings, PdfTextLayerParseOptions> _mapOptions;

    public PdfTextLayerExtension(
        PdfTextLayerDefinition definition,
        IPdfTextLayerAnalyzer analyzer,
        Func<TSettings, PdfTextLayerParseOptions> mapOptions)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(analyzer);
        ArgumentNullException.ThrowIfNull(mapOptions);
        ProviderDefinition = definition;
        _analyzer = analyzer;
        _mapOptions = mapOptions;
    }

    public PdfTextLayerDefinition ProviderDefinition { get; }

    public Type SettingsType => typeof(TSettings);

    public IReadOnlyList<ConfigFieldDescriptor> ConfigFields => ConfigSettings.Describe<TSettings>();

    public Task<Result<PdfTextLayerResult>> ParseAsync(
        PdfTextLayerContext context,
        IExtensionResources resources,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        TSettings settings = ConfigSettings.Read<TSettings>(context.Config);
        PdfTextLayerParseOptions options = _mapOptions(settings);
        return _analyzer.AnalyzeAsync(context, options, cancellationToken);
    }
}

/// <summary>Entry points the built-in FSX packages use to create their exports.</summary>
public static class PdfTextLayerExtensions
{
    /// <summary>
    /// Declarative provider metadata: the same instances the factories report as
    /// <see cref="IPdfTextLayerExtension.ProviderDefinition"/> and the host lists for preset
    /// pickers — a single source of truth that never activates a package, opens a model
    /// session, or runs an analyzer.
    /// </summary>
    public static PdfTextLayerDefinition XyCutDefinition { get; } = new(
        PdfTextLayerProviderIds.NativeXyCut,
        "Native XY Cut text layer",
        Notes: "Segment the PDFium text layer with recursive XY cuts; no rendering, no models.");

    /// <inheritdoc cref="XyCutDefinition" />
    public static PdfTextLayerDefinition GapTreeDefinition { get; } = new(
        PdfTextLayerProviderIds.NativeGapTree,
        "Native GapTree text layer",
        Notes: "Order the PDFium text layer with the gap-tree layout sort; no rendering, no models.");

    /// <inheritdoc cref="XyCutDefinition" />
    public static PdfTextLayerDefinition VisualGuidedDefinition { get; } = new(
        PdfTextLayerProviderIds.VisualGuided,
        "Visual-guided text layer",
        RequiresPageImage: true,
        RequiresLayoutModel: true,
        Notes: "Match the shared layout detector's regions to native glyphs; only missing or " +
               "untrustworthy regions go to the image OCR engine.");

    /// <summary>The native recursive XY Cut provider. Pure text-layer input — no pixels.</summary>
    public static IPdfTextLayerExtension CreateXyCut<TSettings>(
        Func<TSettings, PdfTextLayerParseOptions> mapOptions)
    {
        return new PdfTextLayerExtension<TSettings>(XyCutDefinition,
            new XyCutTextLayerAnalyzer(), mapOptions);
    }

    /// <summary>The native GapTree provider. Pure text-layer input — no pixels.</summary>
    public static IPdfTextLayerExtension CreateGapTree<TSettings>(
        Func<TSettings, PdfTextLayerParseOptions> mapOptions)
    {
        return new PdfTextLayerExtension<TSettings>(GapTreeDefinition,
            new GapTreeTextLayerAnalyzer(), mapOptions);
    }

    /// <summary>
    /// The visual-guided provider: shared layout detection steers native-glyph preservation
    /// and bounded region OCR. Requires the host's page-image and layout-detection delegates.
    /// </summary>
    public static IPdfTextLayerExtension CreateVisualGuided<TSettings>(
        Func<TSettings, PdfTextLayerParseOptions> mapOptions)
    {
        return new PdfTextLayerExtension<TSettings>(VisualGuidedDefinition,
            new VisualGuidedTextLayerAnalyzer(), mapOptions);
    }
}
