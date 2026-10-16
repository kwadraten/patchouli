namespace Patchouli.Llm;

public static class OcrLayoutModelIds
{
    public const string DocLayoutYolo = "doclayout-yolo";
    public const string PpDocLayoutV3 = "pp-doclayout-v3";

    public static string Normalize(string? modelId)
    {
        return modelId?.Trim().ToLowerInvariant() == DocLayoutYolo ? DocLayoutYolo : PpDocLayoutV3;
    }
}
