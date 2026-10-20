using System.Globalization;
using Patchouli.Core.Ids;

namespace Patchouli.Core.Translation;

/// <summary>
/// One text-bearing box of a source page as seen by the translation pipeline. The caller extracts
/// <paramref name="Text"/> from the box payload; <paramref name="Type"/> is one of the
/// <see cref="Patchouli.Core.Documents.DocumentBoxType"/> constants.
/// </summary>
public sealed record TranslationSourceBox(
    DocumentBoxId BoxId,
    string Type,
    string Text,
    bool Translatable = true);

/// <summary>
/// A translatable unit produced by <see cref="ITranslationSegmenter"/>. A box that exceeds the
/// per-segment character limit is split into parts; every part keeps the owning <see cref="BoxId"/>
/// and a stable <see cref="SegmentId"/> formed as <c>{box-id}#{part-index}</c> (one-based).
/// </summary>
public sealed record TranslationSegment(
    string SegmentId,
    DocumentBoxId BoxId,
    int PartIndex,
    string Text);

/// <summary>Builds the stable segment identifier shared by segmentation, batching and result merging.</summary>
public static class TranslationSegmentIds
{
    public static string Create(DocumentBoxId boxId, int partIndex)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(partIndex, 1);
        return string.Concat(boxId.ToString(), "#", partIndex.ToString(CultureInfo.InvariantCulture));
    }
}
