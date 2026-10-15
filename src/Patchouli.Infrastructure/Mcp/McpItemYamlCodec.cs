using System.Text.Json;
using System.Text.Json.Nodes;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Csl;
using Patchouli.Mcp;
using YamlDotNet.Core;

namespace Patchouli.Infrastructure.Mcp;

/// <summary>Encodes and decodes the Patchouli item profile layered over a CSL YAML mapping.</summary>
public static class McpItemYamlCodec
{
    private const string Profile = "patchouli-item-1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly string[] CreatorRoles =
    [
        "author", "editor", "translator", "container-author", "host", "producer", "director", "composer",
        "performer", "interviewer", "recipient", "script-writer", "original-author", "organizer",
        "reviewed-author", "holder"
    ];

    private static readonly string[] DateRoles = ["issued", "accessed", "original-date", "event-date", "submitted"];

    private static readonly string[] IdentifierSchemes =
    [
        BuiltInIdentifierSchemes.DOI, BuiltInIdentifierSchemes.ISBN, BuiltInIdentifierSchemes.ISSN,
        BuiltInIdentifierSchemes.URL, BuiltInIdentifierSchemes.CallNumber
    ];

    private static readonly HashSet<string> CslFieldNames = new(
    [
        "type", "title", "subtitle", "title-short", "container-title", "container-title-short", "collection-title",
        "publisher", "publisher-place", "edition", "genre", "number", "chapter-number", "volume", "version",
        "issue", "page", "language", "status", "note", "abstract", "DOI", "ISBN", "ISSN", "URL", "call-number"
    ], StringComparer.Ordinal);

    public static string Encode(McpItemMetadataResponse item, bool includeTags)
    {
        ArgumentNullException.ThrowIfNull(item);
        JsonObject csl = new();
        if (!string.Equals(item.ItemType, "general", StringComparison.OrdinalIgnoreCase))
        {
            csl["type"] = item.ItemType;
        }

        Set(csl, "title", item.Title);
        Set(csl, "subtitle", item.Subtitle);
        Set(csl, "title-short", item.TitleShort);
        Set(csl, "container-title", item.PublicationTitle);
        Set(csl, "container-title-short", item.ContainerTitleShort);
        Set(csl, "collection-title", item.CollectionTitle);
        Set(csl, "publisher", item.Publisher);
        Set(csl, "publisher-place", item.Place);
        Set(csl, "edition", item.Edition);
        Set(csl, "genre", item.Genre);
        Set(csl, "number", item.Number);
        Set(csl, "chapter-number", item.ChapterNumber);
        Set(csl, "volume", item.Volume);
        Set(csl, "version", item.Version);
        Set(csl, "issue", item.Issue);
        Set(csl, "page", item.Pages);
        Set(csl, "language", item.Language);
        Set(csl, "status", item.Status);
        Set(csl, "note", item.Note);
        Set(csl, "abstract", item.Abstract);

        foreach (string role in CreatorRoles)
        {
            csl[role] = new JsonArray(item.Creators.Where(c => IsRole(c.Role, role)).Select(ToCslCreator).ToArray());
        }

        foreach (string role in DateRoles)
        {
            McpItemDate? date = item.Dates.FirstOrDefault(d => IsRole(d.Role, role));
            csl[role] = date is not null
                ? ToCslDate(date)
                : role == "issued"
                    ? item.Date
                    : null;
        }

        foreach (string scheme in IdentifierSchemes)
        {
            string? key = CslIdentifierKey(scheme);
            McpItemIdentifier? identifier = item.Identifiers.FirstOrDefault(i =>
                BuiltInIdentifierSchemes.Normalize(i.Scheme) == scheme);
            csl[key!] = identifier?.Value;
        }

        JsonObject patchouli = new()
        {
            ["item_id"] = item.ItemId.ToString(),
            ["citation_key"] = item.CitationKey,
            ["identifiers"] = JsonSerializer.SerializeToNode(item.Identifiers, JsonOptions),
            ["custom_fields"] = ParseJson(item.CustomFieldsJson, new JsonObject())
        };
        if (string.Equals(item.ItemType, "general", StringComparison.OrdinalIgnoreCase))
        {
            patchouli["item_type"] = "general";
        }

        if (includeTags)
        {
            patchouli["tags"] = ParseJson(item.TagsJson, new JsonArray());
        }

        JsonObject root = new()
        {
            ["profile"] = Profile,
            ["csl"] = csl,
            ["extensions"] = new JsonObject { ["patchouli"] = patchouli }
        };
        return McpYamlCodec.Encode(root);
    }

    public static Result<(UpdateItemRequest Request, IReadOnlyList<ItemIdentifierInput> Identifiers)> Decode(
        string yaml, ItemMetadata existing, bool preserveTags)
    {
        ArgumentNullException.ThrowIfNull(existing);
        try
        {
            JsonNode? decoded = McpYamlCodec.Decode(yaml);
            if (decoded is not JsonObject root || !TryString(root, "profile", out string? profile) ||
                profile != Profile)
            {
                return Failure("The YAML root must use profile 'patchouli-item-1'.");
            }

            if (GetObject(root, "csl") is not JsonObject csl)
            {
                return Failure("The YAML profile requires a 'csl' mapping.");
            }

            ValidateCslFields(csl);

            JsonObject? extensions = OptionalObject(root, "extensions");
            JsonObject? patchouli = extensions is null ? null : OptionalObject(extensions, "patchouli");
            if (patchouli is not null && TryString(patchouli, "item_id", out string? yamlItemId) &&
                !string.Equals(yamlItemId, existing.ItemId.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return Failure("The YAML item URI does not match the target item.");
            }

            string itemType = GetString(csl, "type") ?? GetString(patchouli, "item_type") ?? "general";
            IReadOnlyList<ItemCreatorInput> creators = ReadCreators(csl);
            IReadOnlyList<ItemDateInput> dates = ReadDates(csl);
            IReadOnlyList<ItemIdentifierInput> identifiers = ReadIdentifiers(patchouli, csl);
            JsonArray? tags = patchouli is null ? null : OptionalArray(patchouli, "tags");
            if (tags is not null && tags.Any(tag => tag is not JsonValue value || !value.TryGetValue(out string? _)))
            {
                throw new FormatException("Patchouli tags must be an array of strings.");
            }

            string tagsJson = preserveTags
                ? existing.TagsJson
                : tags?.ToJsonString() ?? "[]";
            JsonObject? customFields = patchouli is null ? null : OptionalObject(patchouli, "custom_fields");
            string customFieldsJson = customFields?.ToJsonString() ?? "{}";

            UpdateItemRequest request = new(
                itemType,
                GetString(csl, "title") ?? string.Empty,
                GetString(csl, "subtitle"),
                GetString(csl, "title-short"),
                null,
                GetDateDisplay(csl["issued"]),
                GetString(csl, "container-title"),
                GetString(csl, "container-title-short"),
                GetString(csl, "collection-title"),
                GetString(csl, "publisher"),
                GetString(csl, "publisher-place"),
                GetNumberOrString(csl, "edition"),
                GetString(csl, "genre"),
                GetNumberOrString(csl, "number"),
                GetNumberOrString(csl, "chapter-number"),
                GetNumberOrString(csl, "volume"),
                GetString(csl, "version"),
                GetNumberOrString(csl, "issue"),
                GetNumberOrString(csl, "page"),
                GetString(csl, "language"),
                GetString(csl, "status"),
                GetString(csl, "note"),
                GetString(csl, "abstract"),
                tagsJson,
                existing.CollectionsJson,
                customFieldsJson,
                creators,
                dates,
                Collections: null);

            return Result<(UpdateItemRequest, IReadOnlyList<ItemIdentifierInput>)>.Success((request, identifiers));
        }
        catch (Exception exception) when (exception is FormatException or JsonException or InvalidOperationException or
                                              YamlException)
        {
            return Failure($"Invalid Patchouli item YAML: {exception.Message}");
        }
    }

    private static IReadOnlyList<ItemCreatorInput> ReadCreators(JsonObject csl)
    {
        List<ItemCreatorInput> creators = [];
        foreach (string role in CreatorRoles)
        {
            if (!csl.TryGetPropertyValue(role, out JsonNode? value) || value is null)
            {
                continue;
            }

            if (value is not JsonArray entries)
            {
                throw new FormatException($"CSL creator role '{role}' must be an array or null.");
            }

            foreach (JsonNode? entry in entries)
            {
                JsonObject creator = entry as JsonObject ?? throw new FormatException("CSL creators must be mappings.");
                ValidateNestedFields(creator, "CSL creator",
                    ["family", "given", "literal", "suffix", "non-dropping-particle"]);
                creators.Add(new ItemCreatorInput(role, GetString(creator, "family"), GetString(creator, "given"),
                    GetString(creator, "literal"), GetString(creator, "suffix"),
                    GetString(creator, "non-dropping-particle")));
            }
        }

        return creators;
    }

    private static IReadOnlyList<ItemDateInput> ReadDates(JsonObject csl)
    {
        List<ItemDateInput> dates = [];
        foreach (string role in DateRoles)
        {
            if (!csl.TryGetPropertyValue(role, out JsonNode? value) || value is null)
            {
                continue;
            }

            if (value is JsonValue scalar && scalar.TryGetValue(out string? literal))
            {
                dates.Add(new ItemDateInput(role, "[]", Literal: literal));
                continue;
            }

            if (value is not JsonObject date)
            {
                throw new FormatException($"CSL date '{role}' must be a mapping or string.");
            }

            ValidateNestedFields(date, $"CSL date '{role}'", ["date-parts", "circa", "season", "literal"]);
            dates.Add(new ItemDateInput(role, GetCslDatePartsJson(date) ?? "[]", GetBoolean(date, "circa"),
                GetNumberOrString(date, "season"), GetString(date, "literal")));
        }

        return dates;
    }

    private static IReadOnlyList<ItemIdentifierInput> ReadIdentifiers(JsonObject? patchouli, JsonObject csl)
    {
        JsonArray? extensionIdentifiers = patchouli is null ? null : OptionalArray(patchouli, "identifiers");
        List<ItemIdentifierInput> identifiers = (extensionIdentifiers ?? [])
            .Select(node =>
            {
                JsonObject identifier = node as JsonObject ??
                                        throw new FormatException("Identifier entries must be mappings.");
                string scheme = GetString(identifier, "scheme") ??
                                throw new FormatException("Identifier scheme is required.");
                string value = GetString(identifier, "value") ??
                               throw new FormatException("Identifier value is required.");
                return new ItemIdentifierInput(BuiltInIdentifierSchemes.Normalize(scheme), value,
                    GetString(identifier, "note"));
            }).ToList();

        foreach (string scheme in IdentifierSchemes)
        {
            string key = CslIdentifierKey(scheme)!;
            if (!csl.TryGetPropertyValue(key, out JsonNode? node))
            {
                continue;
            }

            if (node is null)
            {
                identifiers.RemoveAll(i => i.Scheme == scheme);
                continue;
            }

            string value = GetString(csl, key) ??
                           throw new FormatException($"CSL identifier '{key}' must be a string.");
            int projectedIndex = identifiers.FindIndex(i => i.Scheme == scheme);
            if (projectedIndex < 0)
            {
                identifiers.Add(new ItemIdentifierInput(scheme, value));
            }
            else if (!string.Equals(identifiers[projectedIndex].Value, value, StringComparison.Ordinal))
            {
                ItemIdentifierInput projected = identifiers[projectedIndex];
                identifiers[projectedIndex] = projected with { Value = value };
            }
        }

        return identifiers;
    }

    private static JsonObject ToCslCreator(McpItemCreator creator)
    {
        JsonObject result = new();
        Set(result, "family", creator.Family);
        Set(result, "given", creator.Given);
        Set(result, "literal", creator.Literal);
        Set(result, "suffix", creator.Suffix);
        Set(result, "non-dropping-particle", creator.Particles);
        return result;
    }

    private static JsonObject ToCslDate(McpItemDate date)
    {
        JsonObject result = new();
        using JsonDocument dateParts = JsonDocument.Parse(date.DatePartsJson);
        List<List<int>> parts = CslDateParts.Read(dateParts.RootElement);
        if (parts.Count > 0)
        {
            result["date-parts"] = JsonSerializer.SerializeToNode(parts);
        }

        if (date.Circa)
        {
            result["circa"] = true;
        }

        Set(result, "season", date.Season);
        Set(result, "literal", date.Literal);
        return result;
    }

    private static string? GetCslDatePartsJson(JsonObject date)
    {
        if (!date.TryGetPropertyValue("date-parts", out JsonNode? parts))
        {
            return null;
        }

        // Earlier projections wrapped the empty array of literal-only dates once more.
        if (parts is JsonArray { Count: 1 } legacy && legacy[0] is JsonArray { Count: 0 } &&
            !string.IsNullOrWhiteSpace(GetString(date, "literal")))
        {
            return "[]";
        }

        using JsonDocument document = JsonDocument.Parse(parts?.ToJsonString() ?? "null");
        return JsonSerializer.Serialize(CslDateParts.Read(document.RootElement));
    }

    private static string? GetDateDisplay(JsonNode? date)
    {
        if (date is JsonValue scalar && scalar.TryGetValue(out string? literal))
        {
            return literal;
        }

        if (date is JsonObject mapping)
        {
            return GetString(mapping, "literal") ?? GetCslDatePartsJson(mapping);
        }

        return null;
    }

    private static string? CslIdentifierKey(string scheme)
    {
        return BuiltInIdentifierSchemes.Normalize(scheme) switch
        {
            "doi" => "DOI",
            "isbn" => "ISBN",
            "issn" => "ISSN",
            "url" => "URL",
            BuiltInIdentifierSchemes.CallNumber => "call-number",
            _ => null
        };
    }

    private static void Set(JsonObject target, string key, string? value)
    {
        target[key] = value;
    }

    private static JsonNode? ParseJson(string json, JsonNode fallback)
    {
        return JsonNode.Parse(json) ?? fallback;
    }

    private static JsonObject? GetObject(JsonObject? parent, string key)
    {
        return parent?[key] as JsonObject;
    }

    private static string? GetString(JsonObject? parent, string key)
    {
        if (parent is null || !parent.TryGetPropertyValue(key, out JsonNode? node) || node is null)
        {
            return null;
        }

        return node is JsonValue value && value.TryGetValue(out string? result)
            ? result
            : throw new FormatException($"'{key}' must be a string.");
    }

    private static bool TryString(JsonObject parent, string key, out string? result)
    {
        result = GetString(parent, key);
        return result is not null;
    }

    private static string? GetNumberOrString(JsonObject parent, string key)
    {
        JsonNode? node = parent[key];
        if (node is JsonValue value && value.GetValueKind() == JsonValueKind.Number)
        {
            return value.ToJsonString();
        }

        return GetString(parent, key);
    }

    private static bool GetBoolean(JsonObject parent, string key)
    {
        if (!parent.TryGetPropertyValue(key, out JsonNode? node))
        {
            return false;
        }

        if (node is not JsonValue value || !value.TryGetValue(out bool result))
        {
            throw new FormatException($"'{key}' must be a boolean.");
        }

        return result;
    }

    private static JsonObject? OptionalObject(JsonObject parent, string key)
    {
        if (!parent.TryGetPropertyValue(key, out JsonNode? value))
        {
            return null;
        }

        return value as JsonObject ?? throw new FormatException($"'{key}' must be a mapping.");
    }

    private static JsonArray? OptionalArray(JsonObject parent, string key)
    {
        if (!parent.TryGetPropertyValue(key, out JsonNode? value))
        {
            return null;
        }

        return value as JsonArray ?? throw new FormatException($"'{key}' must be an array.");
    }

    private static void ValidateCslFields(JsonObject csl)
    {
        HashSet<string> accepted = new(CslFieldNames, StringComparer.Ordinal);
        accepted.UnionWith(CreatorRoles);
        accepted.UnionWith(DateRoles);
        foreach (string field in csl.Select(property => property.Key))
        {
            if (!accepted.Contains(field))
            {
                throw new FormatException($"Unsupported CSL field '{field}' in profile '{Profile}'.");
            }
        }
    }

    private static void ValidateNestedFields(JsonObject value, string description, IReadOnlyCollection<string> allowed)
    {
        foreach (string field in value.Select(property => property.Key))
        {
            if (!allowed.Contains(field, StringComparer.Ordinal))
            {
                throw new FormatException($"Unsupported field '{field}' in {description}.");
            }
        }
    }

    private static bool IsRole(string actual, string expected)
    {
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static Result<(UpdateItemRequest Request, IReadOnlyList<ItemIdentifierInput> Identifiers)> Failure(
        string message)
    {
        return Result<(UpdateItemRequest, IReadOnlyList<ItemIdentifierInput>)>.Failure(AppErrorCodes.ValidationFailed,
            message);
    }
}
