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

        if (item.ItemType is "article-journal")
        {
            Set(fields, "journaltitle", item.PublicationTitle);
            Set(fields, "shortjournal", item.ContainerTitleShort);
            Set(fields, "number", item.Issue ?? item.Number);
        }
        else if (item.ItemType is "chapter" or "paper-conference")
        {
            Set(fields, "booktitle", item.PublicationTitle);
            Set(fields, "number", item.Number ?? item.Issue);
        }
        else
        {
            Set(fields, "number", item.Number);
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
            }
        }

        Dictionary<string, IReadOnlyList<BiblatexPersonDto>> persons = new(StringComparer.Ordinal);
        AddPersons(persons, "author", item.Creators, ItemCreatorRoles.Author);
        AddPersons(persons, "editor", item.Creators, ItemCreatorRoles.Editor);
        AddPersons(persons, "translator", item.Creators, ItemCreatorRoles.Translator);
        AddPersons(persons, "bookauthor", item.Creators, ItemCreatorRoles.ContainerAuthor);

        foreach (ItemDate date in item.Dates)
        {
            string? field = date.Role switch
            {
                ItemDateRoles.Issued => "date",
                ItemDateRoles.Accessed => "urldate",
                ItemDateRoles.OriginalDate => "origdate",
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

            JsonElement first = document.RootElement[0];
            if (first.ValueKind != JsonValueKind.Array || first.GetArrayLength() == 0)
            {
                return null;
            }

            int[] parts = first.EnumerateArray().Select(static part => part.GetInt32()).ToArray();
            return parts.Length switch
            {
                1 => parts[0].ToString(),
                2 => $"{parts[0]:D4}-{parts[1]:D2}",
                >= 3 => $"{parts[0]:D4}-{parts[1]:D2}-{parts[2]:D2}",
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
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

    private static void Set(Dictionary<string, string> fields, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        fields[key] = value.Trim();
    }
}
