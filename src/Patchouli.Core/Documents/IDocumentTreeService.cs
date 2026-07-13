using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Core.Documents;

public interface IDocumentTreeService
{
    Task<Result<DocumentTreeRevision>> CreateStagingRevisionAsync(
        DocumentInstanceId documentInstanceId,
        PageId pageId,
        string source,
        DocumentTreeRevisionId? parentTreeRevisionId = null,
        CancellationToken cancellationToken = default);

    Task<Result<DocumentTreeRevision>> StagePageAsync(
        DocumentInstanceId documentInstanceId,
        PageId pageId,
        IReadOnlyList<DocumentBoxSeed> boxes,
        string source = DocumentTreeRevisionSource.Import,
        DocumentTreeRevisionId? parentTreeRevisionId = null,
        CancellationToken cancellationToken = default);

    Task<Result<PageEditSession>> BeginPageEditAsync(
        DocumentInstanceId documentInstanceId,
        PageId pageId,
        CancellationToken cancellationToken = default);

    Task<Result<DocumentTreeRevision>> GetCurrentRevisionAsync(
        DocumentInstanceId documentInstanceId,
        PageId pageId,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<DocumentBox>>> ListBoxesAsync(
        DocumentTreeRevisionId treeRevisionId,
        CancellationToken cancellationToken = default);

    Task<Result<DocumentTreeRevision>> AdoptStagingRevisionAsync(
        DocumentTreeRevisionId stagingRevisionId,
        CancellationToken cancellationToken = default);

    Task<Result<DocumentTreeRevision>> CommitPageEditAsync(
        PageEditSessionId sessionId,
        CancellationToken cancellationToken = default);

    Task<Result> DiscardPageEditAsync(
        PageEditSessionId sessionId,
        CancellationToken cancellationToken = default);
}

public interface IDocumentTreeEditor
{
    Task<Result<DocumentBox>> InsertLogicalPageAsync(
        PageEditSessionId sessionId,
        DocumentBoxId? insertAfterBoxId,
        Layout.NormalizedBBox bbox,
        CancellationToken cancellationToken = default);

    Task<Result<DocumentBox>> DrawAndInsertLeafAsync(
        PageEditSessionId sessionId,
        InsertLeafCommand command,
        CancellationToken cancellationToken = default);

    Task<Result> UpdateLeafAsync(
        PageEditSessionId sessionId,
        UpdateLeafCommand command,
        CancellationToken cancellationToken = default);

    Task<Result> UpdateBBoxAsync(
        PageEditSessionId sessionId,
        DocumentBoxId boxId,
        Layout.NormalizedBBox bbox,
        CancellationToken cancellationToken = default);

    Task<Result> MoveBoxAsync(
        PageEditSessionId sessionId,
        MoveBoxCommand command,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<DocumentBox>>> SplitLeafAsync(
        PageEditSessionId sessionId,
        SplitLeafCommand command,
        CancellationToken cancellationToken = default);

    Task<Result<DocumentBox>> MergeLeavesAsync(
        PageEditSessionId sessionId,
        MergeLeavesCommand command,
        CancellationToken cancellationToken = default);

    Task<Result> SetSuppressedAsync(
        PageEditSessionId sessionId,
        DocumentBoxId boxId,
        bool suppressed,
        CancellationToken cancellationToken = default);

    Task<Result> DeleteBoxAsync(
        PageEditSessionId sessionId,
        DocumentBoxId boxId,
        CancellationToken cancellationToken = default);

    Task<Result> AcceptLocalOcrCandidateAsync(
        PageEditSessionId sessionId,
        DocumentBoxId boxId,
        LocalOcrCandidate candidate,
        CancellationToken cancellationToken = default);
}
