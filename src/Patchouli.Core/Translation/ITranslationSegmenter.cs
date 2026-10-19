namespace Patchouli.Core.Translation;

/// <summary>
/// Splits one source page into translatable segments. Implementations must:
/// <list type="bullet">
/// <item>skip boxes whose <see cref="TranslationSourceBox.Translatable"/> is false, and boxes whose
/// text is empty or whitespace-only;</item>
/// <item>preserve source box order and emit the parts of one box in ascending
/// <see cref="TranslationSegment.PartIndex"/>, starting at one;</item>
/// <item>split text that exceeds the character limit into parts whose source text, concatenated in
/// part order, reproduces the original text exactly (no character is dropped or reordered), using
/// <see cref="TranslationSegmentIds.Create"/> for each part id;</item>
/// <item>never merge different boxes into one segment.</item>
/// </list>
/// The character limit is <see cref="TranslationSettings.MaxBatchCharacters"/>; an implementation may
/// use a smaller cap of its own but must not exceed that value. Splitting is the only stage allowed to
/// divide a box: the batcher never splits or drops a segment.
/// </summary>
public interface ITranslationSegmenter
{
    IReadOnlyList<TranslationSegment> Segment(
        IReadOnlyList<TranslationSourceBox> boxes,
        TranslationSettings settings);
}
