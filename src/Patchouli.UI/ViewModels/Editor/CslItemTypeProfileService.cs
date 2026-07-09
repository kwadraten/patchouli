using System.Collections.Generic;
using System.Linq;

namespace Patchouli.UI.ViewModels.Editor;

public sealed record ItemFieldDefinition(string Key, string Label, string Type);

public static class CslItemTypeProfileService
{
    private static readonly Dictionary<string, List<ItemFieldDefinition>> Profiles = new()
    {
        {
            "book", new List<ItemFieldDefinition>
            {
                new("Title", "标题", "String"),
                new("Creators", "作者/贡献者", "CreatorList"),
                new("IssuedDate", "出版日期", "Date"),
                new("Publisher", "出版社", "String"),
                new("Place", "出版地", "String"),
                new("Volume", "卷", "String"),
                new("Language", "语言", "String"),
                new("AbstractText", "摘要", "MultilineString"),
                new("TagsText", "标签 (逗号分隔)", "String")
            }
        },
        {
            "article-journal", new List<ItemFieldDefinition>
            {
                new("Title", "标题", "String"),
                new("Creators", "作者/贡献者", "CreatorList"),
                new("PublicationTitle", "期刊名称", "String"),
                new("Volume", "卷", "String"),
                new("Issue", "期", "String"),
                new("Pages", "页码", "String"),
                new("IssuedDate", "发表日期", "Date"),
                new("Language", "语言", "String"),
                new("AbstractText", "摘要", "MultilineString"),
                new("TagsText", "标签 (逗号分隔)", "String")
            }
        },
        {
            "thesis", new List<ItemFieldDefinition>
            {
                new("Title", "标题", "String"),
                new("Creators", "作者/贡献者", "CreatorList"),
                new("Publisher", "颁发机构/学校", "String"),
                new("IssuedDate", "日期", "Date"),
                new("Language", "语言", "String"),
                new("AbstractText", "摘要", "MultilineString"),
                new("TagsText", "标签 (逗号分隔)", "String")
            }
        },
        {
            "general", new List<ItemFieldDefinition>
            {
                new("Title", "标题", "String"),
                new("Creators", "作者/贡献者", "CreatorList"),
                new("IssuedDate", "日期", "Date"),
                new("Publisher", "出版社/机构", "String"),
                new("PublicationTitle", "期刊/出处", "String"),
                new("Place", "地点", "String"),
                new("Volume", "卷", "String"),
                new("Issue", "期", "String"),
                new("Pages", "页码", "String"),
                new("Language", "语言", "String"),
                new("AbstractText", "摘要", "MultilineString"),
                new("TagsText", "标签 (逗号分隔)", "String")
            }
        }
    };

    public static IReadOnlyList<ItemFieldDefinition> GetProfile(string itemType)
    {
        if (Profiles.TryGetValue(itemType, out var fields))
            return fields;
        
        // Fallback to a generous default
        return Profiles["book"];
    }
}
