namespace Patchouli.Llm;

public static class OcrPerformanceSettings
{
    public const int DefaultConcurrency = 4;
    public const int MaxConcurrency = 16;

    public static string NormalizeReasoning(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "default" => "default", "low" => "low", "medium" => "medium", "high" => "high", "max" => "max",
            _ => "none"
        };
    }
}
