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
    public static OcrStorageLocations FromAppPaths(IAppPaths appPaths)
    {
        return FromResolved(appPaths.Resolve());
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
