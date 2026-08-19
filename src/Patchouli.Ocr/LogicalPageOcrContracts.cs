using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;

namespace Patchouli.Ocr;

public sealed record LogicalPageOcrTarget(DocumentBoxId LogicalPageBoxId, NormalizedBBox BBox);

public sealed record LogicalPageOcrResult(
    DocumentTreeRevisionId WorkingTreeRevisionId,
    IReadOnlyList<OcrRunId> RegionRunIds);

public sealed record LogicalDocumentOcrPagePlan(
    PageId PageId,
    IReadOnlyList<LogicalPageOcrTarget> LogicalPageTargets);

public sealed record LogicalDocumentOcrResult(
    IReadOnlyList<DocumentTreeRevisionId> WorkingTreeRevisionIds,
    IReadOnlyList<OcrRunId> RunIds);

public sealed record PhysicalPageOcrResult(
    DocumentTreeRevisionId WorkingTreeRevisionId,
    IReadOnlyList<OcrRunId> RunIds,
    bool UsedLogicalPages);

public interface ILogicalPageOcrService
{
    Task<Result<LogicalPageOcrResult>> RunAsync(
        DocumentInstanceId documentInstanceId,
        OcrPresetId presetId,
        PageId pageId,
        IReadOnlyList<LogicalPageOcrTarget> targets,
        CancellationToken cancellationToken = default);

    Task<Result<PhysicalPageOcrResult>> RunPageAsync(
        DocumentInstanceId documentInstanceId,
        OcrPresetId presetId,
        LogicalDocumentOcrPagePlan page,
        CancellationToken cancellationToken = default);

    Task<Result<LogicalDocumentOcrResult>> RunDocumentAsync(
        DocumentInstanceId documentInstanceId,
        OcrPresetId presetId,
        IReadOnlyList<LogicalDocumentOcrPagePlan> pages,
        CancellationToken cancellationToken = default);
}
