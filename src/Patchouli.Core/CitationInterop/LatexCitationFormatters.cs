using Patchouli.Core.Results;

namespace Patchouli.Core.CitationInterop;

/// <summary>
/// natbib output (CAYW formats <c>natbib</c>, <c>latex</c>, <c>cite</c>,
/// <c>citep</c>, <c>citet</c>), following the behavior the BBT documentation
/// describes: when no entry carries locator/prefix/suffix and suppress-author
/// is uniform, one combined command is emitted (the year variant when every
/// entry suppresses the author); otherwise each entry is written separately
/// because natbib has no combined form for mixed notes.
/// </summary>
public sealed class NatbibCitationFormatter : ICitationStringFormatter
{
    public string FormatName => CaywCompatibility.FormatNatbib;

    public Result<string> Format(CitationFormatRequest request)
    {
        Result<string> command = CitationFormatSupport.ValidateLatexCommand(request.Options.Command, "cite");
        if (command.IsFailure)
        {
            return Result<string>.Failure(command.ErrorCode!, command.ErrorMessage!);
        }

        foreach (CitationPickEntry entry in request.Pick.Entries)
        {
            Result valid = CitationFormatSupport.ValidateLatexKey(entry.CitationKey);
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

        bool anyNotes = entries.Any(CitationFormatSupport.HasLocatorOrNotes);
        bool allSuppressed = entries.All(static entry => entry.SuppressAuthor);
        bool anySuppressed = entries.Any(static entry => entry.SuppressAuthor);
        string keys = string.Join(",", entries.Select(static entry => entry.CitationKey));

        if (!anyNotes && !anySuppressed)
        {
            return Result<string>.Success($"\\{command.Value}{{{keys}}}");
        }

        if (!anyNotes && allSuppressed)
        {
            return Result<string>.Success($"\\{YearCommand(command.Value)}{{{keys}}}");
        }

        List<string> parts = new(entries.Count);
        foreach (CitationPickEntry entry in entries)
        {
            string entryCommand = entry.SuppressAuthor ? YearCommand(command.Value) : command.Value;
            parts.Add($"\\{entryCommand}{OptionalArguments(entry, request.Options)}{{{entry.CitationKey}}}");
        }

        return Result<string>.Success(string.Concat(parts));
    }

    /// <summary>The natbib year-only variant of a command, used for
    /// suppress-author. Only \citeyear and \citeyearpar exist in natbib; other
    /// commands fall back to \citeyear (documented in the issue-14 ADR).</summary>
    internal static string YearCommand(string command)
    {
        return command switch
        {
            "cite" => "citeyear",
            "citep" => "citeyearpar",
            "citet" => "citeyear",
            _ => "citeyear"
        };
    }

    /// <summary>natbib optional arguments: [pre][post]. A single bracket is the
    /// post-note (matching BBT's \cite[p. 1]{key} example); a prefix-only entry
    /// needs an explicit empty post-note.</summary>
    internal static string OptionalArguments(CitationPickEntry entry, CitationFormatOptions options)
    {
        string? pre = string.IsNullOrWhiteSpace(entry.Prefix)
            ? null
            : CitationFormatSupport.EscapeLatexText(entry.Prefix.Trim());
        string? locator = CitationFormatSupport.LocatorText(entry, options.LocatorLabelOverrides);
        string? post = JoinNote(
            locator is null ? null : CitationFormatSupport.EscapeLatexText(locator),
            string.IsNullOrWhiteSpace(entry.Suffix)
                ? null
                : CitationFormatSupport.EscapeLatexText(entry.Suffix.Trim()));
        if (pre is null && post is null)
        {
            return "";
        }

        if (pre is null)
        {
            return $"[{post}]";
        }

        return post is null ? $"[{pre}][]" : $"[{pre}][{post}]";
    }

    internal static string? JoinNote(string? locator, string? suffix)
    {
        if (locator is null)
        {
            return suffix;
        }

        return suffix is null ? locator : $"{locator}, {suffix}";
    }
}

/// <summary>
/// BibLaTeX output (CAYW format <c>biblatex</c>, default command
/// <c>autocite</c>), following the BBT documentation: suppress-author is
/// ignored except for a single item with \cite, \autocite or \parencite,
/// which becomes the starred variant; multiple items with notes use the
/// <c>s</c>-suffixed multicite variant.
/// </summary>
public sealed class BiblatexCitationFormatter : ICitationStringFormatter
{
    public string FormatName => CaywCompatibility.FormatBiblatex;

    public Result<string> Format(CitationFormatRequest request)
    {
        Result<string> command = CitationFormatSupport.ValidateLatexCommand(request.Options.Command, "autocite");
        if (command.IsFailure)
        {
            return Result<string>.Failure(command.ErrorCode!, command.ErrorMessage!);
        }

        foreach (CitationPickEntry entry in request.Pick.Entries)
        {
            Result valid = CitationFormatSupport.ValidateLatexKey(entry.CitationKey);
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

        if (entries.Count == 1)
        {
            CitationPickEntry entry = entries[0];
            bool starred = entry.SuppressAuthor &&
                           command.Value is "cite" or "autocite" or "parencite";
            string name = starred ? command.Value + "*" : command.Value;
            return Result<string>.Success(
                $"\\{name}{NatbibCitationFormatter.OptionalArguments(entry, request.Options)}{{{entry.CitationKey}}}");
        }

        bool anyNotes = entries.Any(CitationFormatSupport.HasLocatorOrNotes);
        if (!anyNotes)
        {
            string keys = string.Join(",", entries.Select(static entry => entry.CitationKey));
            return Result<string>.Success($"\\{command.Value}{{{keys}}}");
        }

        List<string> parts = new(entries.Count);
        foreach (CitationPickEntry entry in entries)
        {
            parts.Add($"{NatbibCitationFormatter.OptionalArguments(entry, request.Options)}{{{entry.CitationKey}}}");
        }

        return Result<string>.Success($"\\{command.Value}s{string.Concat(parts)}");
    }
}
