using LiteratureApp.Core.Ids;

namespace LiteratureApp.Core.Layout;

public sealed record LayoutNode(
    LayoutNodeId NodeId,
    DocumentInstanceId DocumentInstanceId,
    PageId PageId,
    LayoutNodeId? ParentNodeId,
    string NodeType,
    NormalizedBBox? BBox,
    string? OwnText,
    string TextPolicy,
    int ReadingOrder,
    string Source,
    LayoutRevisionId RevisionId,
    double? Confidence,
    bool Ignored);
