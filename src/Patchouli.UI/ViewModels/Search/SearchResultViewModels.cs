using Patchouli.Core.Ids;
using Patchouli.UI.Services;

namespace Patchouli.UI.ViewModels;

public sealed class SearchMatchedUnitViewModel
{
    public SearchMatchedUnitViewModel(
        string unitId,
        string text,
        string nodeType,
        int readingOrder,
        bool isMatch,
        DocumentInstanceId documentInstanceId,
        int pageIndex,
        DocumentBoxId boxId,
        DocumentTreeRevisionId treeRevisionId)
    {
        UnitId = unitId;
        Text = text;
        NodeType = nodeType;
        ReadingOrder = readingOrder;
        IsMatch = isMatch;
        DocumentInstanceId = documentInstanceId;
        PageIndex = pageIndex;
        BoxId = boxId;
        TreeRevisionId = treeRevisionId;
    }

    public string UnitId { get; }
    public string Text { get; }
    public string NodeType { get; }
    public int ReadingOrder { get; }
    public bool IsMatch { get; }
    public DocumentInstanceId DocumentInstanceId { get; }
    public int PageIndex { get; }
    public DocumentBoxId BoxId { get; }
    public DocumentTreeRevisionId TreeRevisionId { get; }

    public string VersionedUri => PatchouliUriNavigationParser.BuildTextPageUri(
        DocumentInstanceId,
        PageIndex,
        TreeRevisionId,
        BoxId);

    public bool HasVersionedUri => true;
}

public sealed class SearchPageResultViewModel
{
    public SearchPageResultViewModel(string itemTitle, string documentInstanceId, string pageId, string? pageLabel,
        int pageIndex, string indexStatus, bool matchedUnitsHasMore,
        IEnumerable<SearchMatchedUnitViewModel> matchedUnits)
    {
        ItemTitle = itemTitle;
        DocumentInstanceId = documentInstanceId;
        PageId = pageId;
        PageLabel = string.IsNullOrWhiteSpace(pageLabel) ? $"第 {pageIndex + 1} 页" : pageLabel;
        PageIndex = pageIndex;
        IndexStatus = indexStatus;
        MatchedUnitsHasMore = matchedUnitsHasMore;
        MatchedUnits = matchedUnits.ToArray();
    }

    public string ItemTitle { get; }
    public string DocumentInstanceId { get; }
    public string PageId { get; }
    public string PageLabel { get; }
    public int PageIndex { get; }
    public string IndexStatus { get; }
    public bool MatchedUnitsHasMore { get; }
    public IReadOnlyList<SearchMatchedUnitViewModel> MatchedUnits { get; }
}
