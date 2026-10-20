using Patchouli.Core.Results;

namespace Patchouli.Core.Translation;

/// <summary>
/// Sends one batch to the selected model and validates the structured response. Implementations must
/// never infer the segment mapping from response order:
/// <list type="bullet">
/// <item>an unparseable or structurally invalid provider response fails with
/// <see cref="TranslationErrorCodes.InvalidModelResponse"/>;</item>
/// <item>missing, duplicate, unknown or mismatched segment ids fail with
/// <see cref="TranslationErrorCodes.BatchMismatch"/>, optionally carrying
/// <see cref="TranslationBatchMismatchDetails"/>;</item>
/// <item>a successful result contains exactly one <see cref="TranslationSegmentTranslation"/> per
/// requested segment, copies <see cref="TranslationSegment.BoxId"/> and
/// <see cref="TranslationSegment.PartIndex"/> from the request, and echoes the requested ids;</item>
/// <item><see cref="TranslationInvocationContext.ReferenceContexts"/> are never translated, counted
/// as batch content, or returned as results;</item>
/// <item>the implementation honors <see cref="TranslationInvocationContext.Timeout"/> and
/// <see cref="CancellationToken"/>.</item>
/// </list>
/// </summary>
public interface ITranslationModelInvoker
{
    Task<Result<TranslationBatchResult>> InvokeAsync(
        TranslationBatch batch,
        TranslationInvocationContext context,
        CancellationToken cancellationToken = default);
}
