using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Conflicts;

namespace Patchouli.Infrastructure.Documents;

internal sealed class DocumentTreeValidator
{
    private readonly IMarkdownEngine _markdown;

    public DocumentTreeValidator(IMarkdownEngine markdown)
    {
        _markdown = markdown;
    }

    public Result Validate(
        DocumentTreeRevision revision,
        IReadOnlyList<DocumentBox> boxes,
        bool validateCollisions = true)
    {
        if (!DocumentTreeRevisionSource.IsKnown(revision.Source) ||
            !DocumentTreeRevisionStatus.IsKnown(revision.Status))
        {
            return Invalid("Document tree revision source or status is invalid.");
        }

        if (boxes.Any(box => box.TreeRevisionId != revision.TreeRevisionId ||
                             box.DocumentInstanceId != revision.DocumentInstanceId || box.PageId != revision.PageId))
        {
            return Invalid("Every box must belong to the same revision, document instance, and physical page.");
        }

        Dictionary<DocumentBoxId, DocumentBox> byId = new();
        foreach (DocumentBox box in boxes)
        {
            if (!byId.TryAdd(box.BoxId, box))
            {
                return Invalid("Document box ids must be unique within a revision.");
            }

            Result boxValidation = ValidateBox(box);
            if (boxValidation.IsFailure)
            {
                return boxValidation;
            }
        }

        foreach (DocumentBox box in boxes)
        {
            if (box.ParentBoxId is not null && !byId.ContainsKey(box.ParentBoxId.Value))
            {
                return Invalid("Document box parent must exist in the same revision.");
            }

            if (box.NextSiblingBoxId is not null && !byId.ContainsKey(box.NextSiblingBoxId.Value))
            {
                return Invalid("Document box next sibling must exist in the same revision.");
            }
        }

        Result shape = ValidateShape(boxes);
        if (shape.IsFailure)
        {
            return shape;
        }

        Result chains = ValidateSiblingChains(boxes);
        if (chains.IsFailure)
        {
            return chains;
        }

        Result cycles = ValidateParentCycles(boxes, byId);
        if (cycles.IsFailure)
        {
            return cycles;
        }

        return validateCollisions ? ValidateCollisions(boxes) : Result.Success();
    }

    private Result ValidateBox(DocumentBox box)
    {
        Result bbox = box.BBox.Validate();
        if (bbox.IsFailure)
        {
            return bbox;
        }

        if (string.IsNullOrWhiteSpace(box.BoxType))
        {
            return Invalid("Document box type is required.");
        }

        if (!DocumentBoxType.IsKnown(box.BoxType) && box.BaseType is not ("text" or "image" or "table" or "code"))
        {
            return Invalid("Unknown document box types require a usable base_type before adoption.");
        }

        if (box.BoxType == DocumentBoxType.LogicalPage)
        {
            return box.HeadingLevel is null && box.CodeLanguage is null && !box.Suppressed &&
                   (box.Payload is null || box.Payload is TextBoxPayload)
                ? Result.Success()
                : Invalid("Logical pages can only have optional text payloads and cannot have heading level, code language, or suppression.");
        }

        if (box.Payload is null)
        {
            return Invalid("Leaf document boxes require a typed payload.");
        }

        if (box.BoxType == DocumentBoxType.Title)
        {
            if (box.HeadingLevel is < 1 or > 6)
            {
                return Invalid("Title boxes require heading_level from 1 through 6.");
            }
        }
        else if (box.HeadingLevel is not null)
        {
            return Invalid("Only title boxes can store heading_level.");
        }

        if (box.BoxType is not (DocumentBoxType.Code or DocumentBoxType.Algorithm) && box.CodeLanguage is not null)
        {
            return Invalid("Only code boxes can store code_language.");
        }

        string validationType = DocumentBoxType.IsKnown(box.BoxType)
            ? box.BoxType
            : box.BaseType switch
            {
                "image" => DocumentBoxType.Image,
                "table" => DocumentBoxType.Table,
                "code" => DocumentBoxType.Code,
                _ => DocumentBoxType.Text
            };
        return _markdown.ValidateLeaf(validationType, box.Payload);
    }

    private static Result ValidateShape(IReadOnlyList<DocumentBox> boxes)
    {
        DocumentBox[] roots = boxes.Where(box => box.ParentBoxId is null).ToArray();
        bool hasLogicalRoots = roots.Any(box => box.BoxType == DocumentBoxType.LogicalPage);
        bool hasLeafRoots = roots.Any(box => box.BoxType != DocumentBoxType.LogicalPage);
        if (hasLogicalRoots && hasLeafRoots)
        {
            return Invalid("A physical page cannot mix direct leaf boxes with logical-page roots.");
        }

        foreach (DocumentBox box in boxes)
        {
            if (box.BoxType == DocumentBoxType.LogicalPage && box.ParentBoxId is not null)
            {
                return Invalid("Logical pages must be roots of the physical page tree.");
            }
        }

        return Result.Success();
    }

    private static Result ValidateSiblingChains(IReadOnlyList<DocumentBox> boxes)
    {
        foreach (IGrouping<DocumentBoxId?, DocumentBox> group in boxes.GroupBy(box => box.ParentBoxId))
        {
            DocumentBox[] siblings = group.ToArray();
            if (siblings.Length == 0)
            {
                continue;
            }

            HashSet<DocumentBoxId> ids = siblings.Select(box => box.BoxId).ToHashSet();
            Dictionary<DocumentBoxId, int> incoming = ids.ToDictionary(id => id, _ => 0);
            foreach (DocumentBox sibling in siblings)
            {
                if (sibling.NextSiblingBoxId is null)
                {
                    continue;
                }

                if (!ids.Contains(sibling.NextSiblingBoxId.Value))
                {
                    return Invalid("A next sibling must have the same parent.");
                }

                incoming[sibling.NextSiblingBoxId.Value]++;
                if (incoming[sibling.NextSiblingBoxId.Value] > 1)
                {
                    return Invalid("A sibling chain cannot branch or have multiple predecessors.");
                }
            }

            DocumentBoxId[] heads = incoming.Where(pair => pair.Value == 0).Select(pair => pair.Key).ToArray();
            if (heads.Length != 1)
            {
                return Invalid("Each non-empty sibling group must contain exactly one complete chain.");
            }

            HashSet<DocumentBoxId> visited = new();
            DocumentBox? current = siblings.Single(box => box.BoxId == heads[0]);
            while (current is not null)
            {
                if (!visited.Add(current.BoxId))
                {
                    return Invalid("Sibling chains cannot contain cycles.");
                }

                current = current.NextSiblingBoxId is null
                    ? null
                    : siblings.Single(box => box.BoxId == current.NextSiblingBoxId.Value);
            }

            if (visited.Count != siblings.Length)
            {
                return Invalid("Sibling chains must cover every child exactly once.");
            }
        }

        return Result.Success();
    }

    private static Result ValidateParentCycles(
        IReadOnlyList<DocumentBox> boxes,
        IReadOnlyDictionary<DocumentBoxId, DocumentBox> byId)
    {
        foreach (DocumentBox box in boxes)
        {
            HashSet<DocumentBoxId> seen = [box.BoxId];
            DocumentBoxId? parent = box.ParentBoxId;
            while (parent is not null)
            {
                if (!seen.Add(parent.Value))
                {
                    return Invalid("Document box parents cannot contain cycles.");
                }

                parent = byId[parent.Value].ParentBoxId;
            }
        }

        return Result.Success();
    }

    private static Result ValidateCollisions(IReadOnlyList<DocumentBox> boxes)
    {
        foreach (IGrouping<DocumentBoxId?, DocumentBox> group in boxes.GroupBy(box => box.ParentBoxId))
        {
            DocumentBox[] siblings = group
                .Where(box => box.BoxType != DocumentBoxType.LogicalPage && !box.Suppressed)
                .ToArray();
            for (int first = 0; first < siblings.Length; first++)
            for (int second = first + 1; second < siblings.Length; second++)
            {
                DocumentBox left = siblings[first];
                DocumentBox right = siblings[second];
                if (DocumentBoxType.AllowsOverlap(left.BoxType) || DocumentBoxType.AllowsOverlap(right.BoxType))
                {
                    continue;
                }

                if (HasSignificantOverlap(left.BBox, right.BBox))
                {
                    return Result.Failure(AppErrorCodes.ValidationFailed,
                        "CF-06: ordinary sibling document boxes have significantly overlapping bbox regions.",
                        [
                            ConflictDescriptorMapper.DocumentBoxBBoxOrdinaryOverlap(
                                left.PageId.ToString(), right.BoxId.ToString(), right.BoxType, right.BBox,
                                left.BoxType, left.BBox)
                        ]);
                }
            }
        }

        return Result.Success();
    }

    private static bool HasSignificantOverlap(NormalizedBBox left, NormalizedBBox right)
    {
        double width = Math.Min(left.X + left.Width, right.X + right.Width) - Math.Max(left.X, right.X);
        double height = Math.Min(left.Y + left.Height, right.Y + right.Height) - Math.Max(left.Y, right.Y);
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        double intersection = width * height;
        double smallerArea = Math.Min(left.Width * left.Height, right.Width * right.Height);
        return intersection / smallerArea >= 0.1;
    }

    private static Result Invalid(string message)
    {
        return Result.Failure(AppErrorCodes.ValidationFailed, message);
    }
}
