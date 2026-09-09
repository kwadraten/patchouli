namespace Patchouli.UI.ViewModels;

/// <summary>The three display segments of a matched snippet: text before the hit, the bold hit
/// itself, and the text after it. Ellipsis markers are included in the segments when truncated.</summary>
public sealed record SearchSnippetParts(string Prefix, string Hit, string Suffix);

/// <summary>
/// Formats matched snippet text for the hierarchical full-text grid: collapses all whitespace
/// runs (including newlines) into single spaces, locates the first occurrence of a query term,
/// and computes a character window around the hit so the bold term lands near the visual center
/// of the row. Display width estimates CJK characters as double width.
/// </summary>
public static class SearchSnippetFormatter
{
    public const int DefaultBudget = 96;

    public static SearchSnippetParts Format(string text, string query, int budget = DefaultBudget)
    {
        string normalized = Normalize(text);
        if (normalized.Length == 0)
        {
            return new SearchSnippetParts("", "", "");
        }

        int hitStart = FindHit(normalized, query);
        if (hitStart < 0)
        {
            return TruncateHead(normalized, budget);
        }

        int hitLength = HitLength(normalized, query, hitStart);
        return TruncateAround(normalized, hitStart, hitLength, budget);
    }

    public static int DisplayWidth(string text)
    {
        int width = 0;
        foreach (char c in text)
        {
            width += IsCjk(c) ? 2 : 1;
        }

        return width;
    }

    private static string Normalize(string text)
    {
        char[] buffer = new char[text.Length];
        int length = 0;
        bool pendingSpace = false;
        foreach (char raw in text.Trim())
        {
            if (char.IsWhiteSpace(raw))
            {
                pendingSpace = length > 0;
                continue;
            }

            if (pendingSpace)
            {
                buffer[length++] = ' ';
                pendingSpace = false;
            }

            buffer[length++] = raw;
        }

        return new string(buffer, 0, length);
    }

    private static int FindHit(string text, string query)
    {
        foreach (string term in CandidateTerms(query))
        {
            int index = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static int HitLength(string text, string query, int hitStart)
    {
        foreach (string term in CandidateTerms(query))
        {
            if (hitStart + term.Length <= text.Length &&
                string.Compare(text, hitStart, term, 0, term.Length, StringComparison.OrdinalIgnoreCase) == 0)
            {
                return term.Length;
            }
        }

        return 0;
    }

    private static IEnumerable<string> CandidateTerms(string query)
    {
        string trimmed = query.Trim();
        if (trimmed.Length == 0)
        {
            yield break;
        }

        yield return trimmed;
        foreach (string token in trimmed.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries |
                                                                        StringSplitOptions.TrimEntries))
        {
            if (!string.Equals(token, trimmed, StringComparison.Ordinal))
            {
                yield return token;
            }
        }
    }

    private static SearchSnippetParts TruncateHead(string text, int budget)
    {
        if (budget < 8 || DisplayWidth(text) <= budget)
        {
            return new SearchSnippetParts(text, "", "");
        }

        (_, int end) = Window(text, 0, budget);
        return new SearchSnippetParts(text[..end] + "…", "", "");
    }

    private static SearchSnippetParts TruncateAround(string text, int hitStart, int hitLength, int budget)
    {
        if (budget < 8 || DisplayWidth(text) <= budget)
        {
            return new SearchSnippetParts(text[..hitStart], text.Substring(hitStart, hitLength),
                text[(hitStart + hitLength)..]);
        }

        int centeredStart = Math.Clamp(hitStart + Math.Max(hitLength, 1) / 2 - budget / 2, 0, text.Length - 1);
        (int start, int end) = Window(text, centeredStart, budget);
        if (hitStart < start || hitStart + hitLength > end)
        {
            start = Math.Clamp(hitStart - budget / 4, 0, text.Length - 1);
            (start, end) = Window(text, start, budget);
        }

        int hitEnd = Math.Min(hitStart + hitLength, end);
        string prefix = (start > 0 ? "…" : "") + text[start..hitStart];
        string suffix = text[hitEnd..end] + (end < text.Length ? "…" : "");
        return new SearchSnippetParts(prefix, text[hitStart..hitEnd], suffix);
    }

    private static (int Start, int End) Window(string text, int start, int budget)
    {
        int end = start;
        int width = 0;
        while (end < text.Length)
        {
            int charWidth = IsCjk(text[end]) ? 2 : 1;
            if (width + charWidth > budget)
            {
                break;
            }

            width += charWidth;
            end++;
        }

        return (start, end);
    }

    private static bool IsCjk(char c)
    {
        return (c >= '\u3400' && c <= '\u9fff')
               || (c >= '\uf900' && c <= '\ufaff')
               || (c >= '\u3040' && c <= '\u30ff')
               || (c >= '\uac00' && c <= '\ud7af');
    }
}
