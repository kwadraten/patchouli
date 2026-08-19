using Patchouli.Core.Ids;

namespace Patchouli.Ocr;

/// <summary>
/// Raised exactly once after an OCR candidate commit succeeds and all
/// protocol-visible successor state (current revision pointer, search dirty state) has been
/// persisted. It is never raised for working, cancelled, or failed runs, and carries only the
/// identities needed to locate the affected resources.
/// </summary>
public sealed class OcrCommitCompletedEventArgs : EventArgs
{
    public OcrCommitCompletedEventArgs(
        DocumentInstanceId documentInstanceId,
        OcrRunId ocrRunId,
        IReadOnlyList<DocumentTreeRevisionId> committedRevisionIds)
    {
        DocumentInstanceId = documentInstanceId;
        OcrRunId = ocrRunId;
        CommittedRevisionIds = committedRevisionIds;
    }

    public DocumentInstanceId DocumentInstanceId { get; }

    public OcrRunId OcrRunId { get; }

    public IReadOnlyList<DocumentTreeRevisionId> CommittedRevisionIds { get; }
}
