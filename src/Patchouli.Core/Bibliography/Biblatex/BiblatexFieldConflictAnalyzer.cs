using System.Text.Json;
using Patchouli.Core.Bibliography;

namespace Patchouli.Core.Bibliography.Biblatex;

public static class BiblatexFieldConflictAnalyzer
{
    public static IReadOnlyList<BiblatexFieldConflict> FindConflicts(
        ItemMetadata local,
        BiblatexMappedItem incoming)
    {
        List<BiblatexFieldConflict> conflicts = [];

        AddScalar(conflicts, "item_type", "题录类型", local.ItemType, incoming.ItemType);
        AddScalar(conflicts, "title", "标题", local.Title, incoming.Title);
        AddScalar(conflicts, "subtitle", "副标题", local.Subtitle, incoming.Subtitle);
        AddScalar(conflicts, "title_short", "短标题", local.TitleShort, incoming.TitleShort);
        AddScalar(conflicts, "publication_title", "来源", local.PublicationTitle, incoming.PublicationTitle);
        AddScalar(conflicts, "container_title_short", "来源缩写", local.ContainerTitleShort,
            incoming.ContainerTitleShort);
        AddScalar(conflicts, "collection_title", "丛书", local.CollectionTitle, incoming.CollectionTitle);
        AddScalar(conflicts, "publisher", "出版社", local.Publisher, incoming.Publisher);
        AddScalar(conflicts, "place", "出版地", local.Place, incoming.Place);
        AddScalar(conflicts, "edition", "版次", local.Edition, incoming.Edition);
        AddScalar(conflicts, "genre", "类型说明", local.Genre, incoming.Genre);
        AddScalar(conflicts, "number", "编号", local.Number, incoming.Number);
        AddScalar(conflicts, "chapter_number", "章节", local.ChapterNumber, incoming.ChapterNumber);
        AddScalar(conflicts, "volume", "卷", local.Volume, incoming.Volume);
        AddScalar(conflicts, "version", "版本", local.Version, incoming.Version);
        AddScalar(conflicts, "issue", "期", local.Issue, incoming.Issue);
        AddScalar(conflicts, "pages", "页码", local.Pages, incoming.Pages);
        AddScalar(conflicts, "language", "语言", local.Language, incoming.Language);
        AddScalar(conflicts, "status", "状态", local.Status, incoming.Status);
        AddScalar(conflicts, "note", "附注", local.Note, incoming.Note);
        AddScalar(conflicts, "abstract", "摘要", local.Abstract, incoming.AbstractText);

        if (incoming.Creators.Count > 0)
        {
            string localCreators = SerializeCreators(local.Creators);
            string incomingCreators = SerializeCreators(incoming.Creators);
            if (!string.Equals(localCreators, incomingCreators, StringComparison.Ordinal))
            {
                conflicts.Add(new BiblatexFieldConflict("creators", "责任者", localCreators, incomingCreators));
            }
        }

        if (incoming.Dates.Count > 0)
        {
            string localDates = SerializeDates(local.Dates);
            string incomingDates = SerializeDates(incoming.Dates);
            if (!string.Equals(localDates, incomingDates, StringComparison.Ordinal))
            {
                conflicts.Add(new BiblatexFieldConflict("dates", "日期", localDates, incomingDates));
            }
        }

        if (incoming.Tags.Count > 0)
        {
            IReadOnlyList<string> localTags = ParseTags(local.TagsJson);
            if (!TagSetsEqual(localTags, incoming.Tags))
            {
                conflicts.Add(new BiblatexFieldConflict(
                    "tags",
                    "标签",
                    JsonSerializer.Serialize(localTags),
                    JsonSerializer.Serialize(incoming.Tags)));
            }
        }

        if (incoming.Identifiers.Count > 0)
        {
            string localIds = SerializeIdentifiers(local.Identifiers);
            string incomingIds = SerializeIdentifiers(incoming.Identifiers);
            if (!string.Equals(localIds, incomingIds, StringComparison.Ordinal))
            {
                conflicts.Add(new BiblatexFieldConflict("identifiers", "标识符", localIds, incomingIds));
            }
        }

        return conflicts;
    }

    public static IReadOnlyList<string> MergeTags(IEnumerable<string> local, IEnumerable<string> incoming)
    {
        List<string> merged = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string tag in local.Concat(incoming).Select(static value => value.Trim())
                     .Where(static value => value.Length > 0))
        {
            if (seen.Add(tag))
            {
                merged.Add(tag);
            }
        }

        return merged;
    }

    public static IReadOnlyList<ItemIdentifierInput> MergeIdentifiers(
        IEnumerable<ItemIdentifier> local,
        IEnumerable<ItemIdentifierInput> incoming)
    {
        List<ItemIdentifierInput> merged = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (ItemIdentifierInput identifier in local
                     .Select(static value => new ItemIdentifierInput(value.Scheme, value.Value, value.Note))
                     .Concat(incoming))
        {
            string scheme = identifier.Scheme.Trim().ToLowerInvariant();
            string value = identifier.Value.Trim();
            if (scheme.Length == 0 || value.Length == 0)
            {
                continue;
            }

            string key = scheme + "\u001f" + value;
            if (seen.Add(key))
            {
                merged.Add(new ItemIdentifierInput(scheme, value, identifier.Note));
            }
        }

        return merged;
    }

    private static void AddScalar(
        List<BiblatexFieldConflict> conflicts,
        string key,
        string label,
        string? localValue,
        string? incomingValue)
    {
        string? incoming = BiblatexFieldMapper.ExactTrim(incomingValue);
        if (incoming is null)
        {
            return;
        }

        string? local = BiblatexFieldMapper.ExactTrim(localValue);
        if (string.Equals(local, incoming, StringComparison.Ordinal))
        {
            return;
        }

        conflicts.Add(new BiblatexFieldConflict(key, label, local, incoming));
    }

    private static string SerializeCreators(IEnumerable<ItemCreatorInput> creators)
    {
        return JsonSerializer.Serialize(creators.Select(static creator => new
        {
            role = creator.Role,
            family = creator.Family,
            given = creator.Given,
            literal = creator.Literal,
            suffix = creator.Suffix,
            particles = creator.Particles
        }));
    }

    private static string SerializeCreators(IEnumerable<ItemCreator> creators)
    {
        return SerializeCreators(creators.Select(static creator => new ItemCreatorInput(
            creator.Role,
            creator.Family,
            creator.Given,
            creator.Literal,
            creator.Suffix,
            creator.Particles)));
    }

    private static string SerializeDates(IEnumerable<ItemDateInput> dates)
    {
        return JsonSerializer.Serialize(dates
            .OrderBy(static date => date.Role, StringComparer.Ordinal)
            .Select(static date => new
            {
                role = date.Role,
                date_parts_json = date.DatePartsJson,
                circa = date.Circa,
                season = date.Season,
                literal = date.Literal
            }));
    }

    private static string SerializeDates(IEnumerable<ItemDate> dates)
    {
        return SerializeDates(dates.Select(static date => new ItemDateInput(
            date.Role,
            date.DatePartsJson,
            date.Circa,
            date.Season,
            date.Literal)));
    }

    private static string SerializeIdentifiers(IEnumerable<ItemIdentifierInput> identifiers)
    {
        return JsonSerializer.Serialize(identifiers
            .Select(static identifier => new
            {
                scheme = identifier.Scheme.Trim().ToLowerInvariant(),
                value = identifier.Value.Trim()
            })
            .OrderBy(static identifier => identifier.scheme, StringComparer.Ordinal)
            .ThenBy(static identifier => identifier.value, StringComparer.Ordinal));
    }

    private static string SerializeIdentifiers(IEnumerable<ItemIdentifier> identifiers)
    {
        return SerializeIdentifiers(identifiers.Select(static identifier =>
            new ItemIdentifierInput(identifier.Scheme, identifier.Value, identifier.Note)));
    }

    private static IReadOnlyList<string> ParseTags(string tagsJson)
    {
        try
        {
            string[]? values = JsonSerializer.Deserialize<string[]>(tagsJson);
            return values?
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

    private static bool TagSetsEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        HashSet<string> leftSet = new(left, StringComparer.Ordinal);
        return right.All(leftSet.Contains) && left.All(static _ => true) && leftSet.Count == right.Count &&
               right.All(leftSet.Contains);
    }
}
