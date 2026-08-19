namespace Patchouli.UI;

public sealed record OcrStorageLocations(
    string ModelsRoot,
    string NdlKotenModelsDirectory,
    string MinerUWorkDirectory,
    string NdlKotenWorkDirectory)
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
            Path.Combine(locations.CacheDirectory, "ocr-work", "mineru"),
            Path.Combine(locations.CacheDirectory, "ocr-work", "ndl-koten"));
    }
}
