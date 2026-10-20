using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Core.Translation;

/// <summary>
/// Merges batch results back into box-level payloads. Results are matched strictly by segment id;
/// the order of the result list and of the translations inside a result is irrelevant. Implementations
/// must:
/// <list type="bullet">
/// <item>treat each result's <see cref="TranslationBatchResult.RequestedSegmentIds"/> as the expected
/// id set and fail with <see cref="TranslationErrorCodes.BatchMismatch"/> when ids are missing,
/// duplicated or unknown, listing the offending ids in the error message and, when available, in
/// <see cref="TranslationBatchMismatchDetails"/>;</item>
/// <item>reject a translation whose <see cref="TranslationSegmentTranslation.BoxId"/> or
/// <see cref="TranslationSegmentTranslation.PartIndex"/> disagrees with repeated declarations for the
/// same id, or whose <see cref="TranslationSegmentTranslation.SegmentId"/> does not equal
/// <see cref="TranslationSegmentIds.Create"/> for that box and part;</item>
/// <item>for one box, order the part translations by ascending
/// <see cref="TranslationSegmentTranslation.PartIndex"/> and concatenate them in that order;</item>
/// <item>never infer a box, part or ordering from the order in which results were returned.</item>
/// </list>
/// An empty result list merges to an empty dictionary. Merging only validates the results it receives;
/// callers must pass one result per successfully invoked batch and must not pass results from a failed
/// invocation, because a batch that never produced a result has no data to validate here.
/// </summary>
public interface ITranslationResultProcessor
{
    Result<IReadOnlyDictionary<DocumentBoxId, string>> Merge(
        IReadOnlyList<TranslationBatchResult> results);
}
