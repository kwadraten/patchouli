namespace Patchouli.UI.Reading;

/// <summary>
/// Tracks each streamed page's vertical offset inside the reading document so the view can pin
/// page-number badges in the left rail. AvaloniaRichEditor exposes no block geometry, so the view
/// records offsets as it inserts pages: an append measures the document height right before the
/// page's blocks are added; a prepend reports where it inserted and how tall the insertion was,
/// and every page at or below that point shifts down by the inserted height.
/// </summary>
/// <remarks>
/// Pages are assumed to be at least one block tall (<see cref="BookReadingHtml"/> emits an empty
/// paragraph for empty pages), so no two pages share a start offset and a prepend never needs to
/// leave a zero-height page behind at the insertion point.
/// </remarks>
public sealed class BookReadingPageMap
{
    private readonly SortedDictionary<int, double> _startOffsets = new();

    /// <summary>Page index → vertical offset of the page's first block inside the editor content.</summary>
    public IReadOnlyDictionary<int, double> StartOffsets => _startOffsets;

    public void Clear()
    {
        _startOffsets.Clear();
    }

    /// <summary>Records a page appended at the bottom of the document; <paramref name="startY"/>
    /// is the measured document height right before the page's blocks were added.</summary>
    public void RecordAppend(int pageIndex, double startY)
    {
        _startOffsets[pageIndex] = startY;
    }

    /// <summary>Records a page front-inserted at <paramref name="insertY"/> (the end of the
    /// previously prepended region). Every already-recorded page at or below that point shifts
    /// down by <paramref name="insertedHeight"/>.</summary>
    public void RecordPrepend(int pageIndex, double insertY, double insertedHeight)
    {
        List<int> shifted = [];
        foreach ((int index, double start) in _startOffsets)
        {
            if (start >= insertY)
            {
                shifted.Add(index);
            }
        }

        foreach (int index in shifted)
        {
            _startOffsets[index] += insertedHeight;
        }

        _startOffsets[pageIndex] = insertY;
    }

    public bool TryGetStart(int pageIndex, out double startY)
    {
        return _startOffsets.TryGetValue(pageIndex, out startY);
    }

    /// <summary>Returns the smallest start offset among recorded pages whose index is greater
    /// than <paramref name="pageIndex"/> — i.e. the offset of the page that directly follows it
    /// in visual order. This is where a prepended page must be inserted (just above its
    /// successor). Returns 0 when no larger page is recorded; callers guarantee the start page
    /// and everything after it were recorded first, so that only happens if the contract is
    /// broken.</summary>
    public double GetInsertY(int pageIndex)
    {
        foreach ((int index, double start) in _startOffsets)
        {
            if (index > pageIndex)
            {
                return start;
            }
        }

        return 0;
    }
}
