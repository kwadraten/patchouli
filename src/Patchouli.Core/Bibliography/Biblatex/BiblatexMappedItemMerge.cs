using System.Text.Json;

namespace Patchouli.Core.Bibliography.Biblatex;

public static class BiblatexMappedItemMerge
{
    public const string ChoiceLocal = "local";
    public const string ChoiceIncoming = "incoming";

    public static CreateItemRequest ToCreateRequest(BiblatexMappedItem source)
    {
        return new CreateItemRequest(
            source.ItemType,
            source.Title,
            source.Subtitle,
            source.TitleShort,
            PublicationTitle: source.PublicationTitle,
            ContainerTitleShort: source.ContainerTitleShort,
            CollectionTitle: source.CollectionTitle,
            Publisher: source.Publisher,
            Place: source.Place,
            Edition: source.Edition,
            Genre: source.Genre,
            Number: source.Number,
            ChapterNumber: source.ChapterNumber,
            Volume: source.Volume,
            Version: source.Version,
            Issue: source.Issue,
            Pages: source.Pages,
            Language: source.Language,
            Status: source.Status,
            Note: source.Note,
            AbstractText: source.AbstractText,
            TagsJson: JsonSerializer.Serialize(source.Tags),
            CustomFieldsJson: BuildCustomFields(source),
            Creators: source.Creators,
            Dates: source.Dates,
            Identifiers: source.Identifiers);
    }

    public static UpdateItemRequest ToAcceptedUpdateRequest(
        ItemMetadata local,
        BiblatexMappedItem incoming,
        out IReadOnlyList<ItemIdentifierInput> identifiersAfter)
    {
        return BuildUpdate(
            local,
            incoming,
            true,
            null,
            out identifiersAfter);
    }

    public static UpdateItemRequest ToFieldChoiceUpdateRequest(
        ItemMetadata local,
        BiblatexMappedItem incoming,
        IReadOnlyDictionary<string, string> fieldChoices,
        out IReadOnlyList<ItemIdentifierInput> identifiersAfter)
    {
        return BuildUpdate(local, incoming, false, fieldChoices, out identifiersAfter);
    }

    public static bool ShouldAdopt(
        string fieldKey,
        IReadOnlyDictionary<string, string>? fieldChoices,
        bool adoptAllProvided)
    {
        if (adoptAllProvided)
        {
            return true;
        }

        return fieldChoices is not null &&
               fieldChoices.TryGetValue(fieldKey, out string? choice) &&
               string.Equals(choice, ChoiceIncoming, StringComparison.Ordinal);
    }

    private static UpdateItemRequest BuildUpdate(
        ItemMetadata local,
        BiblatexMappedItem incoming,
        bool adoptAllProvided,
        IReadOnlyDictionary<string, string>? fieldChoices,
        out IReadOnlyList<ItemIdentifierInput> identifiersAfter)
    {
        bool Adopt(string key)
        {
            return ShouldAdopt(key, fieldChoices, adoptAllProvided);
        }

        string itemType = Adopt("item_type") ? incoming.ItemType : local.ItemType;
        string title = Adopt("title") ? incoming.Title : local.Title;
        string? subtitle = ChooseScalar(local.Subtitle, incoming.Subtitle, Adopt("subtitle"), adoptAllProvided);
        string? titleShort =
            ChooseScalar(local.TitleShort, incoming.TitleShort, Adopt("title_short"), adoptAllProvided);
        string? publicationTitle = ChooseScalar(local.PublicationTitle, incoming.PublicationTitle,
            Adopt("publication_title"), adoptAllProvided);
        string? containerTitleShort = ChooseScalar(local.ContainerTitleShort, incoming.ContainerTitleShort,
            Adopt("container_title_short"), adoptAllProvided);
        string? collectionTitle = ChooseScalar(local.CollectionTitle, incoming.CollectionTitle,
            Adopt("collection_title"), adoptAllProvided);
        string? publisher = ChooseScalar(local.Publisher, incoming.Publisher, Adopt("publisher"), adoptAllProvided);
        string? place = ChooseScalar(local.Place, incoming.Place, Adopt("place"), adoptAllProvided);
        string? edition = ChooseScalar(local.Edition, incoming.Edition, Adopt("edition"), adoptAllProvided);
        string? genre = ChooseScalar(local.Genre, incoming.Genre, Adopt("genre"), adoptAllProvided);
        string? number = ChooseScalar(local.Number, incoming.Number, Adopt("number"), adoptAllProvided);
        string? chapterNumber = ChooseScalar(local.ChapterNumber, incoming.ChapterNumber, Adopt("chapter_number"),
            adoptAllProvided);
        string? volume = ChooseScalar(local.Volume, incoming.Volume, Adopt("volume"), adoptAllProvided);
        string? version = ChooseScalar(local.Version, incoming.Version, Adopt("version"), adoptAllProvided);
        string? issue = ChooseScalar(local.Issue, incoming.Issue, Adopt("issue"), adoptAllProvided);
        string? pages = ChooseScalar(local.Pages, incoming.Pages, Adopt("pages"), adoptAllProvided);
        string? language = ChooseScalar(local.Language, incoming.Language, Adopt("language"), adoptAllProvided);
        string? status = ChooseScalar(local.Status, incoming.Status, Adopt("status"), adoptAllProvided);
        string? note = ChooseScalar(local.Note, incoming.Note, Adopt("note"), adoptAllProvided);
        string? abstractText = ChooseScalar(local.Abstract, incoming.AbstractText, Adopt("abstract"), adoptAllProvided);

        IReadOnlyList<ItemCreatorInput> creators = Adopt("creators") && incoming.Creators.Count > 0
            ? incoming.Creators
            : local.Creators.Select(static creator => new ItemCreatorInput(
                creator.Role, creator.Family, creator.Given, creator.Literal, creator.Suffix,
                creator.Particles)).ToArray();

        IReadOnlyList<ItemDateInput> dates = Adopt("dates") && incoming.Dates.Count > 0
            ? incoming.Dates
            : local.Dates.Select(static date => new ItemDateInput(
                date.Role, date.DatePartsJson, date.Circa, date.Season, date.Literal)).ToArray();

        IReadOnlyList<string> localTags = ParseTags(local.TagsJson);
        IReadOnlyList<string> tags = Adopt("tags") && incoming.Tags.Count > 0
            ? BiblatexFieldConflictAnalyzer.MergeTags(localTags, incoming.Tags)
            : localTags;

        identifiersAfter = Adopt("identifiers") && incoming.Identifiers.Count > 0
            ? BiblatexFieldConflictAnalyzer.MergeIdentifiers(local.Identifiers, incoming.Identifiers)
            : local.Identifiers.Select(static identifier =>
                new ItemIdentifierInput(identifier.Scheme, identifier.Value, identifier.Note)).ToArray();

        string? customFields = local.CustomFieldsJson;
        if (!string.IsNullOrWhiteSpace(incoming.OriginalBiblatexEntryType))
        {
            customFields = MergeOriginalType(customFields, incoming.OriginalBiblatexEntryType);
        }

        return new UpdateItemRequest(
            itemType,
            title,
            subtitle,
            titleShort,
            PublicationTitle: publicationTitle,
            ContainerTitleShort: containerTitleShort,
            CollectionTitle: collectionTitle,
            Publisher: publisher,
            Place: place,
            Edition: edition,
            Genre: genre,
            Number: number,
            ChapterNumber: chapterNumber,
            Volume: volume,
            Version: version,
            Issue: issue,
            Pages: pages,
            Language: language,
            Status: status,
            Note: note,
            AbstractText: abstractText,
            TagsJson: JsonSerializer.Serialize(tags),
            CollectionsJson: local.CollectionsJson,
            CustomFieldsJson: customFields,
            Creators: creators,
            Dates: dates,
            ExpectedUpdatedAt: local.UpdatedAt);
    }

    private static string? ChooseScalar(
        string? local,
        string? incoming,
        bool adopt,
        bool adoptAllProvided)
    {
        string? trimmedIncoming = BiblatexFieldMapper.ExactTrim(incoming);
        if (trimmedIncoming is null)
        {
            return local;
        }

        if (adopt || adoptAllProvided)
        {
            return trimmedIncoming;
        }

        return local;
    }

    private static string? BuildCustomFields(BiblatexMappedItem source)
    {
        if (string.IsNullOrWhiteSpace(source.OriginalBiblatexEntryType))
        {
            return null;
        }

        return JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["original_biblatex_entry_type"] = source.OriginalBiblatexEntryType
        });
    }

    private static string MergeOriginalType(string? existingJson, string originalType)
    {
        Dictionary<string, JsonElement> map = new(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(existingJson))
        {
            try
            {
                Dictionary<string, JsonElement>? parsed =
                    JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existingJson);
                if (parsed is not null)
                {
                    foreach ((string key, JsonElement value) in parsed)
                    {
                        map[key] = value;
                    }
                }
            }
            catch (JsonException)
            {
                // Replace unreadable custom fields with the retained entry type marker.
            }
        }

        map["original_biblatex_entry_type"] =
            JsonSerializer.SerializeToElement(originalType);
        return JsonSerializer.Serialize(map);
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
}
