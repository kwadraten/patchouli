using FluentAssertions;
using Patchouli.UI.Reading;

namespace Patchouli.Tests;

public sealed class BookReadingPageMapTests
{
    [Fact]
    public void Appends_stack_pages_in_arrival_order()
    {
        BookReadingPageMap map = new();

        map.RecordAppend(2, 0);
        map.RecordAppend(3, 100);
        map.RecordAppend(4, 250);

        map.StartOffsets.Should().Equal(
            new KeyValuePair<int, double>(2, 0),
            new KeyValuePair<int, double>(3, 100),
            new KeyValuePair<int, double>(4, 250));
    }

    [Fact]
    public void Prepend_shifts_pages_below_the_insertion_point_down()
    {
        BookReadingPageMap map = new();
        map.RecordAppend(2, 0);
        map.RecordAppend(3, 100);

        // Page 1 front-inserts at the top; everything from the insertion point down grows.
        map.RecordPrepend(1, 0, 40);

        map.StartOffsets.Should().Equal(
            new KeyValuePair<int, double>(1, 0),
            new KeyValuePair<int, double>(2, 40),
            new KeyValuePair<int, double>(3, 140));
    }

    [Fact]
    public void Later_prepends_leave_earlier_prepended_pages_in_place()
    {
        // The stream yields earlier pages in ascending order (0, then 1); each front-inserts
        // right after the previously prepended region.
        BookReadingPageMap map = new();
        map.RecordAppend(2, 0);
        map.RecordPrepend(0, 0, 60);
        map.RecordPrepend(1, 60, 40);

        map.StartOffsets.Should().Equal(
            new KeyValuePair<int, double>(0, 0),
            new KeyValuePair<int, double>(1, 60),
            new KeyValuePair<int, double>(2, 100));
    }

    [Fact]
    public void Clear_drops_all_recorded_offsets()
    {
        BookReadingPageMap map = new();
        map.RecordAppend(0, 0);

        map.Clear();

        map.StartOffsets.Should().BeEmpty();
        map.TryGetStart(0, out _).Should().BeFalse();
    }

    [Fact]
    public void GetInsertY_returns_the_next_recorded_page_offset()
    {
        BookReadingPageMap map = new();
        map.RecordAppend(2, 0);
        map.RecordAppend(3, 100);
        map.RecordAppend(4, 250);

        // A page inserted before page 2 lands where page 2 begins; page 2's own successor is
        // page 3, so inserting page 0 or 1 uses the same anchor.
        map.GetInsertY(0).Should().Be(0);
        map.GetInsertY(1).Should().Be(0);
        map.GetInsertY(2).Should().Be(100);
        map.GetInsertY(3).Should().Be(250);
    }

    [Fact]
    public void GetInsertY_returns_zero_when_no_higher_page_is_recorded()
    {
        BookReadingPageMap map = new();
        map.RecordAppend(5, 300);

        map.GetInsertY(5).Should().Be(0);
        map.GetInsertY(9).Should().Be(0);
    }
}
