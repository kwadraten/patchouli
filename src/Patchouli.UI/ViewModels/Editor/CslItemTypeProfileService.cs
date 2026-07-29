using System.Collections.Generic;
using System.Linq;
using Patchouli.Core.Bibliography;

namespace Patchouli.UI.ViewModels.Editor;

public sealed record ItemFieldDefinition(
    string Key,
    string Label,
    string Type,
    string? IdentifierScheme = null,
    string? ExtraCslVariable = null);

public sealed record ItemEditorFieldSet(
    IReadOnlyList<ItemFieldDefinition> VisibleFields,
    IReadOnlyList<ItemFieldDefinition> MoreFields);

public static class CslItemTypeProfileService
{
    public const string IdentifierBackedFieldType = "IdentifierBacked";
    public const string ExtraCslBackedFieldType = "ExtraCslBacked";

    private static readonly Dictionary<string, ItemFieldDefinition> KnownFields = new(StringComparer.Ordinal)
    {
        ["title"] = new ItemFieldDefinition("Title", "标题", "String"),
        ["author"] = new ItemFieldDefinition("Creators", "作者/贡献者", "CreatorList"),
        ["editor"] = new ItemFieldDefinition("Creators", "作者/贡献者", "CreatorList"),
        ["issued"] = new ItemFieldDefinition("IssuedDate", "日期", "Date"),
        ["title-short"] = new ItemFieldDefinition("TitleShort", "短标题", "String"),
        ["publisher"] = new ItemFieldDefinition("Publisher", "出版社/机构", "String"),
        ["publisher-place"] = new ItemFieldDefinition("Place", "地点", "String"),
        ["edition"] = new ItemFieldDefinition("Edition", "版次", "String"),
        ["container-title"] = new ItemFieldDefinition("PublicationTitle", "期刊/出处", "String"),
        ["container-title-short"] = new ItemFieldDefinition("ContainerTitleShort", "刊名缩写", "String"),
        ["publication-title"] = new ItemFieldDefinition("PublicationTitle", "期刊/出处", "String"),
        ["volume"] = new ItemFieldDefinition("Volume", "卷", "String"),
        ["issue"] = new ItemFieldDefinition("Issue", "期", "String"),
        ["pages"] = new ItemFieldDefinition("Pages", "页码", "String"),
        ["chapter-number"] = new ItemFieldDefinition("ChapterNumber", "章节号", "String"),
        ["status"] = new ItemFieldDefinition("Status", "状态", "String"),
        ["language"] = new ItemFieldDefinition("Language", "语言", "String"),
        ["abstract"] = new ItemFieldDefinition("AbstractText", "摘要", "MultilineString"),
        ["collection-title"] = new ItemFieldDefinition("CollectionTitle", "丛书/文集", "String"),
        ["genre"] = new ItemFieldDefinition("Genre", "体裁/类型", "String"),
        ["number"] = new ItemFieldDefinition("Number", "编号", "String"),
        ["version"] = new ItemFieldDefinition("Version", "版本", "String"),
        ["accessed"] = new ItemFieldDefinition("AccessedDate", "访问日期", "Date"),
        ["original-date"] = new ItemFieldDefinition("OriginalDate", "原始日期", "Date"),
        ["event-date"] = new ItemFieldDefinition("EventDate", "事件日期", "Date"),
        ["submitted"] = new ItemFieldDefinition("SubmittedDate", "提交日期", "Date")
    };

    private static readonly ItemFieldDefinition[] GeneralVisibleFields =
    [
        new("Title", "标题", "String"),
        new("Creators", "作者/贡献者", "CreatorList"),
        new("IssuedDate", "日期", "Date"),
        new("PublicationTitle", "期刊/出处", "String"),
        new("Publisher", "出版社/机构", "String"),
        new("Place", "地点", "String"),
        new("Volume", "卷", "String"),
        new("Issue", "期", "String"),
        new("Pages", "页码", "String"),
        new("Language", "语言", "String"),
        new("AbstractText", "摘要", "MultilineString"),
        new("TagsText", "标签 (逗号分隔)", "String")
    ];

    public static ItemEditorFieldSet GetProfile(CslItemTypeProfile? profile)
    {
        if (profile is null || profile.ItemType == "general")
        {
            // Every type recommends a URL, including the general fallback type.
            ItemFieldDefinition urlProjection = new(
                ProjectionKey(BuiltInIdentifierSchemes.URL),
                "链接 URL",
                IdentifierBackedFieldType,
                BuiltInIdentifierSchemes.URL);
            ItemFieldDefinition[] generalVisible = GeneralVisibleFields
                .Take(GeneralVisibleFields.Length - 2)
                .Append(urlProjection)
                .Concat(GeneralVisibleFields.TakeLast(2))
                .ToArray();
            return new ItemEditorFieldSet(generalVisible, []);
        }

        List<ItemFieldDefinition> projections = profile.IdentifierSchemes
            .Where(static scheme => !string.IsNullOrWhiteSpace(scheme))
            .Select(static scheme => scheme.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Select(scheme => CreateProjectionField(profile, scheme))
            .Where(field => field is not null)
            .Cast<ItemFieldDefinition>()
            .ToList();

        IEnumerable<ItemFieldDefinition> typeFields = profile.PrimaryFields.Concat(profile.RecommendedFields)
            .Select(field => ResolveField(profile, field))
            .Where(field => field is not null)
            .Cast<ItemFieldDefinition>();

        List<ItemFieldDefinition> visible = [];
        if (profile.ItemType == "webpage")
        {
            // The URL is the primary locator of a web page, so it takes the first position.
            visible.AddRange(projections);
        }

        visible.Add(new ItemFieldDefinition("Title", "标题", "String"));
        visible.Add(new ItemFieldDefinition("Creators", "作者/贡献者", "CreatorList"));
        visible.Add(new ItemFieldDefinition("IssuedDate", "日期", "Date"));
        visible.AddRange(typeFields);
        if (profile.ItemType != "webpage")
        {
            visible.AddRange(projections);
        }

        visible.Add(new ItemFieldDefinition("AbstractText", "摘要", "MultilineString"));
        visible.Add(new ItemFieldDefinition("TagsText", "标签 (逗号分隔)", "String"));

        ItemFieldDefinition[] visibleDistinct = visible.DistinctBy(field => field.Key).ToArray();
        HashSet<string> visibleKeys = new(visibleDistinct.Select(field => field.Key), StringComparer.Ordinal);

        ItemFieldDefinition[] more = profile.AdvancedFields
            .Concat(profile.HiddenByDefaultFields)
            .Concat(KnownFields.Keys)
            .Distinct(StringComparer.Ordinal)
            .Select(field => ResolveField(profile, field))
            .Where(field => field is not null)
            .Cast<ItemFieldDefinition>()
            .Where(field => !visibleKeys.Contains(field.Key))
            .DistinctBy(field => field.Key)
            .ToArray();

        return new ItemEditorFieldSet(visibleDistinct, more);
    }

    /// <summary>Type-aware localized display name for an identifier scheme, shared by the
    /// identifier-backed projection fields and the identifier card shortcut buttons.</summary>
    public static string GetIdentifierSchemeLabel(CslItemTypeProfile? profile, string scheme)
    {
        return scheme switch
        {
            BuiltInIdentifierSchemes.DOI => "DOI",
            BuiltInIdentifierSchemes.ISBN => "ISBN",
            BuiltInIdentifierSchemes.ISSN => "ISSN",
            BuiltInIdentifierSchemes.URL => "链接",
            BuiltInIdentifierSchemes.CallNumber => profile is not null &&
                                                   profile.FieldLabels.TryGetValue("call-number", out string? label)
                ? label
                : "索书号",
            _ => scheme
        };
    }

    public static string ProjectionKey(string scheme)
    {
        return $"Identifier:{scheme}";
    }

    public static string ExtraCslProjectionKey(string variable)
    {
        return $"ExtraCsl:{variable}";
    }

    private static ItemFieldDefinition? CreateProjectionField(CslItemTypeProfile profile, string scheme)
    {
        return scheme switch
        {
            BuiltInIdentifierSchemes.URL => new ItemFieldDefinition(
                ProjectionKey(scheme), "链接 URL", IdentifierBackedFieldType, scheme),
            BuiltInIdentifierSchemes.CallNumber => new ItemFieldDefinition(
                ProjectionKey(scheme), GetIdentifierSchemeLabel(profile, scheme), IdentifierBackedFieldType, scheme),
            _ => null
        };
    }

    private static ItemFieldDefinition? ResolveField(CslItemTypeProfile profile, string cslField)
    {
        if (KnownFields.TryGetValue(cslField, out ItemFieldDefinition? field))
        {
            return profile.FieldLabels.TryGetValue(cslField, out string? label)
                ? field with { Label = label }
                : field;
        }

        // Fields without dedicated storage are projected onto the structured extra-CSL rows.
        ExtraCslVariableOption? option = ExtraCslVariableCatalog.Find(cslField);
        if (option is null)
        {
            return null;
        }

        string resolvedLabel = profile.FieldLabels.TryGetValue(cslField, out string? overrideLabel)
            ? overrideLabel
            : option.Label;
        return new ItemFieldDefinition(
            ExtraCslProjectionKey(option.Key),
            resolvedLabel,
            ExtraCslBackedFieldType,
            ExtraCslVariable: option.Key);
    }
}
