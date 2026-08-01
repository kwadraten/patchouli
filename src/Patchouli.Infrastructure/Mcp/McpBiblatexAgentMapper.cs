using System.Text.Json;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Results;

namespace Patchouli.Infrastructure.Mcp;

/// <summary>
/// Builds the MCP-only BibLaTeX projection for a general item. This is deliberately separate
/// from the UI export mapper so a UI export rule cannot make general items citable by accident.
/// </summary>
internal static class McpBiblatexAgentMapper
{
    public static Result<BiblatexWriteEntryDto> MapGeneralItem(ItemMetadata item)
    {
        if (!string.Equals(item.ItemType, "general", StringComparison.OrdinalIgnoreCase))
        {
            return Result<BiblatexWriteEntryDto>.Failure(
                AppErrorCodes.ValidationFailed,
                "The MCP general-item mapper only accepts general items.");
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
        Set(fields, "number", item.Number);
        Set(fields, "publisher", item.Publisher);
        Set(fields, "location", item.Place);

        foreach (ItemIdentifier identifier in item.Identifiers)
        {
            string scheme = identifier.Scheme.Trim().ToLowerInvariant();
            string value = identifier.Value.Trim();
            if (value.Length == 0)
            {
                continue;
            }

            string? field = scheme switch
            {
                BuiltInIdentifierSchemes.DOI => "doi",
                BuiltInIdentifierSchemes.ISBN => "isbn",
                BuiltInIdentifierSchemes.ISSN => "issn",
                BuiltInIdentifierSchemes.URL => "url",
                _ => null
            };
            if (field is not null)
            {
                Set(fields, field, value);
            }
        }

        Dictionary<string, IReadOnlyList<BiblatexPersonDto>> persons = new(StringComparer.Ordinal);
        AddPersons(persons, "author", item.Creators, ItemCreatorRoles.Author);
        AddPersons(persons, "editor", item.Creators, ItemCreatorRoles.Editor);
        AddPersons(persons, "translator", item.Creators, ItemCreatorRoles.Translator);
        AddPersons(persons, "bookauthor", item.Creators, ItemCreatorRoles.ContainerAuthor);
        foreach (string role in ItemCreatorRoles.Supported
                     .Where(role => role is not (ItemCreatorRoles.Author or ItemCreatorRoles.Editor
                         or ItemCreatorRoles.Translator or ItemCreatorRoles.ContainerAuthor))
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
            if (field is not null)
            {
                Set(fields, field, SerializeDate(date));
            }
        }

        return Result<BiblatexWriteEntryDto>.Success(new BiblatexWriteEntryDto(
            item.CitationKey,
            "misc",
            fields,
            persons,
            ParseTags(item.TagsJson)));
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
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() == 0)
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
        if (!string.IsNullOrWhiteSpace(value))
        {
            fields[key] = value.Trim();
        }
    }
}
