using Patchouli.Core.Ids;

namespace Patchouli.Core.Layout;

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
    bool Ignored,
    int? RowIndex = null,
    int? ColIndex = null,
    int? RowSpan = null,
    int? ColSpan = null,
    bool IsHeader = false);
