using Patchouli.Core.Ids;

namespace Patchouli.Core.Translation;

/// <summary>
/// Identifies the source page a batch was built from. This is a reference, not the context content:
/// document title, glossary and adjacent reference text are carried by
/// <see cref="TranslationInvocationContext"/>. <paramref name="PageIndex"/> is the zero-based physical
/// page index used by <see cref="Patchouli.Core.Layout.Page"/>.
/// </summary>
public sealed record TranslationBatchContext(
    DocumentInstanceId DocumentInstanceId,
    PageId PageId,
    int PageIndex);

/// <summary>
/// An ordered group of segments sent to one model request. <see cref="SegmentIds"/> is a snapshot of
/// the stable ids in source order and is the alignment key for
/// <see cref="TranslationBatchResult"/>. <see cref="Index"/> is zero-based within one page.
/// </summary>
public sealed record TranslationBatch(
    int Index,
    IReadOnlyList<TranslationSegment> Segments,
    TranslationBatchContext? Context = null)
{
    /// <summary>The stable segment ids in source order.</summary>
    public IReadOnlyList<string> SegmentIds =>
        Segments.Select(static segment => segment.SegmentId).ToArray();
}
