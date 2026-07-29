namespace Patchouli.Core.Bibliography.Biblatex;

/// <summary>
/// BibLaTeX entry-type tables transplanted from Citation.js
/// <c>@citation-js/plugin-bibtex</c> <c>src/mapping/biblatexTypes.json</c>
/// (MIT License). Locked upstream revision is recorded in
/// <c>.agent/adr/0021-biblatex-import-export-via-typst-biblatex-helper.md</c>.
/// </summary>
public static class BiblatexEntryTypeMap
{
    public static readonly IReadOnlyDictionary<string, string> BiblatexToCsl =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["article"] = "article-journal",
            ["book"] = "book",
            ["mvbook"] = "book",
            ["inbook"] = "chapter",
            ["bookinbook"] = "book",
            ["booklet"] = "book",
            ["collection"] = "book",
            ["mvcollection"] = "book",
            ["incollection"] = "chapter",
            ["dataset"] = "dataset",
            ["manual"] = "report",
            ["misc"] = "document",
            ["online"] = "webpage",
            ["patent"] = "patent",
            ["periodical"] = "periodical",
            ["proceedings"] = "book",
            ["mvproceedings"] = "book",
            ["inproceedings"] = "paper-conference",
            ["reference"] = "book",
            ["mvreference"] = "book",
            ["inreference"] = "entry",
            ["report"] = "report",
            ["software"] = "software",
            ["thesis"] = "thesis",
            ["unpublished"] = "manuscript",
            ["artwork"] = "graphic",
            ["audio"] = "song",
            ["image"] = "figure",
            ["jurisdiction"] = "legal_case",
            ["legislation"] = "legislation",
            ["legal"] = "treaty",
            ["letter"] = "personal_communication",
            ["movie"] = "motion_picture",
            ["music"] = "musical_score",
            ["performance"] = "performance",
            ["review"] = "review",
            ["standard"] = "standard",
            ["video"] = "motion_picture",
            ["conference"] = "paper-conference",
            ["electronic"] = "webpage",
            ["mastersthesis"] = "thesis",
            ["phdthesis"] = "thesis",
            ["techreport"] = "report",
            ["www"] = "webpage"
        };

    public static readonly IReadOnlyDictionary<string, string> CslToBiblatex =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["article"] = "article",
            ["article-journal"] = "article",
            ["article-magazine"] = "article",
            ["article-newspaper"] = "article",
            ["bill"] = "legislation",
            ["book"] = "book",
            ["broadcast"] = "audio",
            ["chapter"] = "inbook",
            ["classic"] = "unpublished",
            ["collection"] = "misc",
            ["dataset"] = "dataset",
            ["document"] = "misc",
            ["entry"] = "inreference",
            ["entry-dictionary"] = "inreference",
            ["entry-encyclopedia"] = "inreference",
            ["event"] = "misc",
            ["figure"] = "artwork",
            ["graphic"] = "artwork",
            ["hearing"] = "legal",
            ["interview"] = "audio",
            ["legal_case"] = "jurisdiction",
            ["legislation"] = "legislation",
            ["manuscript"] = "unpublished",
            ["motion_picture"] = "movie",
            ["musical_score"] = "music",
            ["paper-conference"] = "inproceedings",
            ["patent"] = "patent",
            ["performance"] = "performance",
            ["periodical"] = "periodical",
            ["personal_communication"] = "letter",
            ["post"] = "online",
            ["post-weblog"] = "online",
            ["regulation"] = "legal",
            ["report"] = "report",
            ["review"] = "review",
            ["review-book"] = "review",
            ["software"] = "software",
            ["song"] = "music",
            ["speech"] = "audio",
            ["standard"] = "standard",
            ["thesis"] = "thesis",
            ["treaty"] = "legal",
            ["webpage"] = "online"
        };

    public static readonly IReadOnlySet<string> SupportedPatchouliTypes =
        new HashSet<string>(CslItemTypeDisplayNames.Names.Keys, StringComparer.Ordinal);

    public static string ResolvePatchouliItemType(string biblatexEntryType, out string? retainedOriginalType)
    {
        string normalized = biblatexEntryType.Trim();
        if (!BiblatexToCsl.TryGetValue(normalized, out string? cslType))
        {
            retainedOriginalType = normalized;
            return "general";
        }

        if (SupportedPatchouliTypes.Contains(cslType))
        {
            retainedOriginalType = null;
            return cslType;
        }

        retainedOriginalType = normalized;
        return "general";
    }

    public static bool TryMapExportEntryType(string patchouliItemType, out string biblatexEntryType)
    {
        if (string.Equals(patchouliItemType, "general", StringComparison.Ordinal))
        {
            biblatexEntryType = string.Empty;
            return false;
        }

        return CslToBiblatex.TryGetValue(patchouliItemType, out biblatexEntryType!);
    }
}
