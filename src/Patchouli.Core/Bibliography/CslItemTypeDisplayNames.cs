namespace Patchouli.Core.Bibliography;

/// <summary>Single source of truth mapping English CSL item-type keys to their Chinese
/// display names. Storage and APIs always use the English keys; the Chinese names are
/// shown wherever a type is user-visible.</summary>
public static class CslItemTypeDisplayNames
{
    public static readonly IReadOnlyDictionary<string, string> Names =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["general"] = "通用",
            ["article"] = "文章(预印本/工作论文)",
            ["article-journal"] = "期刊论文",
            ["article-magazine"] = "杂志文章",
            ["article-newspaper"] = "报纸文章",
            ["bill"] = "法案",
            ["book"] = "图书",
            ["broadcast"] = "广播电视节目",
            ["chapter"] = "章节",
            ["classic"] = "古典作品",
            ["collection"] = "档案馆藏",
            ["dataset"] = "数据集",
            ["document"] = "文档",
            ["entry"] = "条目",
            ["entry-dictionary"] = "词典条目",
            ["entry-encyclopedia"] = "百科条目",
            ["event"] = "事件",
            ["figure"] = "图",
            ["graphic"] = "图像作品",
            ["hearing"] = "听证会",
            ["interview"] = "访谈",
            ["legal_case"] = "判例",
            ["legislation"] = "法律",
            ["manuscript"] = "手稿",
            ["map"] = "地图",
            ["motion_picture"] = "电影/视频",
            ["musical_score"] = "乐谱",
            ["pamphlet"] = "小册子",
            ["paper-conference"] = "会议论文",
            ["patent"] = "专利",
            ["performance"] = "现场表演",
            ["periodical"] = "期刊(整期)",
            ["personal_communication"] = "私人通信",
            ["post"] = "帖子",
            ["post-weblog"] = "博客文章",
            ["regulation"] = "行政规章",
            ["report"] = "报告",
            ["review"] = "评论",
            ["review-book"] = "书评",
            ["software"] = "软件",
            ["song"] = "曲目",
            ["speech"] = "演讲",
            ["standard"] = "标准",
            ["thesis"] = "学位论文",
            ["treaty"] = "条约",
            ["webpage"] = "网页"
        };

    /// <summary>Chinese display name for an English CSL item-type key; falls back to the raw key.</summary>
    public static string For(string? itemType)
    {
        return itemType is not null && Names.TryGetValue(itemType, out string? name) ? name : itemType ?? "";
    }
}
