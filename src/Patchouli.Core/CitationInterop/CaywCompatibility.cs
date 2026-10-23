namespace Patchouli.Core.CitationInterop;

/// <summary>
/// The fixed first-version Better BibTeX CAYW / TexPile compatibility surface
/// (issue #14). Values verified against the public BBT documentation
/// (https://retorque.re/zotero-better-bibtex/citing/cayw/, read 2026-10-22)
/// and the documented BBT JSON-RPC method shapes; anything not verifiable from
/// public sources is marked as assumed in the issue-14 ADR instead of being
/// widened silently.
/// </summary>
public static class CaywCompatibility
{
    /// <summary>BBT's loopback port; also the port TexPile hard-codes.</summary>
    public const int DefaultPort = 23119;

    public const string CaywPath = "/better-bibtex/cayw";
    public const string JsonRpcPath = "/better-bibtex/json-rpc";

    /// <summary>Body returned by a successful probe. BBT returns exactly this text.</summary>
    public const string ProbeReadyBody = "ready";

    /// <summary>
    /// BBT flag-parameter truthiness: a flag is set when the parameter is present
    /// with any non-empty value. In particular the string "false" is a non-empty
    /// value and therefore means the flag IS set; CAYW flags must never be parsed
    /// with generic boolean parsing.
    /// </summary>
    public static bool IsFlagSet(string? value)
    {
        return !string.IsNullOrEmpty(value);
    }

    /// <summary>CAYW format used when the request carries no <c>format</c> parameter.</summary>
    public const string DefaultFormat = "latex";

    public const string FormatNatbib = "natbib";
    public const string FormatBiblatex = "biblatex";
    public const string FormatPandoc = "pandoc";
    public const string FormatTypst = "typst";
    public const string FormatJson = "json";
    public const string FormatFormattedCitation = "formatted-citation";
    public const string FormatFormattedBibliography = "formatted-bibliography";

    /// <summary>Every format name/alias the first version resolves. Aliases follow the
    /// BBT documentation: latex/cite are natbib with command <c>cite</c>; citep/citet
    /// are natbib with those commands.</summary>
    public static readonly IReadOnlyDictionary<string, string> FormatAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["natbib"] = FormatNatbib,
            ["latex"] = FormatNatbib,
            ["cite"] = FormatNatbib,
            ["citep"] = FormatNatbib,
            ["citet"] = FormatNatbib,
            ["biblatex"] = FormatBiblatex,
            ["pandoc"] = FormatPandoc,
            ["typst"] = FormatTypst,
            ["json"] = FormatJson,
            ["formatted-citation"] = FormatFormattedCitation,
            ["formatted-bibliography"] = FormatFormattedBibliography
        };

    /// <summary>The natbib command implied by a format alias; null when the alias
    /// carries no command (the format default then applies).</summary>
    public static string? AliasCommand(string format)
    {
        return format.Trim().ToLowerInvariant() switch
        {
            "latex" or "cite" => "cite",
            "citep" => "citep",
            "citet" => "citet",
            _ => null
        };
    }

    /// <summary>Format names BBT knows but the first Patchouli version deliberately
    /// does not implement. They must produce an explicit unsupported error, never a
    /// silent fallback to another format.</summary>
    public static readonly IReadOnlySet<string> KnownUnsupportedFormats =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mmd", "jupyter", "asciidoctor-bibtex", "scannable-cite", "eta", "translate"
        };

    /// <summary>JSON-RPC translator names (verbatim BBT translator names).</summary>
    public const string TranslatorBetterBiblatex = "Better BibLaTeX";

    public const string TranslatorBetterBibtex = "Better BibTeX";

    /// <summary>Server-side cap for TexPile <c>item.search</c> results (issue #14 §7).</summary>
    public const int MaxSearchResults = 100;

    /// <summary>Server-side cap for one <c>item.export</c> key batch.</summary>
    public const int MaxExportKeys = 200;

    /// <summary>Hard cap for the JSON-RPC request body and the CAYW query string.</summary>
    public const int MaxRequestBodyBytes = 64 * 1024;

    public const int MaxQueryStringLength = 2048;
    public const int MaxSearchQueryLength = 512;

    /// <summary>
    /// Default deadline for one interactive picker request. Deliberately far above
    /// search/API timeouts: a human is reading search results. Expiry cancels the
    /// picker and is reported to the client as an empty (cancelled) pick.
    /// </summary>
    public static readonly TimeSpan DefaultInteractiveDeadline = TimeSpan.FromMinutes(10);

    /// <summary>
    /// BBT's default locator-label abbreviations (CAYW documentation table).
    /// A request may override any of them by passing the label name as a query
    /// parameter; an empty override removes the label.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> DefaultLocatorLabels =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["article"] = "art.",
            ["chapter"] = "ch.",
            ["subchapter"] = "subch.",
            ["column"] = "col.",
            ["figure"] = "fig.",
            ["line"] = "l.",
            ["note"] = "n.",
            ["issue"] = "no.",
            ["opus"] = "op.",
            ["page"] = "p.",
            ["paragraph"] = "para.",
            ["subparagraph"] = "subpara.",
            ["part"] = "pt.",
            ["rule"] = "r.",
            ["section"] = "sec.",
            ["subsection"] = "subsec.",
            ["Section"] = "Sec.",
            ["sub verbo"] = "sv.",
            ["schedule"] = "sch.",
            ["title"] = "tit.",
            ["verse"] = "vrs.",
            ["volume"] = "vol."
        };

    /// <summary>The label assumed when a pick entry carries a locator but no label.</summary>
    public const string DefaultLocatorLabel = "page";
}
