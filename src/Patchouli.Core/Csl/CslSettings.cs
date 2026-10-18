namespace Patchouli.Core.Csl;

public sealed record CslSettings(
    string? DefaultStyleId,
    string? Locale,
    DateTimeOffset UpdatedAt,
    IReadOnlyDictionary<string, string>? LanguageLocaleMappings = null)
{
    public static IReadOnlyDictionary<string, string> DefaultLanguageLocaleMappings { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ja"] = "zh-CN",
            ["ko"] = "zh-CN"
        };

    public IReadOnlyDictionary<string, string> EffectiveLanguageLocaleMappings =>
        LanguageLocaleMappings ?? DefaultLanguageLocaleMappings;
}
