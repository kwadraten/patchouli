using System.Globalization;
using System.Text;

namespace Patchouli.Infrastructure.Search;

internal static class SearchTextAnalyzer
{
    public static string BuildIndexText(string text)
    {
        return string.Join(' ', Analyze(text));
    }

    public static IReadOnlyList<string> BuildQueryTokens(string query)
    {
        return Analyze(query);
    }

    private static IReadOnlyList<string> Analyze(string text)
    {
        List<string> tokens = new();
        StringBuilder word = new();
        List<char> cjkRun = new();

        foreach (char c in text.Normalize(NormalizationForm.FormKC))
        {
            if (IsCjk(c))
            {
                FlushWord(tokens, word);
                cjkRun.Add(c);
                continue;
            }

            FlushCjk(tokens, cjkRun);
            if (IsWordChar(c))
            {
                word.Append(char.ToLowerInvariant(c));
            }
            else
            {
                FlushWord(tokens, word);
            }
        }

        FlushWord(tokens, word);
        FlushCjk(tokens, cjkRun);
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

    private static void FlushCjk(List<string> tokens, List<char> run)
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
                tokens.Add(new string(run.Skip(i).Take(n).ToArray()));
            }
        }

        run.Clear();
    }

    private static bool IsWordChar(char c)
    {
        return char.IsLetterOrDigit(c) && !IsCjk(c) &&
               CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark;
    }

    private static bool IsCjk(char c)
    {
        return (c >= '\u3400' && c <= '\u9fff')
               || (c >= '\uf900' && c <= '\ufaff')
               || (c >= '\u3040' && c <= '\u30ff')
               || (c >= '\uac00' && c <= '\ud7af');
    }
}
