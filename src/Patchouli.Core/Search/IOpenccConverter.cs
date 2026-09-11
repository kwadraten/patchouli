namespace Patchouli.Core.Search;

/// <summary>
/// Converts Chinese text between Simplified, Traditional, Taiwan, Hong Kong, and Japanese variants
/// using a named OpenCC configuration.
/// </summary>
public interface IOpenccConverter
{
    /// <param name="config">An OpenCC configuration name such as <c>s2t</c> or <c>t2s</c>.</param>
    /// <param name="text">The text to convert.</param>
    /// <exception cref="ArgumentException">The configuration name is not supported.</exception>
    string Convert(string config, string text);

    IReadOnlyList<string> SupportedConfigs { get; }
}

/// <summary>Canonical OpenCC configuration names used by search rewrite rules.</summary>
public static class OpenccConfigs
{
    public const string S2T = "s2t";
    public const string T2S = "t2s";
    public const string S2Tw = "s2tw";
    public const string Tw2S = "tw2s";
    public const string S2Twp = "s2twp";
    public const string Tw2Sp = "tw2sp";
    public const string T2Tw = "t2tw";
    public const string T2Hk = "t2hk";
    public const string S2Hk = "s2hk";
    public const string Hk2S = "hk2s";
    public const string T2Jp = "t2jp";
    public const string Jp2T = "jp2t";

    public static IReadOnlyList<string> All { get; } = new[]
    {
        S2T, T2S, S2Tw, Tw2S, S2Twp, Tw2Sp, T2Tw, T2Hk, S2Hk, Hk2S, T2Jp, Jp2T
    };

    private static readonly IReadOnlyDictionary<string, string> ReverseMap =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [S2T] = T2S,
            [T2S] = S2T,
            [S2Tw] = Tw2S,
            [Tw2S] = S2Tw,
            [S2Twp] = Tw2Sp,
            [Tw2Sp] = S2Twp,
            [S2Hk] = Hk2S,
            [Hk2S] = S2Hk,
            [T2Tw] = Tw2S,
            [T2Hk] = Hk2S,
            [T2Jp] = Jp2T,
            [Jp2T] = T2Jp
        };

    /// <summary>Returns the reverse configuration for a bidirectional OpenCC rule, if one is known.</summary>
    public static bool TryGetReverse(string config, out string reverse)
    {
        return ReverseMap.TryGetValue(config.Trim().ToLowerInvariant(), out reverse!);
    }
}
