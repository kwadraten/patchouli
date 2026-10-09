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

    public async Task<Result<PhysicalPageOcrResult>> RunPageEditAsync(OcrPresetId presetId,
        PageEditSessionId sessionId, CancellationToken cancellationToken = default)
    {
        if (_trees is not IDocumentTreeEditor editor)
        {
            return Result<PhysicalPageOcrResult>.Failure(AppErrorCodes.UnsupportedOperation,
                "The tree service does not support page editing.");
        }

        Result<PageEditSession> edit = await _trees.GetPageEditAsync(sessionId, cancellationToken);
        if (edit.IsFailure)
        {
            return Result<PhysicalPageOcrResult>.Failure(edit.ErrorCode!, edit.ErrorMessage!);
        }

        PageEditSession session = edit.Value;
        Result<IReadOnlyList<DocumentBox>> snapshot = await _trees.ListBoxesAsync(
            session.DraftRevisionId, cancellationToken);
        if (snapshot.IsFailure)
        {
            return Result<PhysicalPageOcrResult>.Failure(snapshot.ErrorCode!, snapshot.ErrorMessage!);
        }

        DocumentBox[] roots = snapshot.Value.Where(box => box.BoxType == DocumentBoxType.LogicalPage).ToArray();
        List<OcrRunId> runIds = [];
        List<DocumentBox> replacement = [];
        if (roots.Length == 0)
        {
            Result<IReadOnlyList<DocumentBox>> recognized = await RecognizeWorkingBoxesAsync(
                session, presetId, null, runIds, cancellationToken);
            if (recognized.IsFailure)
            {
                return Result<PhysicalPageOcrResult>.Failure(recognized.ErrorCode!, recognized.ErrorMessage!);
            }

            replacement.AddRange(recognized.Value.Select(box => box with
            {
                TreeRevisionId = session.DraftRevisionId
            }));
        }
        else
        {
            foreach (DocumentBox root in Order(roots))
            {
                Result<IReadOnlyList<DocumentBox>> recognized = await RecognizeWorkingBoxesAsync(
                    session, presetId, root, runIds, cancellationToken);
                if (recognized.IsFailure)
                {
                    return Result<PhysicalPageOcrResult>.Failure(recognized.ErrorCode!, recognized.ErrorMessage!);
                }

                HashSet<DocumentBoxId> logicalIds = recognized.Value
                    .Where(box => box.BoxType == DocumentBoxType.LogicalPage).Select(box => box.BoxId).ToHashSet();
                DocumentBox[] content = recognized.Value.Where(box => box.BoxType != DocumentBoxType.LogicalPage)
                    .Select(box => box with
                    {
                        TreeRevisionId = session.DraftRevisionId,
                        ParentBoxId = box.ParentBoxId is null || logicalIds.Contains(box.ParentBoxId.Value)
                            ? root.BoxId
                            : box.ParentBoxId
                    }).ToArray();
                replacement.Add(root with
                {
                    Payload = content.Length == 0
                        ? recognized.Value.FirstOrDefault(box => box.BoxType == DocumentBoxType.LogicalPage)?.Payload
                        : null
                });
                // Removing provider logical-page wrappers joins their children into one sibling chain.
                foreach (IGrouping<DocumentBoxId?, DocumentBox> siblings in content.GroupBy(box => box.ParentBoxId))
                {
                    DocumentBox[] ordered = siblings.ToArray();
                    for (int index = 0; index < ordered.Length; index++)
                    {
                        replacement.Add(ordered[index] with
                        {
                            NextSiblingBoxId = index + 1 < ordered.Length ? ordered[index + 1].BoxId : null
                        });
                    }
                }
            }
        }

        Result applied = await editor.ApplyPageOcrAsync(session, snapshot.Value, replacement, cancellationToken);
        return applied.IsFailure
            ? Result<PhysicalPageOcrResult>.Failure(applied.ErrorCode!, applied.ErrorMessage!, applied.Conflicts)
            : Result<PhysicalPageOcrResult>.Success(new PhysicalPageOcrResult(
                session.DraftRevisionId, runIds, roots.Length > 0));
    }

    private async Task<Result<IReadOnlyList<DocumentBox>>> RecognizeWorkingBoxesAsync(PageEditSession session,
        OcrPresetId presetId, DocumentBox? logicalPage, List<OcrRunId> runIds, CancellationToken cancellationToken)
    {
        Result<OcrRun> run = logicalPage is null
            ? await _ocr.RunWorkingOnPageAsync(session.DocumentInstanceId, presetId, session.PageId, cancellationToken)
            : await _ocr.RunPresetOnRegionAsync(session.DocumentInstanceId, presetId, session.PageId,
                logicalPage.BBox, cancellationToken);
        if (run.IsFailure)
        {
            return Result<IReadOnlyList<DocumentBox>>.Failure(run.ErrorCode!, run.ErrorMessage!);
        }

        runIds.Add(run.Value.OcrRunId);
        Result<IReadOnlyList<DocumentBox>> recognized;
        Result cleanup;
        try
        {
            recognized = await ReadWorkingBoxesAsync(run.Value.OcrRunId, session.PageId, cancellationToken);
        }
        finally
        {
            // Cleanup must also finish after cancellation, before the session draft is ever modified.
            cleanup = await _ocr.DiscardWorkingRunAsync(run.Value.OcrRunId, CancellationToken.None);
        }

        return cleanup.IsFailure
            ? Result<IReadOnlyList<DocumentBox>>.Failure(cleanup.ErrorCode!, cleanup.ErrorMessage!)
            : recognized;
    }

    private async Task<Result<IReadOnlyList<DocumentBox>>> ReadWorkingBoxesAsync(OcrRunId runId, PageId pageId,
        CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<OcrPageResult>> results = await _ocr.ListPageResultsAsync(runId, cancellationToken);
        if (results.IsFailure)
        {
            return Result<IReadOnlyList<DocumentBox>>.Failure(results.ErrorCode!, results.ErrorMessage!);
        }

        OcrPageResult? page = results.Value.SingleOrDefault(result => result.PageId == pageId);
        if (page is null || page.State != OcrPageResultState.Succeeded || page.WorkingTreeRevisionId is null)
        {
            return Result<IReadOnlyList<DocumentBox>>.Failure(page?.ErrorCode ?? AppErrorCodes.InvalidState,
                page?.ErrorMessage ?? "Page OCR did not produce a successful working result.");
        }

        return await _trees.ListBoxesAsync(page.WorkingTreeRevisionId.Value, cancellationToken);
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
                ? pageResults.Value.SingleOrDefault(result => result.PageId == pageId)?.WorkingTreeRevisionId
                : null;
            if (regionRevision is null)
            {
                return Result<LogicalPageOcrResult>.Failure(
                    AppErrorCodes.InvalidState, "A logical-page OCR region did not produce a working revision.");
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

        Result<DocumentTreeRevision> working = await _trees.BeginWorkingRevisionAsync(
            documentInstanceId, pageId, seeds, DocumentTreeRevisionSource.OcrAdopted,
            current.Value.TreeRevisionId, cancellationToken);
        return working.IsFailure
            ? Result<LogicalPageOcrResult>.Failure(working.ErrorCode!, working.ErrorMessage!, working.Conflicts)
            : Result<LogicalPageOcrResult>.Success(new LogicalPageOcrResult(working.Value.TreeRevisionId, runIds));
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

        // Plans arrive in document order: the caller builds them from the page table ordered by
        // page_index, so adjacent target-less plans are consecutive physical pages and share one
        // cloud upload. A non-contiguous subset would still be correct because the run engine
        // splits non-adjacent pages into separate ranges within that single upload.
        DocumentTreeRevisionId?[] revisions = new DocumentTreeRevisionId?[pages.Count];
        List<OcrRunId> runIds = [];
        int index = 0;
        while (index < pages.Count)
        {
            if (pages[index].LogicalPageTargets.Count > 0)
            {
                Result<PhysicalPageOcrResult> result = await RunPageAsync(
                    documentInstanceId, presetId, pages[index], cancellationToken);
                if (result.IsFailure)
                {
                    return Result<LogicalDocumentOcrResult>.Failure(result.ErrorCode!, result.ErrorMessage!);
                }

                revisions[index] = result.Value.WorkingTreeRevisionId;
                runIds.AddRange(result.Value.RunIds);
                index++;
                continue;
            }

            int groupStart = index;
            while (index < pages.Count && pages[index].LogicalPageTargets.Count == 0)
            {
                index++;
            }

            PageId[] groupPageIds = pages.Skip(groupStart).Take(index - groupStart)
                .Select(page => page.PageId).ToArray();
            Result<OcrRun> run = await _ocr.RunPresetOnPagesAsync(
                documentInstanceId, presetId, groupPageIds, cancellationToken);
            if (run.IsFailure)
            {
                return Result<LogicalDocumentOcrResult>.Failure(run.ErrorCode!, run.ErrorMessage!);
            }

            runIds.Add(run.Value.OcrRunId);
            Result<IReadOnlyList<OcrPageResult>> pageResults = await _ocr.ListPageResultsAsync(
                run.Value.OcrRunId, cancellationToken);
            for (int groupIndex = groupStart; groupIndex < index; groupIndex++)
            {
                DocumentTreeRevisionId? revision = pageResults.IsSuccess
                    ? pageResults.Value.SingleOrDefault(result => result.PageId == pages[groupIndex].PageId)
                        ?.WorkingTreeRevisionId
                    : null;
                if (revision is null)
                {
                    return Result<LogicalDocumentOcrResult>.Failure(
                        AppErrorCodes.InvalidState, "A physical page did not produce a working revision.");
                }

                revisions[groupIndex] = revision.Value;
            }
        }

        return Result<LogicalDocumentOcrResult>.Success(new LogicalDocumentOcrResult(
            revisions.Select(revision => revision!.Value).ToArray(), runIds));
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
                    logical.Value.WorkingTreeRevisionId, logical.Value.RegionRunIds, true));
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
            ? pageResults.Value.SingleOrDefault(result => result.PageId == page.PageId)?.WorkingTreeRevisionId
            : null;
        return revision is null
            ? Result<PhysicalPageOcrResult>.Failure(
                AppErrorCodes.InvalidState, "A physical page did not produce a working revision.")
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
