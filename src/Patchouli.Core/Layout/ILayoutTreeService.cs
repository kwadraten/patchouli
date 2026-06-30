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
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<LayoutNode>>> ListNodesForPageAsync(
        PageId pageId,
        LayoutRevisionId revisionId,
        CancellationToken cancellationToken = default);

    Task<Result> UpdateNodeTextAsync(
        LayoutNodeId nodeId,
        string? ownText,
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
