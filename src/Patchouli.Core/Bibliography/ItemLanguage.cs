namespace Patchouli.Core.Bibliography;

/// <summary>Normalizes recognized bibliographic language names while preserving useful tags and unknown values.</summary>
public static class ItemLanguage
{
    private static readonly IReadOnlyDictionary<string, string> Aliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Chinese"] = "zh",
            ["chi"] = "zh",
            ["zho"] = "zh",
            ["Chinese-Traditional"] = "zh-Hant",
            ["Chinese-Simplified"] = "zh-Hans",
            ["中文"] = "zh",
            ["zhongwen"] = "zh",
            ["English"] = "en",
            ["eng"] = "en",
            ["American"] = "en-US",
            ["British"] = "en-GB",
            ["Japanese"] = "ja",
            ["jpn"] = "ja",
            ["日本語"] = "ja",
            ["Korean"] = "ko",
            ["kor"] = "ko",
            ["한국어"] = "ko",
            ["French"] = "fr",
            ["fre"] = "fr",
            ["fra"] = "fr",
            ["German"] = "de",
            ["ger"] = "de",
            ["deu"] = "de",
            ["ngerman"] = "de-DE",
            ["Arabic"] = "ar",
            ["ara"] = "ar",
            ["Czech"] = "cs",
            ["ces"] = "cs",
            ["cze"] = "cs",
            ["Danish"] = "da",
            ["dan"] = "da",
            ["Greek"] = "el",
            ["ell"] = "el",
            ["gre"] = "el",
            ["Finnish"] = "fi",
            ["fin"] = "fi",
            ["Hebrew"] = "he",
            ["heb"] = "he",
            ["Hindi"] = "hi",
            ["hin"] = "hi",
            ["Hungarian"] = "hu",
            ["hun"] = "hu",
            ["Indonesian"] = "id",
            ["ind"] = "id",
            ["Italian"] = "it",
            ["ita"] = "it",
            ["Dutch"] = "nl",
            ["nld"] = "nl",
            ["dut"] = "nl",
            ["Norwegian"] = "no",
            ["nor"] = "no",
            ["Polish"] = "pl",
            ["pol"] = "pl",
            ["Portuguese"] = "pt",
            ["por"] = "pt",
            ["Romanian"] = "ro",
            ["ron"] = "ro",
            ["rum"] = "ro",
            ["Spanish"] = "es",
            ["spa"] = "es",
            ["Russian"] = "ru",
            ["rus"] = "ru",
            ["Swedish"] = "sv",
            ["swe"] = "sv",
            ["Thai"] = "th",
            ["tha"] = "th",
            ["Turkish"] = "tr",
            ["tur"] = "tr",
            ["Ukrainian"] = "uk",
            ["ukr"] = "uk",
            ["Vietnamese"] = "vi",
            ["vie"] = "vi"
        };

    /// <summary>Returns an ISO 639-1 code for recognized aliases and normalizes valid language tags.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        if (Aliases.TryGetValue(trimmed, out string? fullAlias))
        {
            return fullAlias;
        }

        string[] parts = trimmed.Replace('_', '-').Split('-', StringSplitOptions.None);
        if (parts.Any(static part => part.Length is < 1 or > 8 || !part.All(char.IsAsciiLetterOrDigit)))
        {
            return trimmed;
        }

        string primary;
        string[] trailing;
        if (Aliases.TryGetValue(parts[0], out string? alias))
        {
            string[] aliasParts = alias.Split('-');
            primary = aliasParts[0];
            trailing = aliasParts.Skip(1).Concat(parts.Skip(1)).Select(NormalizeSubtag).ToArray();
        }
        else if (parts[0].Length == 2 && parts[0].All(char.IsAsciiLetter))
        {
            primary = parts[0].ToLowerInvariant();
            trailing = parts.Skip(1).Select(NormalizeSubtag).ToArray();
        }
        else
        {
            return trimmed;
        }

        return trailing.Length == 0 ? primary : $"{primary}-{string.Join('-', trailing)}";
    }

    /// <summary>Returns the normalized primary language subtag used to select a local rendering rule.</summary>
    public static string? GetPrimaryLanguage(string? value)
    {
        string? normalized = Normalize(value);
        if (normalized is null)
        {
            return null;
        }

        int separator = normalized.IndexOf('-');
        return separator < 0 ? normalized : normalized[..separator];
    }

    private static string NormalizeSubtag(string subtag)
    {
        return subtag.Length switch
        {
            4 when subtag.All(char.IsAsciiLetter) => char.ToUpperInvariant(subtag[0]) + subtag[1..].ToLowerInvariant(),
            2 when subtag.All(char.IsAsciiLetter) => subtag.ToUpperInvariant(),
            3 when subtag.All(char.IsAsciiDigit) => subtag,
            _ => subtag.ToLowerInvariant()
        };
    }
}
