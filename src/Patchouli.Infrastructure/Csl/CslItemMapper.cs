using System.Text.Json;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Csl;
using Patchouli.Core.Results;

namespace Patchouli.Infrastructure.Csl;

public sealed class CslItemMapper : ICslItemMapper
{
    public Task<Result<CslMappedItem>> MapAsync(ItemMetadata item, CancellationToken cancellationToken = default)
    {
        if (string.Equals(item.ItemType, "general", StringComparison.Ordinal))
        {
            return Task.FromResult(Result<CslMappedItem>.Failure("general_type_not_renderable",
                "Patchouli item type 'general' cannot be rendered as CSL bibliography."));
        }

        Dictionary<string, object?> variables = new(StringComparer.Ordinal)
        {
            ["id"] = item.ItemId.ToString(),
            ["type"] = item.ItemType,
            ["citation-key"] = item.CitationKey,
            ["title"] = item.Title,
            ["title-short"] = item.TitleShort,
            ["subtitle"] = item.Subtitle,
            ["container-title"] = item.PublicationTitle,
            ["container-title-short"] = item.ContainerTitleShort,
            ["collection-title"] = item.CollectionTitle,
            ["publisher"] = item.Publisher,
            ["publisher-place"] = item.Place,
            ["edition"] = item.Edition,
            ["genre"] = item.Genre,
            ["number"] = item.Number,
            ["chapter-number"] = item.ChapterNumber,
            ["volume"] = item.Volume,
            ["version"] = item.Version,
            ["issue"] = item.Issue,
            ["page"] = item.Pages,
            ["language"] = item.Language,
            ["status"] = item.Status,
            ["note"] = item.Note,
            ["abstract"] = item.Abstract,
            ["keyword"] = ParseJsonArray(item.TagsJson),
            ["collection"] = ParseJsonArray(item.CollectionsJson)
        };

        Dictionary<string, object?> creators = item.Creators
            .GroupBy(creator => creator.Role, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (object?)group.Select(creator => new Dictionary<string, object?>
                {
                    ["family"] = creator.Family,
                    ["given"] = creator.Given,
                    ["literal"] = creator.Literal
                }).ToArray(),
                StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> creatorGroup in creators)
        {
            variables[creatorGroup.Key] = creatorGroup.Value;
        }

        foreach (ItemDate date in item.Dates)
        {
            variables[date.Role] = CreateDateObject(date);
        }

        foreach (ItemIdentifier identifier in item.Identifiers)
        {
            if (string.Equals(identifier.Scheme, BuiltInIdentifierSchemes.DOI, StringComparison.OrdinalIgnoreCase))
            {
                variables["DOI"] = identifier.Value;
            }
            else if (string.Equals(identifier.Scheme, BuiltInIdentifierSchemes.ISBN,
                         StringComparison.OrdinalIgnoreCase))
            {
                variables["ISBN"] = identifier.Value;
            }
            else if (string.Equals(identifier.Scheme, BuiltInIdentifierSchemes.ISSN,
                         StringComparison.OrdinalIgnoreCase))
            {
                variables["ISSN"] = identifier.Value;
            }
        }

        variables["extra_csl"] = ParseJsonObject(item.CustomFieldsJson);
        return Task.FromResult(Result<CslMappedItem>.Success(new CslMappedItem(item.ItemId, item.ItemType, variables)));
    }

    private static object CreateDateObject(ItemDate date)
    {
        Dictionary<string, object?> dictionary = new(StringComparer.Ordinal)
        {
            ["literal"] = date.Literal,
            ["circa"] = date.Circa,
            ["season"] = date.Season
        };

        try
        {
            if (!string.IsNullOrWhiteSpace(date.DatePartsJson))
            {
                List<List<int>>? parts = JsonSerializer.Deserialize<List<List<int>>>(date.DatePartsJson);
                if (parts is { Count: > 0 })
                {
                    dictionary["date-parts"] = parts;
                }
            }
        }
        catch
        {
            dictionary["date-parts"] = Array.Empty<object>();
        }

        return dictionary;
    }

    private static IReadOnlyList<string> ParseJsonArray(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<string>();
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind != JsonValueKind.Array
                ? Array.Empty<string>()
                : document.RootElement.EnumerateArray()
                    .Where(element => element.ValueKind == JsonValueKind.String)
                    .Select(element => element.GetString()!)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyDictionary<string, object?> ParseJsonObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, object?>(StringComparer.Ordinal);
            }

            Dictionary<string, object?> result = new(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                result[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number when property.Value.TryGetInt64(out long longValue) => longValue,
                    JsonValueKind.Number => property.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => property.Value.GetRawText()
                };
            }

            return result;
        }
        catch
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }
    }
}
