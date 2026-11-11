using System.Globalization;
using System.Text;

namespace Patchouli.Infrastructure.Search;

internal static class SearchTextAnalyzer
{
    internal const int CurrentCacheVersion = 2;

    public static string BuildIndexText(string text)
    {
        return string.Join(' ', Analyze(text, true));
    }

    public static IReadOnlyList<string> BuildQueryTokens(string query)
    {
        return Analyze(query, true);
    }

    public static IReadOnlyList<string> BuildQueryTokens(string query, int cacheVersion)
    {
        bool includeSymbols = cacheVersion >= CurrentCacheVersion;
        if (!includeSymbols && query.Any(char.IsSurrogate))
        {
            return Array.Empty<string>();
        }

        return Analyze(query, includeSymbols);
    }

    private static IReadOnlyList<string> Analyze(string text, bool includeSymbolTokens)
    {
        List<string> tokens = new();
        StringBuilder word = new();
        List<string> cjkRun = new();
        string? normalizedInput = StorageText.NormalizeInvalidCodePoints(text);
        if (string.IsNullOrEmpty(normalizedInput))
        {
            return Array.Empty<string>();
        }

        string normalized = normalizedInput.Normalize(NormalizationForm.FormKC);

        foreach (Rune rune in normalized.EnumerateRunes())
        {
            int cp = rune.Value;
            if (IsCjk(cp))
            {
                FlushWord(tokens, word);
                cjkRun.Add(rune.ToString());
                continue;
            }

            FlushCjk(tokens, cjkRun);
            if (IsWordChar(rune, cp))
            {
                word.Append(Rune.ToLowerInvariant(rune).ToString());
            }
            else
            {
                FlushWord(tokens, word);
            }
        }

        FlushWord(tokens, word);
        FlushCjk(tokens, cjkRun);
        if (includeSymbolTokens)
        {
            // Literal candidates must reflect canonical input before normalization composes marks
            // or removes formatting characters. Exact matching still checks the original text.
            foreach (Rune rune in normalizedInput.EnumerateRunes())
            {
                if (!Rune.IsLetterOrDigit(rune) && !Rune.IsWhiteSpace(rune))
                {
                    tokens.Add($"sym{rune.Value:x4}");
                }
            }
        }

        return tokens.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static void FlushWord(List<string> tokens, StringBuilder word)
    {
        if (word.Length == 0)
        {
            return;
        }

        tokens.Add(word.ToString());
        word.Clear();
    }

    private static void FlushCjk(List<string> tokens, List<string> run)
    {
        if (run.Count == 0)
        {
            return;
        }

        for (int n = 1; n <= 3; n++)
        {
            if (run.Count < n)
            {
                break;
            }

            for (int i = 0; i <= run.Count - n; i++)
            {
                tokens.Add(string.Concat(run.Skip(i).Take(n)));
            }
        }

        run.Clear();
    }

    private static bool IsWordChar(Rune rune, int cp)
    {
        return Rune.IsLetterOrDigit(rune) && !IsCjk(cp) &&
               Rune.GetUnicodeCategory(rune) != UnicodeCategory.NonSpacingMark;
    }

    private static bool IsCjk(int cp)
    {
        return (cp >= 0x3400 && cp <= 0x9fff)
               || (cp >= 0xf900 && cp <= 0xfaff)
               || (cp >= 0x3040 && cp <= 0x30ff)
               || (cp >= 0xac00 && cp <= 0xd7af)
               || (cp >= 0x20000 && cp <= 0x2fa1f);
    }
}
