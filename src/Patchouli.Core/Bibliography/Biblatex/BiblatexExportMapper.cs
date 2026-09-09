using System.Text.Json;
using Patchouli.Core.Results;

namespace Patchouli.Core.Bibliography.Biblatex;

public static class BiblatexExportMapper
{
    public static Result<BiblatexWriteEntryDto> MapItem(ItemMetadata item)
    {
        if (!BiblatexEntryTypeMap.TryMapExportEntryType(item.ItemType, out string entryType))
        {
            return Result<BiblatexWriteEntryDto>.Failure(
                AppErrorCodes.BiblatexGeneralExportForbidden,
                "general 题录禁止导出为 BibLaTeX。");
        }

        Dictionary<string, string> fields = new(StringComparer.Ordinal);
        Set(fields, "title", item.Title);
        Set(fields, "subtitle", item.Subtitle);
        Set(fields, "shorttitle", item.TitleShort);
        Set(fields, "edition", item.Edition);
        Set(fields, "volume", item.Volume);
        Set(fields, "version", item.Version);
        Set(fields, "pages", item.Pages);
        Set(fields, "note", item.Note);
        Set(fields, "abstract", item.Abstract);
        Set(fields, "series", item.CollectionTitle);
        Set(fields, "chapter", item.ChapterNumber);
        Set(fields, "pubstate", item.Status);
        Set(fields, "language", item.Language);
        Set(fields, "type", item.Genre);

        if (item.ItemType is "article-journal" or "article" or "article-magazine" or "article-newspaper")
        {
            Set(fields, "journaltitle", item.PublicationTitle);
            Set(fields, "shortjournal", item.ContainerTitleShort);
            Set(fields, "eid", item.Number);
            Set(fields, "number", item.Issue);
        }
        else if (item.ItemType is "periodical")
        {
            Set(fields, "journaltitle", item.PublicationTitle);
            Set(fields, "shortjournal", item.ContainerTitleShort);
            Set(fields, "number", item.Number);
            Set(fields, "issue", item.Issue);
        }
        else if (item.ItemType is "chapter" or "paper-conference" or "entry" or "entry-dictionary"
                 or "entry-encyclopedia")
        {
            Set(fields, "booktitle", item.PublicationTitle);
            Set(fields, "number", item.Number ?? item.Issue);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(item.PublicationTitle))
            {
                Set(fields, "maintitle", item.PublicationTitle);
            }

            Set(fields, "number", item.Number);
            if (!fields.ContainsKey("issue"))
            {
                Set(fields, "issue", item.Issue);
            }
        }

        if (item.ItemType is "thesis" or "report")
        {
            Set(fields, "institution", item.Publisher);
        }
        else if (item.ItemType is "webpage" or "paper-conference")
        {
            Set(fields, "organization", item.Publisher);
        }
        else
        {
            Set(fields, "publisher", item.Publisher);
        }

        Set(fields, "location", item.Place);

        if (!string.IsNullOrWhiteSpace(item.CustomFieldsJson))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(item.CustomFieldsJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return Result<BiblatexWriteEntryDto>.Failure(
                        AppErrorCodes.ValidationFailed,
                        "Item custom fields must be a JSON object and cannot be exported without losing data.");
                }

                foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
                {
                    string val = prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString() ?? ""
                        : prop.Value.ToString();
                    if (string.IsNullOrWhiteSpace(val))
                    {
                        continue;
                    }

                    switch (prop.Name.ToLowerInvariant())
                    {
                        case "archive":
                            Set(fields, "archive", val);
                            break;
                        case "archive_location":
                            Set(fields, "archive_location", val);
                            break;
                        case "archive-place":
                            Set(fields, "archive-place", val);
                            if (!fields.ContainsKey("location") && item.ItemType is "manuscript" or "collection")
                            {
                                Set(fields, "location", val);
                            }

                            break;
                        case "archive_collection":
                            Set(fields, "archive_collection", val);
                            break;
                        case "event-title":
                            Set(fields, "eventtitle", val);
                            break;
                        case "event-place":
                            Set(fields, "venue", val);
                            break;
                        case "number-of-volumes":
                            Set(fields, "volumes", val);
                            break;
                        case "number-of-pages":
                            Set(fields, "pagetotal", val);
                            break;
                        case "original-title":
                            Set(fields, "origtitle", val);
                            break;
                        case "original-publisher":
                            Set(fields, "origpublisher", val);
                            break;
                        case "original-publisher-place":
                            Set(fields, "origlocation", val);
                            break;
                        case "license":
                            Set(fields, "license", val);
                            break;
                        case "medium":
                            Set(fields, "medium", val);
                            break;
                        case "dimensions":
                            Set(fields, "dimensions", val);
                            break;
                        case "scale":
                            Set(fields, "scale", val);
                            break;
                        case "authority":
                            Set(fields, "authority", val);
                            break;
                        case "jurisdiction":
                            Set(fields, "jurisdiction", val);
                            break;
                        case "division":
                            Set(fields, "division", val);
                            break;
                        case "section":
                            Set(fields, "section", val);
                            break;
                        case "references":
                            Set(fields, "references", val);
                            break;
                        case "reviewed-title":
                            Set(fields, "reviewed-title", val);
                            break;
                        case "reviewed-genre":
                            Set(fields, "reviewed-genre", val);
                            break;
                        case "eprint":
                            Set(fields, "eprint", val);
                            break;
                        case "eprinttype":
                            Set(fields, "eprinttype", val);
                            break;
                        case "eprintclass":
                            Set(fields, "eprintclass", val);
                            break;
                        case "howpublished":
                            Set(fields, "howpublished", val);
                            break;
                        case "original_biblatex_entry_type":
                            break;
                        default:
                            Set(fields, prop.Name, val);
                            break;
                    }
                }
            }
            catch (JsonException)
            {
                return Result<BiblatexWriteEntryDto>.Failure(
                    AppErrorCodes.ValidationFailed,
                    "Item custom fields contain invalid JSON and cannot be exported without losing data.");
            }
        }

        foreach (ItemIdentifier identifier in item.Identifiers)
        {
            string scheme = identifier.Scheme.Trim().ToLowerInvariant();
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
                case BuiltInIdentifierSchemes.CallNumber:
                    Set(fields, "callnumber", value);
                    break;
                case BuiltInIdentifierSchemes.ArXiv:
                    Set(fields, "eprint", value);
                    if (!fields.ContainsKey("eprinttype"))
                    {
                        Set(fields, "eprinttype", "arxiv");
                    }

                    break;
            }
        }

        Dictionary<string, IReadOnlyList<BiblatexPersonDto>> persons = new(StringComparer.Ordinal);
        AddPersons(persons, "author", item.Creators, ItemCreatorRoles.Author);
        AddPersons(persons, "editor", item.Creators, ItemCreatorRoles.Editor);
        AddPersons(persons, "translator", item.Creators, ItemCreatorRoles.Translator);
        AddPersons(persons, "bookauthor", item.Creators, ItemCreatorRoles.ContainerAuthor);
        AddPersons(persons, "origauthor", item.Creators, ItemCreatorRoles.OriginalAuthor);
        // The remaining roles have no canonical BibLaTeX name field; they are written under
        // their CSL role key so a Patchouli round trip does not lose them.
        foreach (string role in ItemCreatorRoles.Supported
                     .Where(role => role is not (ItemCreatorRoles.Author or ItemCreatorRoles.Editor
                         or ItemCreatorRoles.Translator or ItemCreatorRoles.ContainerAuthor
                         or ItemCreatorRoles.OriginalAuthor))
                     .OrderBy(static role => role, StringComparer.Ordinal))
        {
            AddPersons(persons, role, item.Creators, role);
        }

        foreach (ItemDate date in item.Dates)
        {
            string? field = date.Role switch
            {
                ItemDateRoles.Issued => "date",
                ItemDateRoles.Accessed => "urldate",
                ItemDateRoles.OriginalDate => "origdate",
                ItemDateRoles.EventDate => "eventdate",
                ItemDateRoles.Submitted => "submitted",
                _ => null
            };
            if (field is null)
            {
                continue;
            }

            string? serialized = SerializeDate(date);
            Set(fields, field, serialized);
        }

        IReadOnlyList<string> keywords = ParseTags(item.TagsJson);

        return Result<BiblatexWriteEntryDto>.Success(new BiblatexWriteEntryDto(
            item.CitationKey,
            entryType,
            fields,
            persons,
            keywords));
    }

    public static Result<IReadOnlyList<BiblatexWriteEntryDto>> MapItems(IEnumerable<ItemMetadata> items)
    {
        List<BiblatexWriteEntryDto> entries = [];
        foreach (ItemMetadata item in items)
        {
            Result<BiblatexWriteEntryDto> mapped = MapItem(item);
            if (mapped.IsFailure)
            {
                return Result<IReadOnlyList<BiblatexWriteEntryDto>>.Failure(
                    mapped.ErrorCode!,
                    mapped.ErrorMessage!);
            }

            entries.Add(mapped.Value);
        }

        return Result<IReadOnlyList<BiblatexWriteEntryDto>>.Success(entries);
    }

    private static void AddPersons(
        Dictionary<string, IReadOnlyList<BiblatexPersonDto>> persons,
        string roleKey,
        IEnumerable<ItemCreator> creators,
        string role)
    {
        BiblatexPersonDto[] values = creators
            .Where(creator => creator.Role == role)
            .Select(static creator => new BiblatexPersonDto(
                creator.Family,
                creator.Given,
                creator.Particles,
                creator.Suffix,
                creator.Literal))
            .ToArray();
        if (values.Length > 0)
        {
            persons[roleKey] = values;
        }
    }

    private static string? SerializeDate(ItemDate date)
    {
        if (!string.IsNullOrWhiteSpace(date.Literal))
        {
            return date.Literal.Trim();
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(date.DatePartsJson) ? "[]" : date.DatePartsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
            {
                return null;
            }

            int count = document.RootElement.GetArrayLength();
            if (count == 1)
            {
                string? single = FormatDatePart(document.RootElement[0]);
                if (single is null)
                {
                    return null;
                }

                return date.Circa ? $"{single}~" : single;
            }

            if (count >= 2)
            {
                string? start = FormatDatePart(document.RootElement[0]);
                string? end = FormatDatePart(document.RootElement[1]);
                if (start is null && end is null)
                {
                    return null;
                }

                string startStr = start is not null && date.Circa ? $"{start}~" : start ?? "";
                string endStr = end is not null && date.Circa ? $"{end}~" : end ?? "";
                return $"{startStr}/{endStr}";
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? FormatDatePart(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() == 0)
        {
            return null;
        }

        int[] parts = element.EnumerateArray().Select(static part => part.GetInt32()).ToArray();
        return parts.Length switch
        {
            1 => parts[0].ToString(System.Globalization.CultureInfo.InvariantCulture),
            2 => $"{parts[0]:D4}-{parts[1]:D2}",
            >= 3 => $"{parts[0]:D4}-{parts[1]:D2}-{parts[2]:D2}",
            _ => null
        };
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

    private static void Set(Dictionary<string, string> fields, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        fields[key] = value.Trim();
    }
}
