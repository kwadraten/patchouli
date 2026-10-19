using Patchouli.Core.Ids;
using Patchouli.Core.Translation;

namespace Patchouli.Infrastructure.Translation;

/// <summary>
/// Default segmenter for the direct translation pipeline. It keeps the source box order, drops boxes that
/// are marked non-translatable or carry only whitespace, and splits a box that exceeds
/// <see cref="TranslationSettings.MaxBatchCharacters"/> into ordered parts. Every part is a contiguous slice
/// of the source text, so concatenating the parts in <see cref="TranslationSegment.PartIndex"/> order
/// reproduces the original text exactly. Splitting prefers a line break, then a sentence terminator, then
/// whitespace, and only falls back to a hard character cut when the text offers no natural boundary.
/// </summary>
public sealed class DefaultTranslationSegmenter : ITranslationSegmenter
{
    /// <inheritdoc />
    public IReadOnlyList<TranslationSegment> Segment(
        IReadOnlyList<TranslationSourceBox> boxes,
        TranslationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        ArgumentNullException.ThrowIfNull(settings);

        int maxCharacters = Math.Max(1, settings.MaxBatchCharacters);
        List<TranslationSegment> segments = new(boxes.Count);
        foreach (TranslationSourceBox box in boxes)
        {
            if (!box.Translatable || string.IsNullOrWhiteSpace(box.Text))
            {
                continue;
            }

            string text = box.Text;
            if (text.Length <= maxCharacters)
            {
                segments.Add(CreateSegment(box.BoxId, 1, text));
                continue;
            }

            int offset = 0;
            int partIndex = 1;
            while (offset < text.Length)
            {
                int remaining = text.Length - offset;
                int length = Math.Min(maxCharacters, remaining);
                if (length < remaining)
                {
                    length = FindSplitLength(text, offset, length);
                }

                segments.Add(CreateSegment(box.BoxId, partIndex, text.Substring(offset, length)));
                offset += length;
                partIndex++;
            }
        }

        return segments;
    }

    private static TranslationSegment CreateSegment(DocumentBoxId boxId, int partIndex, string text)
    {
        return new TranslationSegment(TranslationSegmentIds.Create(boxId, partIndex), boxId, partIndex, text);
    }

    /// <summary>
    /// Chooses the length of the next part, never more than <paramref name="maxLength"/>. The returned value
    /// is always positive and the remainder of the text is never empty.
    /// </summary>
    private static int FindSplitLength(string text, int offset, int maxLength)
    {
        ReadOnlySpan<char> window = text.AsSpan(offset, maxLength);

        // Prefer the latest boundary near the limit, keeping the declared order line break, sentence end,
        // then whitespace. Searching only the second half avoids producing a tiny part because of an early
        // newline while a much better boundary is available later.
        int preferredStart = Math.Max(1, window.Length / 2);
        if (FindBoundary(window, text, offset, preferredStart, out int preferredLength))
        {
            return preferredLength;
        }

        // No boundary near the limit: use the boundary type priority over the whole window.
        if (FindBoundary(window, text, offset, 0, out int fallbackLength))
        {
            return fallbackLength;
        }

        int hardCut = window.Length;
        if (hardCut > 1 && char.IsHighSurrogate(window[hardCut - 1]) &&
            char.IsLowSurrogate(text[offset + hardCut]))
        {
            hardCut--;
        }

        if (hardCut > 1 && window[hardCut - 1] == '\r' && text[offset + hardCut] == '\n')
        {
            hardCut--;
        }

        return Math.Max(1, hardCut);
    }

    /// <summary>
    /// Finds the last boundary of the highest-priority kind at or after <paramref name="startIndex"/>:
    /// line break first, then sentence end, then whitespace.
    /// </summary>
    private static bool FindBoundary(
        ReadOnlySpan<char> window,
        string text,
        int offset,
        int startIndex,
        out int length)
    {
        return TryFindLastNewline(window, text, offset, startIndex, out length) ||
               TryFindLastSentenceBoundary(window, startIndex, out length) ||
               TryFindLastWhitespace(window, startIndex, out length);
    }

    private static bool TryFindLastNewline(
        ReadOnlySpan<char> window,
        string text,
        int offset,
        int startIndex,
        out int length)
    {
        for (int index = window.Length - 1; index >= startIndex; index--)
        {
            if (window[index] == '\n')
            {
                length = index + 1;
                return true;
            }

            if (window[index] == '\r' && !IsCrlfStart(window, text, offset, index))
            {
                length = index + 1;
                return true;
            }
        }

        length = 0;
        return false;
    }

    private static bool TryFindLastSentenceBoundary(ReadOnlySpan<char> window, int startIndex, out int length)
    {
        for (int index = window.Length - 1; index >= startIndex; index--)
        {
            if (IsSentenceTerminator(window[index]))
            {
                length = IncludeClosingPunctuation(window, index);
                return true;
            }
        }

        length = 0;
        return false;
    }

    private static bool TryFindLastWhitespace(ReadOnlySpan<char> window, int startIndex, out int length)
    {
        for (int index = window.Length - 1; index >= startIndex; index--)
        {
            // A '\r' that reaches this point is the first half of a CRLF whose '\n' falls just outside the
            // window; leave it for the hard cut, which backs off before the pair.
            if (window[index] != '\r' && char.IsWhiteSpace(window[index]))
            {
                length = index + 1;
                return true;
            }
        }

        length = 0;
        return false;
    }

    private static int IncludeClosingPunctuation(ReadOnlySpan<char> window, int index)
    {
        int end = index + 1;
        while (end < window.Length && IsClosingPunctuation(window[end]))
        {
            end++;
        }

        return end;
    }

    private static bool IsCrlfStart(ReadOnlySpan<char> window, string text, int offset, int index)
    {
        if (index + 1 < window.Length)
        {
            return window[index + 1] == '\n';
        }

        return offset + index + 1 < text.Length && text[offset + index + 1] == '\n';
    }

    private static bool IsSentenceTerminator(char value)
    {
        return value is '.' or '!' or '?' or ';' or '。' or '！' or '？' or '；' or '…';
    }

    private static bool IsClosingPunctuation(char value)
    {
        return value is ')' or ']' or '}' or '"' or '\'' or '”' or '’' or '»' or '」' or '』' or '》' or '〉'
            or '）' or '】' or '〕' or '］';
    }
}
