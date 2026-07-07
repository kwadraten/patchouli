using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Core.Layout;

public interface ILayoutTreeService
{
    Task<Result<LayoutRevision>> CreateLayoutRevisionAsync(
        DocumentInstanceId documentInstanceId,
        string source,
        bool makeCurrent = false,
        LayoutRevisionId? parentRevisionId = null,
        CancellationToken cancellationToken = default);

    Task<Result<LayoutRevision>> GetCurrentRevisionAsync(
        DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default);

    Task<Result> SetCurrentRevisionAsync(
        DocumentInstanceId documentInstanceId,
        LayoutRevisionId revisionId,
        CancellationToken cancellationToken = default);

    Task<Result<LayoutNode>> AddNodeAsync(
        LayoutRevisionId revisionId,
        PageId pageId,
        LayoutNodeId? parentNodeId,
        string nodeType,
        NormalizedBBox? bbox,
        string? ownText,
        string textPolicy,
        int readingOrder,
        string source,
        double? confidence = null,
        bool ignored = false,
        int? rowIndex = null,
        int? colIndex = null,
        int? rowSpan = null,
        int? colSpan = null,
        bool isHeader = false,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<LayoutNode>>> ListNodesForPageAsync(
        PageId pageId,
        LayoutRevisionId revisionId,
        CancellationToken cancellationToken = default);

    Task<Result> UpdateNodeTextAsync(
        LayoutNodeId nodeId,
        string? ownText,
        CancellationToken cancellationToken = default);

    Task<Result> UpdateNodeTypeAsync(
        LayoutNodeId nodeId,
        string nodeType,
        CancellationToken cancellationToken = default);

    Task<Result> UpdateNodeBBoxAsync(
        LayoutNodeId nodeId,
        NormalizedBBox? bbox,
        CancellationToken cancellationToken = default);

    Task<Result> UpdateTableCellMetadataAsync(
        LayoutNodeId nodeId,
        int? rowIndex,
        int? colIndex,
        int? rowSpan,
        int? colSpan,
        bool isHeader,
        CancellationToken cancellationToken = default);

    Task<Result<LayoutNode>> SplitNodeTextAsync(
        LayoutNodeId nodeId,
        int splitOffset,
        CancellationToken cancellationToken = default);

    Task<Result<LayoutNode>> MergeTextNodesAsync(
        LayoutNodeId firstNodeId,
        LayoutNodeId secondNodeId,
        CancellationToken cancellationToken = default);

    Task<Result<LayoutNode>> CreateParentForNodesAsync(
        IReadOnlyList<LayoutNodeId> childNodeIds,
        string nodeType,
        string textPolicy,
        int readingOrder,
        NormalizedBBox? bbox = null,
        CancellationToken cancellationToken = default);

    Task<Result> MoveNodeAsync(
        LayoutNodeId nodeId,
        LayoutNodeId? newParentNodeId,
        int newReadingOrder,
        CancellationToken cancellationToken = default);

    Task<Result> MarkIgnoredAsync(
        LayoutNodeId nodeId,
        bool ignored,
        CancellationToken cancellationToken = default);

    Task<Result<PlainTextPage>> BuildPagePlainTextAsync(
        PageId pageId,
        LayoutRevisionId revisionId,
        CancellationToken cancellationToken = default);
}
