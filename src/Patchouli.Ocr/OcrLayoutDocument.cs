using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;

namespace Patchouli.Ocr;

public sealed record OcrLayoutDocument(IReadOnlyList<OcrLayoutPage> Pages)
{
    public int TotalBlockCount => Pages.Sum(page => CountBlocks(page.Blocks));

    public Result Validate()
    {
        if (Pages.Count == 0)
        {
            return Result.Failure(AppErrorCodes.ValidationFailed,
                "OCR layout document must contain at least one page.");
        }

        IGrouping<PageId, OcrLayoutPage>? duplicatePageId = Pages
            .GroupBy(page => page.PageId)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicatePageId is not null)
        {
            return Result.Failure(AppErrorCodes.ValidationFailed,
                $"OCR layout document contains duplicate page '{duplicatePageId.Key}'.");
        }

        foreach (OcrLayoutPage page in Pages)
        {
            Result pageValidation = ValidateBlocks(page.Blocks);
            if (pageValidation.IsFailure)
            {
                return pageValidation;
            }
        }

        return Result.Success();
    }

    private static int CountBlocks(IReadOnlyList<OcrLayoutBlock> blocks)
    {
        return blocks.Sum(block => 1 + CountBlocks(block.Children ?? []));
    }

    private static Result ValidateBlocks(IReadOnlyList<OcrLayoutBlock> blocks)
    {
        foreach (OcrLayoutBlock block in blocks)
        {
            if (string.IsNullOrWhiteSpace(block.NodeType))
            {
                return Result.Failure(AppErrorCodes.ValidationFailed, "OCR layout block type is required.");
            }

            if (string.IsNullOrWhiteSpace(block.TextPolicy) || !TextPolicy.IsKnown(block.TextPolicy.Trim()))
            {
                return Result.Failure(AppErrorCodes.ValidationFailed,
                    $"OCR layout block text policy '{block.TextPolicy}' is invalid.");
            }

            if (block.NodeType == LayoutNodeType.TableCell && block.TableCell is null)
            {
                return Result.Failure(AppErrorCodes.ValidationFailed,
                    "OCR table cell blocks must include table cell metadata.");
            }

            if (block.TableCell is not null)
            {
                if (block.NodeType != LayoutNodeType.TableCell)
                {
                    return Result.Failure(AppErrorCodes.ValidationFailed,
                        "Only table cell blocks may include table cell metadata.");
                }

                if (block.TableCell.RowIndex < 0 || block.TableCell.ColIndex < 0)
                {
                    return Result.Failure(AppErrorCodes.ValidationFailed,
                        "OCR table cell row and column indexes must be non-negative.");
                }

                if (block.TableCell.RowSpan <= 0 || block.TableCell.ColSpan <= 0)
                {
                    return Result.Failure(AppErrorCodes.ValidationFailed, "OCR table cell spans must be positive.");
                }
            }

            Result childValidation = ValidateBlocks(block.Children ?? []);
            if (childValidation.IsFailure)
            {
                return childValidation;
            }
        }

        return Result.Success();
    }
}
