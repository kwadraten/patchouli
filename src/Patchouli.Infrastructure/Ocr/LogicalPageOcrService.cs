using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Ocr;

namespace Patchouli.Infrastructure.Ocr;

public sealed class LogicalPageOcrService : ILogicalPageOcrService
{
    private readonly IOcrRunCoordinator _ocr;
    private readonly IDocumentTreeService _trees;

    public LogicalPageOcrService(IOcrRunCoordinator ocr, IDocumentTreeService trees)
    {
        _ocr = ocr;
        _trees = trees;
    }

    public async Task<Result<LogicalPageOcrResult>> RunAsync(
        DocumentInstanceId documentInstanceId,
        OcrPresetId presetId,
        PageId pageId,
        IReadOnlyList<LogicalPageOcrTarget> targets,
        CancellationToken cancellationToken = default)
    {
        if (targets.Count == 0 || targets.Select(target => target.LogicalPageBoxId).Distinct().Count() != targets.Count)
        {
            return Result<LogicalPageOcrResult>.Failure(
                AppErrorCodes.ValidationFailed, "Logical-page OCR requires distinct target regions.");
        }

        Result<DocumentTreeRevision> current = await _trees.GetCurrentRevisionAsync(
            documentInstanceId, pageId, cancellationToken);
        if (current.IsFailure)
        {
            return Result<LogicalPageOcrResult>.Failure(current.ErrorCode!, current.ErrorMessage!);
        }

        Result<IReadOnlyList<DocumentBox>> currentBoxes = await _trees.ListBoxesAsync(
            current.Value.TreeRevisionId, cancellationToken);
        DocumentBox[] logicalRoots = currentBoxes.IsSuccess
            ? currentBoxes.Value.Where(box => box.BoxType == DocumentBoxType.LogicalPage).ToArray()
            : [];
        if (currentBoxes.IsFailure ||
            targets.Any(target => logicalRoots.All(root => root.BoxId != target.LogicalPageBoxId)))
        {
            return Result<LogicalPageOcrResult>.Failure(
                AppErrorCodes.ValidationFailed, "Every logical-page OCR target must exist in the current page tree.");
        }

        List<DocumentBoxSeed> seeds = [];
        int rootOrder = 0;
        foreach (DocumentBox root in Order(logicalRoots))
        {
            seeds.Add(new DocumentBoxSeed(root.BoxId, null, rootOrder++, root.BoxType, root.SubType, root.BaseType,
                root.BBox, null));
        }

        List<OcrRunId> runIds = [];
        foreach (LogicalPageOcrTarget target in targets)
        {
            Result<OcrRun> run = await _ocr.RunPresetOnRegionAsync(
                documentInstanceId, presetId, pageId, target.BBox, cancellationToken);
            if (run.IsFailure)
            {
                return Result<LogicalPageOcrResult>.Failure(run.ErrorCode!, run.ErrorMessage!);
            }

            runIds.Add(run.Value.OcrRunId);
            Result<IReadOnlyList<OcrPageResult>> pageResults = await _ocr.ListPageResultsAsync(
                run.Value.OcrRunId, cancellationToken);
            DocumentTreeRevisionId? regionRevision = pageResults.IsSuccess
                ? pageResults.Value.SingleOrDefault(result => result.PageId == pageId)?.StagingTreeRevisionId
                : null;
            if (regionRevision is null)
            {
                return Result<LogicalPageOcrResult>.Failure(
                    AppErrorCodes.InvalidState, "A logical-page OCR region did not produce a staging tree.");
            }

            Result<IReadOnlyList<DocumentBox>> regionBoxes = await _trees.ListBoxesAsync(
                regionRevision.Value, cancellationToken);
            if (regionBoxes.IsFailure)
            {
                return Result<LogicalPageOcrResult>.Failure(regionBoxes.ErrorCode!, regionBoxes.ErrorMessage!);
            }

            int sourceOrder = 0;
            foreach (DocumentBox box in regionBoxes.Value.Where(box => box.BoxType != DocumentBoxType.LogicalPage))
            {
                seeds.Add(new DocumentBoxSeed(null, target.LogicalPageBoxId, sourceOrder++, box.BoxType, box.SubType,
                    box.BaseType, box.BBox, box.Payload, box.HeadingLevel, box.CodeLanguage, box.Confidence,
                    box.Suppressed));
            }
        }

        Result<DocumentTreeRevision> staged = await _trees.StagePageAsync(
            documentInstanceId, pageId, seeds, DocumentTreeRevisionSource.OcrAdopted,
            current.Value.TreeRevisionId, cancellationToken);
        return staged.IsFailure
            ? Result<LogicalPageOcrResult>.Failure(staged.ErrorCode!, staged.ErrorMessage!, staged.Conflicts)
            : Result<LogicalPageOcrResult>.Success(new LogicalPageOcrResult(staged.Value.TreeRevisionId, runIds));
    }

    public async Task<Result<LogicalDocumentOcrResult>> RunDocumentAsync(
        DocumentInstanceId documentInstanceId,
        OcrPresetId presetId,
        IReadOnlyList<LogicalDocumentOcrPagePlan> pages,
        CancellationToken cancellationToken = default)
    {
        if (pages.Count == 0 || pages.Select(page => page.PageId).Distinct().Count() != pages.Count)
        {
            return Result<LogicalDocumentOcrResult>.Failure(
                AppErrorCodes.ValidationFailed, "Document OCR requires distinct physical page plans.");
        }

        List<DocumentTreeRevisionId> revisions = [];
        List<OcrRunId> runIds = [];
        foreach (LogicalDocumentOcrPagePlan page in pages)
        {
            Result<PhysicalPageOcrResult> result = await RunPageAsync(
                documentInstanceId, presetId, page, cancellationToken);
            if (result.IsFailure)
            {
                return Result<LogicalDocumentOcrResult>.Failure(result.ErrorCode!, result.ErrorMessage!);
            }

            revisions.Add(result.Value.StagingTreeRevisionId);
            runIds.AddRange(result.Value.RunIds);
        }

        return Result<LogicalDocumentOcrResult>.Success(new LogicalDocumentOcrResult(revisions, runIds));
    }

    public async Task<Result<PhysicalPageOcrResult>> RunPageAsync(
        DocumentInstanceId documentInstanceId,
        OcrPresetId presetId,
        LogicalDocumentOcrPagePlan page,
        CancellationToken cancellationToken = default)
    {
        if (page.LogicalPageTargets.Count > 0)
        {
            Result<LogicalPageOcrResult> logical = await RunAsync(
                documentInstanceId, presetId, page.PageId, page.LogicalPageTargets, cancellationToken);
            return logical.IsFailure
                ? Result<PhysicalPageOcrResult>.Failure(logical.ErrorCode!, logical.ErrorMessage!, logical.Conflicts)
                : Result<PhysicalPageOcrResult>.Success(new PhysicalPageOcrResult(
                    logical.Value.StagingTreeRevisionId, logical.Value.RegionRunIds, true));
        }

        Result<OcrRun> run = await _ocr.RunPresetOnPagesAsync(
            documentInstanceId, presetId, [page.PageId], cancellationToken);
        if (run.IsFailure)
        {
            return Result<PhysicalPageOcrResult>.Failure(run.ErrorCode!, run.ErrorMessage!);
        }

        Result<IReadOnlyList<OcrPageResult>> pageResults = await _ocr.ListPageResultsAsync(
            run.Value.OcrRunId, cancellationToken);
        DocumentTreeRevisionId? revision = pageResults.IsSuccess
            ? pageResults.Value.SingleOrDefault(result => result.PageId == page.PageId)?.StagingTreeRevisionId
            : null;
        return revision is null
            ? Result<PhysicalPageOcrResult>.Failure(
                AppErrorCodes.InvalidState, "A physical page did not produce a staging tree.")
            : Result<PhysicalPageOcrResult>.Success(new PhysicalPageOcrResult(revision.Value, [run.Value.OcrRunId],
                false));
    }

    private static IEnumerable<DocumentBox> Order(IReadOnlyList<DocumentBox> siblings)
    {
        HashSet<DocumentBoxId> referenced = siblings.Where(box => box.NextSiblingBoxId is not null)
            .Select(box => box.NextSiblingBoxId!.Value).ToHashSet();
        DocumentBox? current = siblings.SingleOrDefault(box => !referenced.Contains(box.BoxId));
        while (current is not null)
        {
            yield return current;
            current = current.NextSiblingBoxId is null
                ? null
                : siblings.Single(box => box.BoxId == current.NextSiblingBoxId.Value);
        }
    }
}
