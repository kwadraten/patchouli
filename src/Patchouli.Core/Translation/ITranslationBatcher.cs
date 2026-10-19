namespace Patchouli.Core.Translation;

/// <summary>
/// Groups segments into model requests. Implementations must:
/// <list type="bullet">
/// <item>preserve source order across batches and assign <see cref="TranslationBatch.Index"/> as a
/// zero-based, gap-free sequence;</item>
/// <item>place every segment in exactly one batch, never emit an empty batch and never reorder or
/// duplicate a segment;</item>
/// <item>respect <see cref="TranslationSettings.BatchSize"/> (segments per batch) and
/// <see cref="TranslationSettings.MaxBatchCharacters"/>, and additionally respect
/// <see cref="TranslationSettings.MaxBatchTokens"/> when it has a value;</item>
/// <item>place a single segment that alone exceeds a limit in its own batch instead of splitting or
/// dropping it, because splitting belongs to <see cref="ITranslationSegmenter"/>;</item>
/// <item>copy the optional page reference onto every batch; the reference does not affect grouping.
/// </item>
/// </list>
/// An empty input produces an empty batch list.
/// </summary>
public interface ITranslationBatcher
{
    IReadOnlyList<TranslationBatch> CreateBatches(
        IReadOnlyList<TranslationSegment> segments,
        TranslationSettings settings,
        TranslationBatchContext? context = null);
}
