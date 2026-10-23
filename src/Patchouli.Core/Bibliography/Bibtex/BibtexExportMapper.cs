using System.Text;
using System.Text.Json;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Results;

namespace Patchouli.Core.Bibliography.Bibtex;

/// <summary>
/// The outcome of a classic BibTeX mapping: writer-ready entries plus the
/// information-loss warnings the conversion produced (dropped fields and
/// conservative type fallbacks), so callers can surface them instead of
/// silently pretending the conversion was lossless.
/// </summary>
public sealed record BibtexExportResult(
    IReadOnlyList<BiblatexWriteEntryDto> Entries,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Maps Patchouli items to traditional (classic) BibTeX entries — a real
/// classic mapping, not a BibLaTeX alias (issue #14 §7). The output uses only
/// classic entry types (article, book, booklet, inbook, incollection,
/// inproceedings, manual, mastersthesis, phdthesis, proceedings, techreport,
/// unpublished, misc) and classic field names (journal, year, month, address,
/// school, institution, …). BibLaTeX-only structures (journaltitle, date,
/// location, pubstate, …) are never emitted. Types classic BibTeX cannot
/// express fall back to <c>misc</c> and every dropped or degraded field is
/// recorded in <see cref="BibtexExportResult.Warnings"/>.
/// Serialization is done by <see cref="BibtexSerializer"/> in .NET, NOT by
/// the biblatex-helper writer: probing the helper showed its write path
/// canonicalizes journal/address/school to journaltitle/location/institution,
/// which classic BibTeX would silently ignore. This mapper additionally
/// escapes values for a real LaTeX compile: typeset-text fields and name
/// components get LaTeX special-character escaping, and braces are escaped
/// when (and only when) they do not balance, which classic BibTeX cannot
/// represent raw.
/// </summary>
public static class BibtexExportMapper
{
    public static Result<BibtexExportResult> MapItems(IEnumerable<ItemMetadata> items)
    {
        List<BiblatexWriteEntryDto> entries = [];
        List<string> warnings = [];
        foreach (ItemMetadata item in items)
        {
            Result<BiblatexWriteEntryDto> mapped = MapItem(item, warnings);
            if (mapped.IsFailure)
            {
                return Result<BibtexExportResult>.Failure(mapped.ErrorCode!, mapped.ErrorMessage!);
            }

            entries.Add(mapped.Value);
        }

        return Result<BibtexExportResult>.Success(
            new BibtexExportResult(entries, warnings.Distinct(StringComparer.Ordinal).ToArray()));
    }

    public static Result<BiblatexWriteEntryDto> MapItem(ItemMetadata item, List<string>? warnings = null)
    {
        if (string.Equals(item.ItemType, "general", StringComparison.Ordinal))
        {
            return Result<BiblatexWriteEntryDto>.Failure(
                AppErrorCodes.BiblatexGeneralExportForbidden,
                "general 题录禁止导出为 BibTeX。");
        }

        warnings ??= new List<string>();
        string key = item.CitationKey;
        string entryType = MapEntryType(item, warnings);
        Dictionary<string, string> fields = new(StringComparer.Ordinal);

        Set(fields, "title", JoinTitle(item.Title, item.Subtitle));
        bool inContainer = entryType is "inbook" or "incollection" or "inproceedings";
        switch (entryType)
        {
            case "article":
                Set(fields, "journal", item.PublicationTitle);
                break;
            case "inbook":
            case "incollection":
            case "inproceedings":
                Set(fields, "booktitle", item.PublicationTitle);
                break;
            case "proceedings":
                // The proceedings volume itself is the item; its title is the title.
                break;
            default:
                if (!string.IsNullOrWhiteSpace(item.PublicationTitle))
                {
                    warnings.Add(
                        $"{key}: container title '{item.PublicationTitle}' has no classic BibTeX field for @{entryType} and was dropped.");
                }

                break;
        }

        Set(fields, "series", item.CollectionTitle);
        Set(fields, "publisher", item.Publisher);
        if (entryType is "phdthesis" or "mastersthesis")
        {
            // Classic theses name the institution "school", not "publisher".
            if (fields.Remove("publisher", out string? school))
            {
                fields["school"] = school;
            }
        }
        else if (entryType is "techreport")
        {
            if (fields.Remove("publisher", out string? institution))
            {
                fields["institution"] = institution;
            }

            Set(fields, "type", item.Genre);
            Set(fields, "number", item.Number);
        }

        Set(fields, "address", item.Place);
        Set(fields, "edition", item.Edition);
        Set(fields, "volume", item.Volume);
        if (entryType == "article")
        {
            Set(fields, "number", item.Issue ?? item.Number);
        }
        else if (entryType != "techreport")
        {
            Set(fields, "number", item.Number);
        }

        if (inContainer)
        {
            Set(fields, "chapter", item.ChapterNumber);
            Set(fields, "pages", NormalizePages(item.Pages));
        }
        else if (entryType == "article")
        {
            Set(fields, "pages", NormalizePages(item.Pages));
        }

        Set(fields, "note", item.Note);
        MapIssuedDate(item, fields, warnings);
        MapIdentifiers(item, fields, warnings);
        DropWithWarning("language", item.Language, key, warnings,
            "classic BibTeX has no language field");
        DropWithWarning("abstract", item.Abstract, key, warnings,
            "classic BibTeX has no standard abstract field");
        DropWithWarning("pubstate", item.Status, key, warnings,
            "classic BibTeX has no pubstate field");
        DropWithWarning("version", item.Version, key, warnings,
            "classic BibTeX has no version field");

        Dictionary<string, IReadOnlyList<BiblatexPersonDto>> persons = new(StringComparer.Ordinal);
        AddPersons(persons, "author", item.Creators, ItemCreatorRoles.Author);
        if (inContainer)
        {
            // In classic BibTeX the container's creators are the entry editors;
            // Patchouli's container-author role folds into editor here.
            List<BiblatexPersonDto> editors = PersonsOf(item.Creators, ItemCreatorRoles.Editor)
                .Concat(PersonsOf(item.Creators, ItemCreatorRoles.ContainerAuthor)).ToList();
            if (editors.Count > 0)
            {
                persons["editor"] = editors;
            }
        }
        else
        {
            AddPersons(persons, "editor", item.Creators, ItemCreatorRoles.Editor);
        }

        if (item.Creators.Any(static creator => creator.Role == ItemCreatorRoles.Translator))
        {
            warnings.Add($"{key}: translator names have no classic BibTeX field and were dropped.");
        }

        IReadOnlyList<string> keywords = ParseTags(item.TagsJson).Select(EscapeTextValue).ToArray();
        BiblatexWriteEntryDto entry = new(
            key,
            entryType,
            fields.ToDictionary(pair => pair.Key, pair => EscapeFieldValue(pair.Key, pair.Value),
                StringComparer.Ordinal),
            persons,
            keywords);
        return Result<BiblatexWriteEntryDto>.Success(entry);
    }

    private static string MapEntryType(ItemMetadata item, List<string> warnings)
    {
        string? mapped = item.ItemType switch
        {
            "article" or "article-journal" or "article-magazine" or "article-newspaper" => "article",
            "book" => "book",
            "chapter" => "inbook",
            "paper-conference" => "inproceedings",
            "entry" or "entry-dictionary" or "entry-encyclopedia" => "incollection",
            "report" => "techreport",
            "manuscript" => "unpublished",
            "collection" => "proceedings",
            "thesis" => ThesisType(item, warnings),
            _ => null
        };
        if (mapped is not null)
        {
            return mapped;
        }

        warnings.Add(
            $"{item.CitationKey}: CSL type '{item.ItemType}' has no classic BibTeX equivalent and was mapped to misc.");
        return "misc";
    }

    private static string ThesisType(ItemMetadata item, List<string> warnings)
    {
        string genre = item.Genre ?? "";
        if (genre.Contains("phd", StringComparison.OrdinalIgnoreCase) ||
            genre.Contains("doctoral", StringComparison.OrdinalIgnoreCase) ||
            genre.Contains("博士", StringComparison.Ordinal))
        {
            return "phdthesis";
        }

        if (genre.Contains("master", StringComparison.OrdinalIgnoreCase) ||
            genre.Contains("硕士", StringComparison.Ordinal))
        {
            return "mastersthesis";
        }

        warnings.Add(
            $"{item.CitationKey}: thesis degree is not recorded, so it cannot be told apart as phdthesis/mastersthesis; mapped to misc.");
        return "misc";
    }

    private static void MapIssuedDate(ItemMetadata item, Dictionary<string, string> fields,
        List<string> warnings)
    {
        ItemDate? issued = item.Dates.FirstOrDefault(static date => date.Role == ItemDateRoles.Issued);
        if (issued is null)
        {
            return;
        }

        if (issued.Circa)
        {
            warnings.Add($"{item.CitationKey}: circa (approximate) date cannot be expressed in classic BibTeX.");
        }

        int? year = null;
        int? month = null;
        if (!string.IsNullOrWhiteSpace(issued.DatePartsJson))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(issued.DatePartsJson);
                if (document.RootElement.ValueKind == JsonValueKind.Array &&
                    document.RootElement.GetArrayLength() > 0 &&
                    document.RootElement[0].ValueKind == JsonValueKind.Array &&
                    document.RootElement[0].GetArrayLength() > 0)
                {
                    JsonElement first = document.RootElement[0];
                    year = first[0].GetInt32();
                    if (first.GetArrayLength() > 1)
                    {
                        month = first[1].GetInt32();
                    }
                }
            }
            catch (JsonException)
            {
                year = null;
            }
        }

        if (year is null && !string.IsNullOrWhiteSpace(issued.Literal) &&
            issued.Literal.Trim().Length >= 4 &&
            int.TryParse(issued.Literal.Trim()[..4], out int literalYear))
        {
            year = literalYear;
        }

        if (year is null)
        {
            if (!string.IsNullOrWhiteSpace(issued.Literal) || !string.IsNullOrWhiteSpace(issued.DatePartsJson))
            {
                warnings.Add($"{item.CitationKey}: issued date has no usable year for classic BibTeX.");
            }

            return;
        }

        fields["year"] = year.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (month is >= 1 and <= 12)
        {
            fields["month"] = month.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private static void MapIdentifiers(ItemMetadata item, Dictionary<string, string> fields,
        List<string> warnings)
    {
        foreach (ItemIdentifier identifier in item.Identifiers)
        {
            string scheme = BuiltInIdentifierSchemes.Normalize(identifier.Scheme);
            string value = identifier.Value.Trim();
            if (value.Length == 0)
            {
                continue;
            }

            switch (scheme)
            {
                case BuiltInIdentifierSchemes.DOI:
                    Set(fields, "doi", value);
                    break;
                case BuiltInIdentifierSchemes.ISBN:
                    Set(fields, "isbn", value);
                    break;
                case BuiltInIdentifierSchemes.ISSN:
                    Set(fields, "issn", value);
                    break;
                case BuiltInIdentifierSchemes.URL:
                    Set(fields, "url", value);
                    break;
                default:
                    warnings.Add(
                        $"{item.CitationKey}: identifier '{scheme}' has no classic BibTeX field and was dropped.");
                    break;
            }
        }
    }

    private static void DropWithWarning(string field, string? value,
        string key, List<string> warnings, string reason)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            warnings.Add($"{key}: {field} was dropped ({reason}).");
        }
    }

    private static IReadOnlyList<string> ParseTags(string tagsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(tagsJson)?
                       .Select(static value => value.Trim())
                       .Where(static value => value.Length > 0)
                       .ToArray()
                   ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? JoinTitle(string title, string? subtitle)
    {
        return string.IsNullOrWhiteSpace(subtitle) ? title : $"{title}: {subtitle}";
    }

    /// <summary>Classic BibTeX page ranges use a double hyphen (33--45).</summary>
    private static string? NormalizePages(string? pages)
    {
        if (string.IsNullOrWhiteSpace(pages))
        {
            return pages;
        }

        string trimmed = pages.Trim();
        return System.Text.RegularExpressions.Regex.Replace(trimmed, @"(\d)-(\d)", "$1--$2");
    }

    /// <summary>
    /// Fields whose values are machine identifiers or pre-normalized ranges
    /// rather than typeset text: escaping their characters would corrupt the
    /// data (an underscore inside a DOI, the "--" of a page range), so they
    /// keep the brace-balance treatment only.
    /// </summary>
    private static readonly HashSet<string> NonTextFields = new(StringComparer.Ordinal)
    {
        "pages", "doi", "isbn", "issn", "url"
    };

    private static string EscapeFieldValue(string field, string value)
    {
        return NonTextFields.Contains(field) ? EscapeValue(value) : EscapeTextValue(value);
    }

    /// <summary>
    /// The helper writer emits values verbatim inside braces; a value with
    /// unbalanced braces would corrupt the entry, so braces are escaped when
    /// (and only when) they do not balance.
    /// </summary>
    private static string EscapeValue(string value)
    {
        if (BracesAreBalanced(value))
        {
            return value;
        }

        return value.Replace("{", "\\{", StringComparison.Ordinal)
            .Replace("}", "\\}", StringComparison.Ordinal);
    }

    /// <summary>
    /// Escapes a typeset-text value at the same level as
    /// CitationFormatSupport.EscapeLatexText (backslash, &amp;, %, $, #, _,
    /// ~, ^): left raw inside a .bib field, those characters break a real
    /// LaTeX compile. Balanced braces are preserved verbatim — in a title
    /// they are BibTeX's case-protection mechanism — while unbalanced braces
    /// are escaped as in <see cref="EscapeValue"/>.
    /// </summary>
    private static string EscapeTextValue(string value)
    {
        bool bracesBalanced = BracesAreBalanced(value);
        StringBuilder builder = new(value.Length);
        foreach (char ch in value)
        {
            switch (ch)
            {
                case '\\': builder.Append("\\textbackslash{}"); break;
                case '&': builder.Append("\\&"); break;
                case '%': builder.Append("\\%"); break;
                case '$': builder.Append("\\$"); break;
                case '#': builder.Append("\\#"); break;
                case '_': builder.Append("\\_"); break;
                case '~': builder.Append("\\textasciitilde{}"); break;
                case '^': builder.Append("\\textasciicircum{}"); break;
                case '{': builder.Append(bracesBalanced ? "{" : "\\{"); break;
                case '}': builder.Append(bracesBalanced ? "}" : "\\}"); break;
                default: builder.Append(ch); break;
            }
        }

        return builder.ToString();
    }

    private static bool BracesAreBalanced(string value)
    {
        int depth = 0;
        foreach (char ch in value)
        {
            if (ch == '{')
            {
                depth++;
            }
            else if (ch == '}')
            {
                depth--;
                if (depth < 0)
                {
                    return false;
                }
            }
        }

        return depth == 0;
    }

    private static void AddPersons(
        Dictionary<string, IReadOnlyList<BiblatexPersonDto>> persons,
        string roleKey,
        IEnumerable<ItemCreator> creators,
        string role)
    {
        BiblatexPersonDto[] values = PersonsOf(creators, role).ToArray();
        if (values.Length > 0)
        {
            persons[roleKey] = values;
        }
    }

    private static IEnumerable<BiblatexPersonDto> PersonsOf(IEnumerable<ItemCreator> creators, string role)
    {
        return creators
            .Where(creator => creator.Role == role)
            .Select(static creator => new BiblatexPersonDto(
                EscapePersonComponent(creator.Family),
                EscapePersonComponent(creator.Given),
                EscapePersonComponent(creator.Particles),
                EscapePersonComponent(creator.Suffix),
                EscapePersonComponent(creator.Literal)));
    }

    private static string? EscapePersonComponent(string? component)
    {
        // Name components are typeset text, so they get the same escaping as
        // text fields; the name structure itself (the "Family, Suffix,
        // Given" comma form and the " and " joins) is the serializer's and
        // stays unescaped.
        return component is null ? null : EscapeTextValue(component);
    }

    private static void Set(Dictionary<string, string> fields, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            fields[key] = value.Trim();
        }
    }
}
