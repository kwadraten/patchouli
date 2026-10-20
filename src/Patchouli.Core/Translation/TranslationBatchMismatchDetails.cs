using Patchouli.Core.Results;

namespace Patchouli.Core.Translation;

/// <summary>
/// Structured diagnostics attached to a <see cref="TranslationErrorCodes.BatchMismatch"/> failure.
/// Each list contains distinct segment ids in ordinal order.
/// </summary>
public sealed record TranslationBatchMismatchDetails(
    IReadOnlyList<string> MissingSegmentIds,
    IReadOnlyList<string> DuplicateSegmentIds,
    IReadOnlyList<string> UnknownSegmentIds) : IResultFailureDetails
{
    public string Kind => "translation_batch_mismatch";
}
