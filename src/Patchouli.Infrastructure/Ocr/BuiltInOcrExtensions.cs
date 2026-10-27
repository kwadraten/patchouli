using System.Reflection;
using System.Text;
using Patchouli.Extensions.Contracts;
using Patchouli.Ocr.Engines;

namespace Patchouli.Infrastructure.Ocr;

/// <summary>
/// FSX packages and engine metadata for the built-in OCR engines. Each package ships its
/// attributed-settings <c>main.fsx</c>, the native packages also ship the shared
/// <c>Support.fsx</c>, and every package carries the full support binary closure
/// (Patchouli.Ocr.Engines plus its non-framework managed dependencies and the current RID's
/// native libraries) collected from the real deployment — so a compiled artifact is
/// self-contained for the collectible extension load context. Model weights stay in the
/// configured model directories and are never part of a package.
/// </summary>
public static class BuiltInOcrExtensions
{
    private const string ResourcePrefix = "Patchouli.Infrastructure.Ocr.Extensions.";

    /// <summary>The package ids, in stable registration order.</summary>
    public static readonly string[] PackageIds =
    [
        "ocr.rapidocr",
        "ocr.ndlocr-lite",
        "ocr.ndlkotenocr-lite",
        "ocr.multimodal"
    ];

    // ocr.multimodal's main.fsx is owned by the vision worker; this registry only embeds and
    // ships it. The pdf-text.* packages are registered separately by BuiltInPdfTextLayerExtensions.
    // The shared helper script is loaded only by the native ONNX packages; the vision-owned
    // multimodal package is self-contained and ships its own sources alone.
    private static readonly HashSet<string> NativePackageIds =
        new(PackageIds.Take(3), StringComparer.Ordinal);

    // Lazy so package construction can never observe the binary map before it exists — field
    // initializer order would otherwise depend on declaration order.
    private static readonly Lazy<IReadOnlyDictionary<string, ReadOnlyMemory<byte>>> SupportBinaries =
        new(NativeOcrSupportFiles.CollectBinaries);

    private static readonly IReadOnlyList<ExtensionPackageSnapshot> BuiltInPackages =
        Array.AsReadOnly(PackageIds.Select(CreatePackage).ToArray());

    private static readonly IReadOnlyDictionary<string, OcrEngineDefinition> EngineDefinitions =
        new Dictionary<string, OcrEngineDefinition>(StringComparer.Ordinal)
        {
            ["ocr.rapidocr"] = NativeOcrSession.GetDefinition(NativeOcrSession.RapidOcrEngineId),
            ["ocr.ndlocr-lite"] = NativeOcrSession.GetDefinition(NativeOcrSession.NdlOcrLiteEngineId),
            ["ocr.ndlkotenocr-lite"] =
                NativeOcrSession.GetDefinition(NativeOcrSession.NdlKotenLiteEngineId),
            ["ocr.multimodal"] = MultimodalOcrSession.GetDefinition()
        };

    /// <summary>The immutable package snapshots; the same list feeds schema generation and install.</summary>
    public static IReadOnlyList<ExtensionPackageSnapshot> Packages => BuiltInPackages;

    /// <summary>
    /// Declarative engine metadata keyed by package id; each value's <c>EngineId</c> is the
    /// product engine id (<c>Patchouli.Ocr.OcrEngineIds</c>) the package implements. Capability
    /// listing and preset routing consume this without ever constructing a model session.
    /// </summary>
    public static IReadOnlyDictionary<string, OcrEngineDefinition> Definitions => EngineDefinitions;

    private static ExtensionPackageSnapshot CreatePackage(string id)
    {
        Dictionary<string, string> sources = new(StringComparer.Ordinal)
        {
            ["main.fsx"] = ReadResource(ResourcePrefix + id + ".main.fsx")
        };
        if (NativePackageIds.Contains(id))
        {
            sources["Support.fsx"] = ReadResource(ResourcePrefix + "Support.fsx");
        }

        return new ExtensionPackageSnapshot(id, sources, SupportBinaries.Value);
    }

    private static string ReadResource(string name)
    {
        Assembly assembly = typeof(BuiltInOcrExtensions).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(name) ??
                              throw new InvalidOperationException($"Built-in OCR source '{name}' is missing.");
        using StreamReader reader = new(stream, Encoding.UTF8, true);
        return reader.ReadToEnd();
    }
}
