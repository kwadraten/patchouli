using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Core.Documents;

/// <summary>
/// Working-copy and immutable revision service for document page box trees.
/// All mutations observe the in-place commit rule: a working revision is promoted to
/// committed current by updating status, is_current and committed_at in the same row;
/// the <see cref="DocumentTreeRevisionId"/> and all <see cref="DocumentBoxId"/> values remain
/// stable across promotion. History is append-only; revert creates a new commit rather than
/// moving the current pointer backward.
/// </summary>
public interface IDocumentTreeService
{
    Task<Result> ValidateStoredTreesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new working revision for <paramref name="pageId"/> populated with
    /// <paramref name="boxes"/>. Used by OCR import and by any other bulk creation path.
    /// The revision is not yet current and does not feed search, evidence or MCP reads
    /// until it is committed with <see cref="CommitWorkingRevisionAsync"/>.
    /// </summary>
    Task<Result<DocumentTreeRevision>> BeginWorkingRevisionAsync(
        DocumentInstanceId documentInstanceId,
        PageId pageId,
        IReadOnlyList<DocumentBoxSeed> boxes,
        string source,
        DocumentTreeRevisionId? parentTreeRevisionId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates the boxes of <paramref name="workingRevisionId"/> and promotes it in place
    /// to the current committed revision for its page. If <paramref name="commitId"/> is
    /// provided, the revision is also linked into that document commit via
    /// <c>document_commit_pages</c>. The working revision's <see cref="DocumentTreeRevisionId"/>
    /// becomes a committed revision id and remains valid in versioned URIs.
    /// </summary>
    Task<Result<DocumentTreeRevision>> CommitWorkingRevisionAsync(
        DocumentTreeRevisionId workingRevisionId,
        DocumentCommitId? commitId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new document-wide commit row for <paramref name="documentInstanceId"/>.
    /// The commit's parent is the latest existing document commit for the document, or null
    /// for the first commit. Pages are linked to this commit by passing the returned
    /// <see cref="DocumentCommitId"/> into <see cref="CommitWorkingRevisionAsync"/> as each
    /// page revision is committed. This is the simplest correct shape: create the commit first,
    /// then link page revisions as they are promoted.
    /// </summary>
    Task<Result<DocumentCommit>> CreateDocumentCommitAsync(
        DocumentInstanceId documentInstanceId,
        string source,
        string? message = null,
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

    /// <summary>
    /// Returns every committed revision for <paramref name="pageId"/>, newest first.
    /// Working revisions and legacy status rows are excluded.
    /// </summary>
    Task<Result<IReadOnlyList<DocumentTreeRevision>>> ListRevisionsAsync(
        DocumentInstanceId documentInstanceId,
        PageId pageId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every document-wide commit for <paramref name="documentInstanceId"/>, newest first,
    /// each with the page-to-revision mapping stored in <c>document_commit_pages</c>.
    /// </summary>
    Task<Result<IReadOnlyList<DocumentCommitDetail>>> ListDocumentCommitsAsync(
        DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reverts <paramref name="pageId"/> to <paramref name="targetRevisionId"/> by creating a
    /// new working revision whose boxes are copied from the target revision, then committing it
    /// in place with <see cref="DocumentTreeRevisionSource.Revert"/>,
    /// <see cref="DocumentTreeRevision.RevertedFromTreeRevisionId"/> set to the target, and
    /// parent equal to the previous HEAD revision. Returns the newly created committed revision.
    /// </summary>
    Task<Result<DocumentTreeRevision>> RevertToRevisionAsync(
        DocumentInstanceId documentInstanceId,
        PageId pageId,
        DocumentTreeRevisionId targetRevisionId,
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
