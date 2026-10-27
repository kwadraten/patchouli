using System.Reflection;
using System.Text;
using Patchouli.Extensions.Contracts;
using Patchouli.Ocr.Engines;
using Patchouli.Ocr.Engines.PdfText;

namespace Patchouli.Infrastructure.Ocr;

/// <summary>
/// FSX package snapshots for the built-in PDF text-layer providers. Each package ships its
/// attributed-settings <c>main.fsx</c> and the same support binary closure the OCR engine
/// packages carry (<c>lib/Patchouli.Ocr.Engines.dll</c> plus its managed/native dependencies),
/// so compiled artifacts resolve <c>#r "lib/Patchouli.Ocr.Engines.dll"</c> package-locally.
/// The packages contain no model weights: visual-guided reads the user-configured layout model
/// directory at run time.
/// </summary>
public static class BuiltInPdfTextLayerExtensions
{
    private const string ResourcePrefix = "Patchouli.Infrastructure.Ocr.Extensions.";

    private static readonly string[] PackageIds =
    [
        PdfTextLayerPackageIds.NativeXyCut,
        PdfTextLayerPackageIds.NativeGapTree,
        PdfTextLayerPackageIds.VisualGuided
    ];

    private static readonly IReadOnlyList<ExtensionPackageSnapshot> BuiltInPackages =
        Array.AsReadOnly(PackageIds.Select(CreatePackage).ToArray());

    private static readonly IReadOnlyDictionary<string, PdfTextLayerDefinition> ProviderDefinitions =
        new Dictionary<string, PdfTextLayerDefinition>(StringComparer.Ordinal)
        {
            [PdfTextLayerPackageIds.NativeXyCut] = PdfTextLayerExtensions.XyCutDefinition,
            [PdfTextLayerPackageIds.NativeGapTree] = PdfTextLayerExtensions.GapTreeDefinition,
            [PdfTextLayerPackageIds.VisualGuided] = PdfTextLayerExtensions.VisualGuidedDefinition
        };

    /// <summary>The immutable package snapshots; the same list feeds schema generation and install.</summary>
    public static IReadOnlyList<ExtensionPackageSnapshot> Packages => BuiltInPackages;

    /// <summary>
    /// Declarative provider metadata keyed by package id — the same
    /// <see cref="PdfTextLayerExtensions.XyCutDefinition"/> and sibling instances the
    /// factories report. Capability listing consumes this without compiling a package,
    /// opening a model session, or running an analyzer.
    /// </summary>
    public static IReadOnlyDictionary<string, PdfTextLayerDefinition> Definitions =>
        ProviderDefinitions;

    private static ExtensionPackageSnapshot CreatePackage(string id)
    {
        Dictionary<string, string> sources = new(StringComparer.Ordinal)
        {
            ["main.fsx"] = ReadResource(ResourcePrefix + id + ".main.fsx")
        };
        return new ExtensionPackageSnapshot(id, sources, NativeOcrSupportFiles.CollectBinaries());
    }

    private static string ReadResource(string name)
    {
        Assembly assembly = typeof(BuiltInPdfTextLayerExtensions).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(name) ??
                              throw new InvalidOperationException(
                                  $"Built-in PDF text-layer source '{name}' is missing.");
        using StreamReader reader = new(stream, Encoding.UTF8, true);
        return reader.ReadToEnd();
    }
}
