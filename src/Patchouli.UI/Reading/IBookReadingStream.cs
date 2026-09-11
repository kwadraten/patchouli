using Patchouli.Core.Ids;

namespace Patchouli.UI.Reading;

// One page of the whole-book reading stream. Html is the page's compiled content plus a page
// anchor header (see BookReadingHtml). Pages arrive in reading order starting at the page the
// user was viewing: startPage, startPage+1, …, last page, then the earlier pages 0…startPage-1
// with IsPrepend = true so the view can insert them above and compensate the scroll offset.
public sealed record BookReadingPage(int PageIndex, int PageCount, bool IsPrepend, string Html);

// Streams a whole document instance's committed page content as HTML, page by page, so the
// reading mode can render progressively instead of waiting for a full-book compile.
public interface IBookReadingStream
{
    IAsyncEnumerable<BookReadingPage> StreamPagesAsync(
        DocumentInstanceId documentInstanceId, int startPageIndex, CancellationToken cancellationToken = default);
}
