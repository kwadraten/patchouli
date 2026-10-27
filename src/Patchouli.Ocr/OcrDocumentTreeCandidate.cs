using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;

namespace Patchouli.Ocr;

public sealed record OcrDocumentTreeCandidate(
    IReadOnlyList<OcrPageCandidate> Pages,
    IReadOnlyList<OcrDiagnostic> Diagnostics)
{
    public int TotalBoxCount => Pages.Sum(page => page.Boxes.Count);

    public Result Validate()
    {
        if (Pages.Count == 0)
        {
            return Result.Failure(AppErrorCodes.ValidationFailed,
                "OCR document tree candidate must contain at least one physical page.");
        }

        if (Pages.GroupBy(page => page.PageId).Any(group => group.Count() > 1))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed,
                "OCR document tree candidate cannot contain duplicate physical pages.");
        }

        foreach (OcrPageCandidate page in Pages)
        foreach (OcrBoxCandidate box in page.Boxes)
        {
            if (string.IsNullOrWhiteSpace(box.BoxType))
            {
                return Result.Failure(AppErrorCodes.ValidationFailed, "OCR box type is required.");
            }

            Result bbox = box.BBox.Validate();
            if (bbox.IsFailure)
            {
                return bbox;
            }

            if (!DocumentBoxType.IsKnown(box.BoxType))
            {
                return Result.Failure(AppErrorCodes.ValidationFailed,
                    "Unknown OCR box types cannot be committed.");
            }

            if (!DocumentBoxType.IsValidSubType(box.BoxType, box.SubType))
            {
                return Result.Failure(AppErrorCodes.ValidationFailed, "Invalid document box subtype.");
            }

            if (box.Payload is null && box.BoxType != DocumentBoxType.LogicalPage)
            {
                return Result.Failure(AppErrorCodes.ValidationFailed, "OCR leaf boxes require typed payload.");
            }
        }

        return Diagnostics.Any(diagnostic => diagnostic.BlocksCommit)
            ? Result.Failure(AppErrorCodes.ValidationFailed,
                "OCR candidate contains diagnostics that block commit.")
            : Result.Success();
    }
}

public sealed record OcrPageCandidate(
    PageId PageId,
    int PageIndex,
    IReadOnlyList<OcrBoxCandidate> Boxes);

public sealed record OcrBoxCandidate(
    string BoxType,
    string? SubType,
    int SourceOrder,
    DocumentBoxPayload? Payload,
    NormalizedBBox BBox,
    int? HeadingLevel,
    double? Confidence,
    bool Suppressed,
    DocumentBoxId? ParentLogicalPageBoxId = null,
    DocumentBoxId? PreassignedBoxId = null,
    DocumentBoxId? ContinuesFromBoxId = null,
    string? CodeLanguage = null,
    bool ContinuesPrev = false,
    string? ContinuationState = null);

public sealed record OcrDiagnostic(
    string Code,
    string Message,
    PageId? PageId = null,
    int? SourceOrder = null,
    bool BlocksCommit = false);
