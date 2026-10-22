using System.Text;
using Patchouli.Core.Results;

namespace Patchouli.Core.CitationInterop;

/// <summary>Per-request formatter knobs that arrive as CAYW query parameters.</summary>
public sealed record CitationFormatOptions(
    string? Command = null,
    bool Brackets = false,
    IReadOnlyDictionary<string, string>? LocatorLabelOverrides = null);

/// <summary>
/// Input to a string formatter. <see cref="Items"/> carries the CSL projection
/// for the pick entries (same order) for formatters that embed item data
/// (currently only <c>json</c>); pure key formatters ignore it.
/// </summary>
public sealed record CitationFormatRequest(
    CitationPick Pick,
    IReadOnlyList<CitationItemProjection> Items,
    CitationFormatOptions Options);

/// <summary>A pure-text CAYW formatter over a validated pick.</summary>
public interface ICitationStringFormatter
{
    /// <summary>The canonical format name (see <see cref="CaywCompatibility"/>).</summary>
    string FormatName { get; }

    Result<string> Format(CitationFormatRequest request);
}

/// <summary>Shared text helpers for the CAYW string formatters.</summary>
public static class CitationFormatSupport
{
    /// <summary>
    /// Resolves the display abbreviation for an entry's locator label, honoring
    /// per-request overrides (a CAYW query parameter named after the label; an
    /// empty override removes the label, per the BBT documentation).
    /// </summary>
    public static string? ResolveLabelAbbreviation(CitationPickEntry entry,
        IReadOnlyDictionary<string, string>? overrides)
    {
        string? label = string.IsNullOrWhiteSpace(entry.Label)
            ? string.IsNullOrWhiteSpace(entry.Locator) ? null : CaywCompatibility.DefaultLocatorLabel
            : entry.Label.Trim();
        if (label is null)
        {
            return null;
        }

        if (overrides is not null)
        {
            if (overrides.TryGetValue(label, out string? overridden))
            {
                return string.IsNullOrEmpty(overridden) ? null : overridden;
            }

            foreach (KeyValuePair<string, string> pair in overrides)
            {
                if (string.Equals(pair.Key, label, StringComparison.OrdinalIgnoreCase))
                {
                    return string.IsNullOrEmpty(pair.Value) ? null : pair.Value;
                }
            }
        }

        if (CaywCompatibility.DefaultLocatorLabels.TryGetValue(label, out string? abbreviation))
        {
            return abbreviation;
        }

        foreach (KeyValuePair<string, string> pair in CaywCompatibility.DefaultLocatorLabels)
        {
            if (string.Equals(pair.Key, label, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        // Unknown labels are shown verbatim rather than dropped.
        return label;
    }

    /// <summary>The locator as displayed by rich formats: "p. 33" — abbreviation
    /// plus locator, or the bare locator when the label resolves to none.</summary>
    public static string? LocatorText(CitationPickEntry entry, IReadOnlyDictionary<string, string>? overrides)
    {
        if (string.IsNullOrWhiteSpace(entry.Locator))
        {
            return null;
        }

        string? abbreviation = ResolveLabelAbbreviation(entry, overrides);
        return string.IsNullOrEmpty(abbreviation) ? entry.Locator.Trim() : $"{abbreviation} {entry.Locator.Trim()}";
    }

    public static bool HasLocatorOrNotes(CitationPickEntry entry)
    {
        return !string.IsNullOrWhiteSpace(entry.Locator) ||
               !string.IsNullOrWhiteSpace(entry.Prefix) ||
               !string.IsNullOrWhiteSpace(entry.Suffix);
    }

    /// <summary>Validates a natbib/biblatex command name from the <c>command</c>
    /// query parameter: letters only, so a request can never smuggle extra LaTeX
    /// syntax into the output through the command slot.</summary>
    public static Result<string> ValidateLatexCommand(string? command, string fallback)
    {
        string value = string.IsNullOrWhiteSpace(command) ? fallback : command.Trim();
        return value.Length > 0 && value.All(char.IsAsciiLetter)
            ? Result<string>.Success(value)
            : Result<string>.Failure(AppErrorCodes.ValidationFailed,
                $"Invalid citation command '{command}': only ASCII letters are allowed.");
    }

    /// <summary>
    /// A citation key is emitted verbatim into \cite{...}; keys containing
    /// characters that would break the command (whitespace, comma, braces,
    /// comment/control characters) are an explicit error — keys are never
    /// silently rewritten.
    /// </summary>
    public static Result ValidateLatexKey(string citationKey)
    {
        return ValidateKey(citationKey, static ch =>
            char.IsWhiteSpace(ch) || ch is ',' or '{' or '}' or '\\' or '%' or '#' or '&' or '$' or '^' or '~' or '"' ||
            char.IsControl(ch), "LaTeX");
    }

    /// <summary>Pandoc citation keys: word characters plus the punctuation pandoc
    /// allows in a citekey; anything else is an explicit error.</summary>
    public static Result ValidatePandocKey(string citationKey)
    {
        if (citationKey.Length == 0 || !(char.IsLetterOrDigit(citationKey[0]) || citationKey[0] == '_'))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed,
                $"Citation key '{citationKey}' cannot be expressed in pandoc citations.");
        }

        return ValidateKey(citationKey, static ch =>
            !(char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' or ':' or ';' or '#' or '$' or '%' or '&' or '+' or '?' or '<' or '>' or '~' or '/'),
            "pandoc");
    }

    private static Result ValidateKey(string citationKey, Func<char, bool> isInvalid, string formatName)
    {
        if (string.IsNullOrWhiteSpace(citationKey))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "A citation key is empty.");
        }

        return citationKey.Any(isInvalid)
            ? Result.Failure(AppErrorCodes.ValidationFailed,
                $"Citation key '{citationKey}' contains characters that cannot be expressed in {formatName} citations; the key was not modified.")
            : Result.Success();
    }

    /// <summary>Escapes free text (prefix/suffix/locator) for LaTeX output.</summary>
    public static string EscapeLatexText(string text)
    {
        StringBuilder builder = new(text.Length);
        foreach (char ch in text)
        {
            switch (ch)
            {
                case '\\': builder.Append("\\textbackslash{}"); break;
                case '&': builder.Append("\\&"); break;
                case '%': builder.Append("\\%"); break;
                case '$': builder.Append("\\$"); break;
                case '#': builder.Append("\\#"); break;
                case '_': builder.Append("\\_"); break;
                case '{': builder.Append("\\{"); break;
                case '}': builder.Append("\\}"); break;
                case '~': builder.Append("\\textasciitilde{}"); break;
                case '^': builder.Append("\\textasciicircum{}"); break;
                default: builder.Append(ch); break;
            }
        }

        return builder.ToString();
    }
}
