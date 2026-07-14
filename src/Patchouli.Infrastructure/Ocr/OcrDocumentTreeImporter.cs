using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Documents;
using Patchouli.Ocr;

namespace Patchouli.Infrastructure.Ocr;

public sealed class OcrDocumentTreeImporter : IOcrDocumentTreeImporter
{
    private readonly IDocumentTreeService _trees;

    public OcrDocumentTreeImporter(IDocumentTreeService trees)
    {
        _trees = trees;
    }

    public OcrDocumentTreeImporter(SqliteConnectionFactory connectionFactory, IClock clock)
        : this(CreateDefault(connectionFactory, clock))
    {
    }

    public async Task<Result<OcrDocumentTreeImportResult>> StageAsync(
        OcrDocumentTreeImportRequest request,
        CancellationToken cancellationToken = default)
    {
        Result validation = request.Candidate.Validate();
        if (validation.IsFailure)
        {
            return Result<OcrDocumentTreeImportResult>.Failure(validation.ErrorCode!, validation.ErrorMessage!);
        }

        List<DocumentTreeRevisionId> revisions = new();
        int count = 0;
        foreach (OcrPageCandidate page in request.Candidate.Pages.OrderBy(page => page.PageIndex))
        {
            List<DocumentBoxSeed> seeds = [];
            DocumentTreeRevisionId? parentRevisionId = null;
            DocumentBoxId[] logicalParentIds = page.Boxes
                .Where(box => box.ParentLogicalPageBoxId is not null)
                .Select(box => box.ParentLogicalPageBoxId!.Value)
                .Distinct()
                .ToArray();
            if (logicalParentIds.Length > 0)
            {
                Result<DocumentTreeRevision> current = await _trees.GetCurrentRevisionAsync(
                    request.DocumentInstanceId, page.PageId, cancellationToken);
                if (current.IsFailure)
                {
                    return Result<OcrDocumentTreeImportResult>.Failure(
                        current.ErrorCode!, current.ErrorMessage!);
                }

                parentRevisionId = current.Value.TreeRevisionId;
                Result<IReadOnlyList<DocumentBox>> currentBoxes = await _trees.ListBoxesAsync(
                    current.Value.TreeRevisionId, cancellationToken);
                if (currentBoxes.IsFailure)
                {
                    return Result<OcrDocumentTreeImportResult>.Failure(
                        currentBoxes.ErrorCode!, currentBoxes.ErrorMessage!);
                }

                DocumentBox[] logicalRoots = currentBoxes.Value
                    .Where(box => box.BoxType == DocumentBoxType.LogicalPage)
                    .ToArray();
                if (logicalRoots.Length == 0 || logicalParentIds.Any(id => logicalRoots.All(root => root.BoxId != id)))
                {
                    return Result<OcrDocumentTreeImportResult>.Failure(
                        AppErrorCodes.ValidationFailed,
                        "OCR logical-page targets must exist in the current committed page tree.");
                }

                int order = 0;
                foreach (DocumentBox root in Order(logicalRoots))
                {
                    seeds.Add(new DocumentBoxSeed(
                        root.BoxId,
                        null,
                        order++,
                        root.BoxType,
                        root.SubType,
                        root.BaseType,
                        root.BBox,
                        null));
                }
            }

            seeds.AddRange(page.Boxes.Select(box => new DocumentBoxSeed(
                null,
                box.ParentLogicalPageBoxId,
                box.SourceOrder,
                box.BoxType,
                box.SubType,
                box.BaseType,
                box.BBox,
                box.Payload,
                box.HeadingLevel,
                null,
                box.Confidence,
                box.Suppressed)));
            Result<DocumentTreeRevision> staged = await _trees.StagePageAsync(
                request.DocumentInstanceId,
                page.PageId,
                seeds,
                request.RevisionSource,
                parentRevisionId,
                cancellationToken);
            if (staged.IsFailure)
            {
                return Result<OcrDocumentTreeImportResult>.Failure(
                    staged.ErrorCode!, staged.ErrorMessage!, staged.Conflicts);
            }

            revisions.Add(staged.Value.TreeRevisionId);
            count += page.Boxes.Count;
        }

        return Result<OcrDocumentTreeImportResult>.Success(new OcrDocumentTreeImportResult(
            revisions,
            count,
            request.Candidate.Diagnostics));
    }

    public async Task<Result<IReadOnlyList<DocumentTreeRevisionId>>> AdoptAsync(
        IReadOnlyList<DocumentTreeRevisionId> stagingRevisionIds,
        CancellationToken cancellationToken = default)
    {
        Result<IReadOnlyList<DocumentTreeRevision>> adopted = await _trees.AdoptStagingRevisionsAsync(
            stagingRevisionIds, cancellationToken);
        if (adopted.IsFailure)
        {
            return Result<IReadOnlyList<DocumentTreeRevisionId>>.Failure(
                adopted.ErrorCode!, adopted.ErrorMessage!, adopted.Conflicts);
        }

        return Result<IReadOnlyList<DocumentTreeRevisionId>>.Success(
            adopted.Value.Select(revision => revision.TreeRevisionId).ToArray());
    }

    private static IEnumerable<DocumentBox> Order(IReadOnlyList<DocumentBox> siblings)
    {
        HashSet<DocumentBoxId> referenced = siblings
            .Where(box => box.NextSiblingBoxId is not null)
            .Select(box => box.NextSiblingBoxId!.Value)
            .ToHashSet();
        DocumentBox? current = siblings.SingleOrDefault(box => !referenced.Contains(box.BoxId));
        while (current is not null)
        {
            yield return current;
            current = current.NextSiblingBoxId is null
                ? null
                : siblings.Single(box => box.BoxId == current.NextSiblingBoxId.Value);
        }
    }

    private static IDocumentTreeService CreateDefault(SqliteConnectionFactory connectionFactory, IClock clock)
    {
        MarkdigMarkdownEngine markdown = new();
        return new DocumentTreeService(connectionFactory, clock, markdown);
    }
}
