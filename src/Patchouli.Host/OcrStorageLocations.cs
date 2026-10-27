namespace Patchouli.UI;

public sealed record OcrStorageLocations(
    string ModelsRoot,
    string NdlKotenModelsDirectory,
    string NdlLiteModelsDirectory,
    string RapidOcrModelsDirectory,
    string MinerUWorkDirectory,
    string NdlKotenWorkDirectory,
    string NdlLiteWorkDirectory,
    string RapidOcrWorkDirectory)
{
    public string DocLayoutYoloModelsDirectory => Path.Combine(ModelsRoot, "doclayout-yolo");
    public string PpDocLayoutV3ModelsDirectory => Path.Combine(ModelsRoot, "pp-doclayout-v3");

    public string GetLayoutModelPath(string modelId)
    {
        return modelId == Llm.OcrLayoutModelIds.DocLayoutYolo
            ? Path.Combine(DocLayoutYoloModelsDirectory, "doclayout_yolo_docstructbench_imgsz1024.onnx")
            : Path.Combine(PpDocLayoutV3ModelsDirectory, "inference.onnx");
    }

    public static OcrStorageLocations FromAppPaths(IAppPaths appPaths)
    {
        return FromResolved(appPaths.Resolve());
    }

    /// <summary>
    ///     Maps an engine definition's <c>ModelDirectoryKind</c> to a storage directory. Unknown
    ///     or absent kinds resolve to null — the caller then seeds no model path rather than
    ///     guessing a directory. Canonical kind constants live on
    ///     <see cref="OcrModelDirectoryKinds" />; bare directory aliases (e.g. <c>"ndl-koten"</c>)
    ///     are tolerated so definitions may adopt either spelling.
    /// </summary>
    public string? ModelDirectoryFor(string? modelDirectoryKind)
    {
        return modelDirectoryKind?.Trim().ToLowerInvariant() switch
        {
            null or "" => null,
            OcrModelDirectoryKinds.NdlKoten or "ndl-koten" => NdlKotenModelsDirectory,
            OcrModelDirectoryKinds.NdlLite or "ndlocr-lite" or "ndl-lite" => NdlLiteModelsDirectory,
            OcrModelDirectoryKinds.RapidOcr or "rapidocr" => RapidOcrModelsDirectory,
            OcrModelDirectoryKinds.DocLayoutYolo or "doclayout-yolo" => DocLayoutYoloModelsDirectory,
            OcrModelDirectoryKinds.PpDocLayoutV3 or "pp-doclayout-v3" => PpDocLayoutV3ModelsDirectory,
            _ => null
        };
    }

    /// <summary>
    ///     Resolves the managed directory for one declared <c>DirectoryKind</c>: known aliases go
    ///     through <see cref="ModelDirectoryFor" /> metadata only; unknown or absent kinds land
    ///     safely below <c>{ModelsRoot}/extensions/{extensionId}/{fieldKey}</c> so arbitrary
    ///     extension file groups get an isolated, deterministic location.
    /// </summary>
    public string ManagedDirectoryFor(string extensionId, string fieldKey, string? directoryKind)
    {
        return ModelDirectoryFor(directoryKind) ?? Path.Combine(ModelsRoot, "extensions",
            SanitizePathSegment(extensionId), SanitizePathSegment(fieldKey));
    }

    /// <summary>
    ///     Display-name metadata for the same known directory aliases
    ///     <see cref="ModelDirectoryFor" /> resolves. Unknown kinds return null; callers fall back
    ///     to extension/field names. Pure metadata — never consults an engine id.
    /// </summary>
    public string? ModelDirectoryDisplayNameFor(string? modelDirectoryKind)
    {
        return modelDirectoryKind?.Trim().ToLowerInvariant() switch
        {
            OcrModelDirectoryKinds.DocLayoutYolo or "doclayout-yolo" => "DocLayout-YOLO 版面模型",
            OcrModelDirectoryKinds.PpDocLayoutV3 or "pp-doclayout-v3" => "PP-DocLayoutV3 版面模型",
            OcrModelDirectoryKinds.NdlKoten or "ndl-koten" => "NDL古典籍OCR-Lite 模型文件",
            OcrModelDirectoryKinds.NdlLite or "ndlocr-lite" or "ndl-lite" => "NDLOCR-Lite 模型文件",
            OcrModelDirectoryKinds.RapidOcr or "rapidocr" => "RapidOCR 模型文件",
            _ => null
        };
    }

    /// <summary>Path segment for the extension-managed fallback directory; keeps the id readable and traversal-proof.</summary>
    internal static string SanitizePathSegment(string? segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            return "_";
        }

        System.Text.StringBuilder builder = new(segment.Length);
        foreach (char character in segment.Trim())
        {
            builder.Append(char.IsLetterOrDigit(character) || character is '.' or '-' or '_'
                ? character
                : '-');
        }

        string sanitized = builder.ToString();
        return sanitized is "." or ".." ? "_" : sanitized;
    }

    public static OcrStorageLocations FromResolved(AppStorageLocations locations)
    {
        string modelsRoot = Path.Combine(locations.DataDirectory, "models");
        return new OcrStorageLocations(
            modelsRoot,
            Path.Combine(modelsRoot, "ndl-koten"),
            Path.Combine(modelsRoot, "ndlocr-lite"),
            Path.Combine(modelsRoot, "rapidocr"),
            Path.Combine(locations.CacheDirectory, "ocr-work", "mineru"),
            Path.Combine(locations.CacheDirectory, "ocr-work", "ndl-koten"),
            Path.Combine(locations.CacheDirectory, "ocr-work", "ndlocr-lite"),
            Path.Combine(locations.CacheDirectory, "ocr-work", "rapidocr"));
    }
}

/// <summary>
///     Canonical <c>ModelDirectoryKind</c> values engine definitions may declare; each maps to a
///     host storage directory through <see cref="OcrStorageLocations.ModelDirectoryFor" />.
/// </summary>
public static class OcrModelDirectoryKinds
{
    public const string NdlKoten = "ndl-koten-models";
    public const string NdlLite = "ndlocr-lite-models";
    public const string RapidOcr = "rapidocr-models";
    public const string DocLayoutYolo = "doclayout-yolo-models";
    public const string PpDocLayoutV3 = "pp-doclayout-v3-models";
}
