using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;

namespace Patchouli.Ocr;

public sealed record OcrLayoutImportRequest(
    DocumentInstanceId DocumentInstanceId,
    OcrLayoutDocument Document,
    string RevisionSource,
    string NodeSource,
    LayoutRevisionId? ParentRevisionId = null,
    LayoutRevisionId? RevisionId = null,
    bool MakeCurrent = false);

public sealed record OcrLayoutImportResult(
    LayoutRevisionId RevisionId,
    int NodesCreated);

public sealed record OcrLayoutCopyRequest(
    LayoutRevisionId SourceRevisionId,
    LayoutRevisionId TargetRevisionId,
    IReadOnlyList<PageId> PageIds);

public sealed record OcrLayoutCopyResult(int NodesCopied);

public interface IOcrLayoutImporter
{
    Task<Result<OcrLayoutImportResult>> ImportRevisionAsync(
        OcrLayoutImportRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<OcrLayoutCopyResult>> CopyPagesAsync(
        OcrLayoutCopyRequest request,
        CancellationToken cancellationToken = default);
}
