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
    /// <param name="rotationDegrees">
    /// Clockwise onscreen quarter turn (0/90/180/270) applied to every provider run inside the
    /// edit; persisted boxes stay in the unrotated document basis. Null uses the page's
    /// persisted user rotation.
    /// </param>
    Task<Result<PhysicalPageOcrResult>> RunPageEditAsync(OcrPresetId presetId, PageEditSessionId sessionId,
        CancellationToken cancellationToken = default, int? rotationDegrees = null);

    /// <param name="rotationDegrees">
    /// Clockwise onscreen quarter turn (0/90/180/270). Null uses the page's persisted user
    /// rotation.
    /// </param>
    Task<Result<LogicalPageOcrResult>> RunAsync(
        DocumentInstanceId documentInstanceId,
        OcrPresetId presetId,
        PageId pageId,
        IReadOnlyList<LogicalPageOcrTarget> targets,
        CancellationToken cancellationToken = default,
        int? rotationDegrees = null);

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
