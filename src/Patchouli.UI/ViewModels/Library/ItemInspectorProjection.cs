using System.Text.Json;
using Patchouli.Core.Bibliography;
using Patchouli.UI.ViewModels.Editor;

namespace Patchouli.UI.ViewModels;

/// <summary>
/// A single label/value pair in the item inspector, without any view-model concerns.
/// </summary>
public sealed record InspectorFieldProjection(string Label, string Value, bool WrapText);

/// <summary>
/// A collapsible group of inspector fields, without any view-model concerns.
/// </summary>
public sealed record InspectorGroupProjection(string Title, IReadOnlyList<InspectorFieldProjection> Fields);

/// <summary>
/// The display projection of one <see cref="ItemMetadata"/>: ordered field groups and
/// ordered tag names. Section ordering stays with the view model because it interleaves
/// live section view models between the groups.
/// </summary>
public sealed record ItemInspectorProjectionModel(
    IReadOnlyList<InspectorGroupProjection> Groups,
    IReadOnlyList<string> Tags);

/// <summary>
/// Pure projection of <see cref="ItemMetadata"/> into inspector display data. The view model
/// owns service access and view-model construction; every label fallback, field filter,
/// grouping and ordering decision lives here so it can be tested without Avalonia or services.
/// </summary>
public static class ItemInspectorProjection
{
    private static readonly IReadOnlyDictionary<string, string> EmptyFieldLabels =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static readonly HashSet<string> BasicCustomKeys = new(StringComparer.Ordinal)
    {
        "archive", "archive_location", "archive-place", "archive_collection", "event-title", "event-place"
    };

    public static ItemInspectorProjectionModel Project(
        ItemMetadata metadata,
        IReadOnlyDictionary<string, string>? fieldLabels)
    {
        IReadOnlyDictionary<string, string> labels = fieldLabels ?? EmptyFieldLabels;
        Dictionary<string, string> customFields = ParseCustomFields(metadata.CustomFieldsJson);
        List<InspectorGroupProjection> groups = [];

        List<InspectorFieldProjection> basic = [];
        basic.Add(new InspectorFieldProjection("条目类型", CslItemTypeDisplayNames.For(metadata.ItemType), false));
        AddIfPresent(basic, "标题", metadata.Title);
        AddIfPresent(basic, "作者", FormatCreators(metadata.Creators));
        AddIfPresent(basic, "年份", metadata.Date);
        AddIfPresent(basic, "语言", metadata.Language);
        AddIfPresent(basic, "状态", metadata.Status);
        AddIfPresent(basic, LabelFor(labels, "container-title", "期刊/出处"), metadata.PublicationTitle);
        AddIfPresent(basic, LabelFor(labels, "publisher", "出版社/机构"), metadata.Publisher);
        AddIfPresent(basic, "会议名", metadata.CollectionTitle);
        AddIfPresent(basic, "版本", metadata.Edition);
        AddIfPresent(basic, "卷", metadata.Volume);
        AddIfPresent(basic, "期", metadata.Issue);
        AddIfPresent(basic, "页码", metadata.Pages);
        if (metadata.ItemType is not "manuscript" and not "collection")
        {
            AddIfPresent(basic, "出版地", metadata.Place);
        }

        if (customFields.TryGetValue("archive", out string? archive))
        {
            AddIfPresent(basic, LabelFor(labels, "archive", "档案馆"), archive);
        }

        if (customFields.TryGetValue("archive_location", out string? archiveLoc))
        {
            AddIfPresent(basic, LabelFor(labels, "archive_location", "馆藏位置"), archiveLoc);
        }

        if (customFields.TryGetValue("archive-place", out string? archivePlace))
        {
            AddIfPresent(basic, LabelFor(labels, "archive-place", "档案所在地"), archivePlace);
        }

        if (customFields.TryGetValue("archive_collection", out string? archiveCol))
        {
            AddIfPresent(basic, LabelFor(labels, "archive_collection", "档案集合"), archiveCol);
        }

        if (customFields.TryGetValue("event-title", out string? eventTitle))
        {
            AddIfPresent(basic, LabelFor(labels, "event-title", "会议名称"), eventTitle);
        }

        if (customFields.TryGetValue("event-place", out string? eventPlace))
        {
            AddIfPresent(basic, LabelFor(labels, "event-place", "会议地点"), eventPlace);
        }

        if (basic.Count > 0)
        {
            groups.Add(new InspectorGroupProjection("基本信息", basic));
        }

        if (metadata.Identifiers.Count > 0)
        {
            List<InspectorFieldProjection> identifiers = [];
            foreach (ItemIdentifier identifier in metadata.Identifiers.OrderBy(static i => i.Scheme,
                         StringComparer.Ordinal))
            {
                string schemeLabel = IdentifierSchemeLabel(identifier.Scheme, labels, metadata.ItemType);
                identifiers.Add(new InspectorFieldProjection(schemeLabel, identifier.Value, false));
            }

            groups.Add(new InspectorGroupProjection("标识符", identifiers));
        }

        List<InspectorFieldProjection> extended = [];
        foreach ((string key, string value) in customFields.OrderBy(static kvp => kvp.Key, StringComparer.Ordinal))
        {
            if (BasicCustomKeys.Contains(key))
            {
                continue;
            }

            ExtraCslVariableOption? option = ExtraCslVariableCatalog.Find(key);
            string label = labels.TryGetValue(key, out string? customLabel)
                ? customLabel
                : option?.Label ?? (key == "original_biblatex_entry_type" ? "原始 BibLaTeX 类型" : key);
            bool wrap = option?.IsMultiline ?? false;
            AddIfPresent(extended, label, value, wrap);
        }

        if (extended.Count > 0)
        {
            groups.Add(new InspectorGroupProjection("扩展信息", extended));
        }

        List<InspectorFieldProjection> other = [];
        AddIfPresent(other, "副标题", metadata.Subtitle);
        AddIfPresent(other, "短标题", metadata.TitleShort);
        AddIfPresent(other, "体裁", metadata.Genre);
        AddIfPresent(other, "编号", metadata.Number);
        AddIfPresent(other, "章节号", metadata.ChapterNumber);
        AddIfPresent(other, "版本号", metadata.Version);
        AddIfPresent(other, "引文键", metadata.CitationKey);
        AddIfPresent(other, "附注", metadata.Note, true);
        AddIfPresent(other, "摘要", metadata.Abstract, true);
        if (other.Count > 0)
        {
            groups.Add(new InspectorGroupProjection("其他", other));
        }

        IReadOnlyList<string> tags = ParseTags(metadata.TagsJson)
            .OrderBy(static tag => tag, StringComparer.Ordinal)
            .ToArray();
        return new ItemInspectorProjectionModel(groups, tags);
    }

    private static string IdentifierSchemeLabel(
        string scheme,
        IReadOnlyDictionary<string, string> fieldLabels,
        string itemType)
    {
        if (string.Equals(scheme, BuiltInIdentifierSchemes.CallNumber, StringComparison.OrdinalIgnoreCase))
        {
            return fieldLabels.TryGetValue("call-number", out string? customCallNum)
                ? customCallNum
                : itemType is "manuscript" or "collection"
                    ? "档案号"
                    : "索书号";
        }

        if (string.Equals(scheme, BuiltInIdentifierSchemes.ArXiv, StringComparison.OrdinalIgnoreCase))
        {
            return "arXiv";
        }

        return scheme.ToUpperInvariant();
    }

    private static string LabelFor(IReadOnlyDictionary<string, string> fieldLabels, string fieldKey,
        string fallback)
    {
        return fieldLabels.TryGetValue(fieldKey, out string? label) ? label : fallback;
    }

    private static void AddIfPresent(List<InspectorFieldProjection> fields, string label, string? value,
        bool wrap = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        fields.Add(new InspectorFieldProjection(label, value.Trim(), wrap));
    }

    private static string FormatCreators(IReadOnlyList<ItemCreator> creators)
    {
        if (creators.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(", ", creators
            .OrderBy(static creator => creator.SequenceIndex)
            .Select(static creator => creator.DisplayName)
            .Where(static name => !string.IsNullOrWhiteSpace(name)));
    }

    private static IReadOnlyList<string> ParseTags(string tagsJson)
    {
        if (string.IsNullOrWhiteSpace(tagsJson))
        {
            return Array.Empty<string>();
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(tagsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            return document.RootElement.EnumerateArray()
                .Select(element => element.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static Dictionary<string, string> ParseCustomFields(string? json)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
                {
                    string val = prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString() ?? ""
                        : prop.Value.ToString();
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        result[prop.Name] = val.Trim();
                    }
                }
            }
        }
        catch (JsonException)
        {
            return result;
        }

        return result;
    }
}
