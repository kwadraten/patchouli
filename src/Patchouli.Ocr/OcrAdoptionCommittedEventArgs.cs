using Patchouli.Core.Ids;

namespace Patchouli.Ocr;

/// <summary>
/// Raised exactly once after an OCR candidate adoption commit succeeds and all
/// protocol-visible successor state (current revision pointer, search dirty state) has been
/// persisted. It is never raised for staging, cancelled, or failed runs, and carries only the
/// identities needed to locate the affected resources.
/// </summary>
public sealed class OcrAdoptionCommittedEventArgs : EventArgs
{
    public OcrAdoptionCommittedEventArgs(
        DocumentInstanceId documentInstanceId,
        OcrRunId ocrRunId,
        IReadOnlyList<DocumentTreeRevisionId> adoptedRevisionIds)
    {
        DocumentInstanceId = documentInstanceId;
        OcrRunId = ocrRunId;
        AdoptedRevisionIds = adoptedRevisionIds;
    }

    public DocumentInstanceId DocumentInstanceId { get; }

    public OcrRunId OcrRunId { get; }

    public IReadOnlyList<DocumentTreeRevisionId> AdoptedRevisionIds { get; }
}
