using System.Collections.Generic;
using System.Linq;
using Patchouli.Core.Bibliography;

namespace Patchouli.UI.ViewModels.Editor;

public sealed record ItemFieldDefinition(string Key, string Label, string Type);

public static class CslItemTypeProfileService
{
    private static readonly Dictionary<string, ItemFieldDefinition> KnownFields = new(StringComparer.Ordinal)
    {
        ["title"] = new("Title", "标题", "String"),
        ["author"] = new("Creators", "作者/贡献者", "CreatorList"),
        ["editor"] = new("Creators", "作者/贡献者", "CreatorList"),
        ["issued"] = new("IssuedDate", "日期", "Date"),
        ["publisher"] = new("Publisher", "出版社/机构", "String"),
        ["container-title"] = new("PublicationTitle", "期刊/出处", "String"),
        ["publication-title"] = new("PublicationTitle", "期刊/出处", "String"),
        ["volume"] = new("Volume", "卷", "String"),
        ["issue"] = new("Issue", "期", "String"),
        ["pages"] = new("Pages", "页码", "String"),
        ["language"] = new("Language", "语言", "String"),
        ["abstract"] = new("AbstractText", "摘要", "MultilineString"),
        ["ISBN"] = new("IdentifierInput", "ISBN", "String"),
        ["DOI"] = new("IdentifierInput", "DOI", "String"),
        ["URL"] = new("IdentifierInput", "URL", "String"),
        ["extra_csl"] = new("ExtraCsl", "更多 CSL 字段", "MultilineString")
    };

    private static readonly ItemFieldDefinition[] AlwaysVisible =
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
        new("TagsText", "标签 (逗号分隔)", "String"),
        new("ExtraCsl", "更多 CSL 字段", "MultilineString")
    ];

    public static IReadOnlyList<ItemFieldDefinition> GetProfile(CslItemTypeProfile? profile)
    {
        if (profile is null || profile.ItemType == "general") return AlwaysVisible;

        var keys = profile.PrimaryFields.Concat(profile.RecommendedFields).Concat(profile.AdvancedFields);
        return keys.Select(field => KnownFields.GetValueOrDefault(field)).Where(field => field is not null).Cast<ItemFieldDefinition>().Concat(AlwaysVisible).DistinctBy(field => field.Key).ToArray();
    }
}
