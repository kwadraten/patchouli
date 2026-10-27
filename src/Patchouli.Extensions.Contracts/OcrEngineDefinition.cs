namespace Patchouli.Extensions.Contracts;

/// <summary>
/// Declarative metadata for one OCR engine exported through the extension runtime. Engine ids use
/// the existing <c>OcrEngineIds</c> values; adapter kinds use <c>OcrAdapterKind</c>; supported
/// inputs use <c>OcrInputKinds</c>. Listing and settings consumers read this record without
/// creating an engine session, so every property must remain cheap metadata.
/// </summary>
public sealed record OcrEngineDefinition(
    string EngineId,
    string DisplayName,
    string AdapterKind,
    string DefaultModelId,
    IReadOnlyList<string> SupportedInputKinds,
    bool RequiresCredential = false,
    bool RequiresModelPath = false,
    bool SupportsVerticalText = false,
    bool SupportsTableDetection = false,
    string? ModelDirectoryKind = null,
    IReadOnlyDictionary<string, string>? DefaultConfig = null,
    int ConfigurationVersion = 1,
    string Notes = "",
    IReadOnlyList<OcrModelResource>? ModelResources = null,
    bool IsDevelopmentOnly = false)
{
    /// <summary>
    /// Generic descriptor used by extensions compiled before the metadata member existed; the
    /// host resolves the real definition through the engine catalog when one is registered.
    /// </summary>
    public static OcrEngineDefinition Unknown { get; } = new(
        "unknown",
        "Unknown OCR engine",
        "local_library",
        "unknown",
        ["page_image"],
        Notes: "Fallback descriptor for an extension that declares no engine metadata.");
}

/// <summary>
/// A declared model artifact for provenance pinning. <paramref name="Path"/> is the declared
/// resource path (relative to the engine's model directory when relative); <paramref name="Version"/>
/// is the public manifest version when one exists; <paramref name="Sha256"/> is its expected
/// pinned digest, not evidence that a local file was observed. Execution provenance uses hashes
/// of the actually selected local bytes; missing or unverifiable bytes remain unknown.
/// </summary>
public sealed record OcrModelResource(string Path, string? Version = null, string? Sha256 = null);
