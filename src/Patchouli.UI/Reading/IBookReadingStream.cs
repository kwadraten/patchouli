using Patchouli.Core.Ids;

namespace Patchouli.UI.Reading;

// One page of the whole-book reading stream. Html is the page's compiled content only (see
// BookReadingHtml); the view shows page boundaries as badges in its left rail. The view model
// decides the delivery order (the page the user was viewing first, then the surrounding window)
// and sets IsPrepend for pages that arrive after pages with a higher index but belong above them.
public sealed record BookReadingPage(int PageIndex, int PageCount, bool IsPrepend, string Html);

// Loads a whole document instance's committed page content as HTML, one page at a time, so the
// reading mode can render a window around the current page instead of waiting for a full-book
// compile. The view model owns ordering, windowing and caching; this contract only lists page
// indices and compiles a single page on demand.
public interface IBookReadingStream
{
    /// <summary>Lists the document's page indices in ascending order. Returns an empty list when
    /// the document cannot be read, matching <see cref="LoadPageAsync"/>'s contract that an
    /// unreadable document yields no pages rather than an exception the view cannot represent.</summary>
    Task<IReadOnlyList<int>> ListPageIndicesAsync(
        DocumentInstanceId documentInstanceId, CancellationToken cancellationToken = default);

    /// <summary>Compiles one page's committed content as HTML, falling back to placeholder HTML
    /// when the page has no committed revision or compilation fails. <paramref name="pageCount"/>
    /// is echoed back in the returned record. <paramref name="cancellationToken"/> is honoured
    /// before any host call, so an already-cancelled request never touches the document.</summary>
    Task<BookReadingPage> LoadPageAsync(
        DocumentInstanceId documentInstanceId, int pageIndex, int pageCount,
        CancellationToken cancellationToken = default);
}
