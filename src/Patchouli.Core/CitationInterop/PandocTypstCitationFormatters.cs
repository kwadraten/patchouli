using System.Text;
using Patchouli.Core.Results;

namespace Patchouli.Core.CitationInterop;

/// <summary>
/// Pandoc output (CAYW format <c>pandoc</c>). Supports all four pick fields:
/// prefix and suffix are literal text inside the citation, suppress-author is
/// the <c>-@key</c> form, and the locator follows the key after a comma with
/// its label abbreviation. With <c>brackets</c> the whole pick is one bracketed
/// citation; without it, bare keys stay in-text citations and any entry that
/// carries notes is bracketed individually (pandoc has no unbracketed form
/// for notes).
/// </summary>
public sealed class PandocCitationFormatter : ICitationStringFormatter
{
    public string FormatName => CaywCompatibility.FormatPandoc;

    public Result<string> Format(CitationFormatRequest request)
    {
        foreach (CitationPickEntry entry in request.Pick.Entries)
        {
            Result valid = CitationFormatSupport.ValidatePandocKey(entry.CitationKey);
            if (valid.IsFailure)
            {
                return Result<string>.Failure(valid.ErrorCode!, valid.ErrorMessage!);
            }
        }

        IReadOnlyList<CitationPickEntry> entries = request.Pick.Entries;
        if (entries.Count == 0)
        {
            return Result<string>.Success("");
        }

        List<string> parts = new(entries.Count);
        foreach (CitationPickEntry entry in entries)
        {
            parts.Add(FormatEntry(entry, request.Options));
        }

        return Result<string>.Success(request.Options.Brackets
            ? $"[{string.Join("; ", parts.Select(StripOuterBrackets))}]"
            : string.Join(" ", parts));
    }

    private static string FormatEntry(CitationPickEntry entry, CitationFormatOptions options)
    {
        string core = entry.SuppressAuthor ? $"-@{entry.CitationKey}" : $"@{entry.CitationKey}";
        if (!CitationFormatSupport.HasLocatorOrNotes(entry))
        {
            return core;
        }

        StringBuilder builder = new();
        if (!string.IsNullOrWhiteSpace(entry.Prefix))
        {
            builder.Append(EscapeBracketText(entry.Prefix.Trim())).Append(' ');
        }

        builder.Append(core);
        string? locator = LocatorText(entry, options);
        if (locator is not null)
        {
            builder.Append(", ").Append(locator);
        }

        if (!string.IsNullOrWhiteSpace(entry.Suffix))
        {
            builder.Append(' ').Append(EscapeBracketText(entry.Suffix.Trim()));
        }

        return $"[{builder}]";
    }

    private static string StripOuterBrackets(string entry)
    {
        return entry.StartsWith('[') && entry.EndsWith(']') ? entry[1..^1] : entry;
    }

    /// <summary>
    /// Pandoc locator rendering: a plain page number/range stays bare
    /// ("p. 33"); anything else (roman numerals, ranges with text) is wrapped
    /// in braces, which is pandoc's escape for non-numeric locators.
    /// </summary>
    private static string? LocatorText(CitationPickEntry entry, CitationFormatOptions options)
    {
        if (string.IsNullOrWhiteSpace(entry.Locator))
        {
            return null;
        }

        string? abbreviation = CitationFormatSupport.ResolveLabelAbbreviation(entry,
            options.LocatorLabelOverrides);
        string locator = entry.Locator.Trim();
        string text = string.IsNullOrEmpty(abbreviation) ? locator : $"{abbreviation} {locator}";
        return IsPlainNumericLocator(locator) ? text : $"{{{text}}}";
    }

    private static bool IsPlainNumericLocator(string locator)
    {
        return locator.All(static ch => char.IsAsciiDigit(ch) || ch is '-' or '–' or ',' or ' ');
    }

    private static string EscapeBracketText(string text)
    {
        return text.Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal);
    }
}

/// <summary>
/// Typst output (CAYW format <c>typst</c>). Plain picks use the <c>@key</c>
/// shorthand; entries with a locator or suppress-author use the functional
/// form <c>#cite(&lt;key&gt;, …)</c> with the locator text as the supplement
/// and <c>form: "year"</c> for suppress-author. Prefix/suffix have no Typst
/// citation slot, so they are folded into the supplement text rather than
/// dropped (the ADR records this as an assumed mapping: the BBT typst
/// formatter's source was not publicly verifiable).
/// </summary>
public sealed class TypstCitationFormatter : ICitationStringFormatter
{
    public string FormatName => CaywCompatibility.FormatTypst;

    public Result<string> Format(CitationFormatRequest request)
    {
        IReadOnlyList<CitationPickEntry> entries = request.Pick.Entries;
        if (entries.Count == 0)
        {
            return Result<string>.Success("");
        }

        List<string> parts = new(entries.Count);
        foreach (CitationPickEntry entry in entries)
        {
            Result<string> formatted = FormatEntry(entry, request.Options);
            if (formatted.IsFailure)
            {
                return formatted;
            }

            parts.Add(formatted.Value);
        }

        return Result<string>.Success(string.Join(" ", parts));
    }

    private static Result<string> FormatEntry(CitationPickEntry entry, CitationFormatOptions options)
    {
        bool needsFunctionalForm = entry.SuppressAuthor || CitationFormatSupport.HasLocatorOrNotes(entry);
        if (!needsFunctionalForm)
        {
            return IsTypstLabelKey(entry.CitationKey)
                ? Result<string>.Success($"@{entry.CitationKey}")
                : FunctionalForm(entry, options);
        }

        return FunctionalForm(entry, options);
    }

    private static Result<string> FunctionalForm(CitationPickEntry entry, CitationFormatOptions options)
    {
        // The functional form embeds the key as a label (<key>), so it must
        // satisfy exactly the same label rules as the @key shorthand: a key
        // with e.g. '/' or whitespace would produce Typst that cannot parse.
        // Fail explicitly instead of emitting an invalid label or rewriting
        // the key.
        if (!IsTypstLabelKey(entry.CitationKey))
        {
            return Result<string>.Failure(AppErrorCodes.ValidationFailed,
                $"Citation key '{entry.CitationKey}' cannot be expressed as a Typst label; the key was not modified.");
        }

        List<string> arguments = new();
        if (entry.SuppressAuthor)
        {
            arguments.Add("form: \"year\"");
        }

        List<string> supplementParts = new();
        if (!string.IsNullOrWhiteSpace(entry.Prefix))
        {
            supplementParts.Add(entry.Prefix.Trim());
        }

        string? locator = CitationFormatSupport.LocatorText(entry, options.LocatorLabelOverrides);
        if (locator is not null)
        {
            supplementParts.Add(locator);
        }

        if (!string.IsNullOrWhiteSpace(entry.Suffix))
        {
            supplementParts.Add(entry.Suffix.Trim());
        }

        if (supplementParts.Count > 0)
        {
            arguments.Add($"supplement: [{EscapeTypstContent(string.Join(", ", supplementParts))}]");
        }

        string args = arguments.Count == 0 ? "" : $", {string.Join(", ", arguments)}";
        return Result<string>.Success($"#cite(<{entry.CitationKey}>{args})");
    }

    /// <summary>Characters allowed in a bare Typst label reference (@key).</summary>
    private static bool IsTypstLabelKey(string key)
    {
        if (key.Length == 0 || !(char.IsLetterOrDigit(key[0]) || key[0] == '_'))
        {
            return false;
        }

        // Typst's label character set is letters, numbers and _ - : . only
        // (no '+': a key like smith+2020 is not a valid Typst label).
        return key.All(static ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' or ':');
    }

    private static string EscapeTypstContent(string text)
    {
        StringBuilder builder = new(text.Length);
        foreach (char ch in text)
        {
            if (ch is '\\' or '#' or '@' or '$' or '[' or ']' or '*' or '_' or '`')
            {
                builder.Append('\\');
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }
}
