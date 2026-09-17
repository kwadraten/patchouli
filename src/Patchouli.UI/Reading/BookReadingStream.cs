using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;

namespace Patchouli.UI.Reading;

/// <summary>
/// Host-backed <see cref="IBookReadingStream"/>. The view model asks for the page list once and
/// then compiles individual pages on demand as its window grows, so a large book never pays for a
/// whole-book compile when the reader only looks at a few pages. Pages without a committed
/// revision (or whose compile fails) fall back to placeholder HTML so the reading flow stays
/// continuous.
/// </summary>
public sealed class BookReadingStream : IBookReadingStream
{
    private readonly Func<DocumentInstanceId, CancellationToken, Task<Result<IReadOnlyList<Page>>>> _listPages;

    private readonly Func<DocumentInstanceId, PageId, CancellationToken, Task<Result<DocumentTreeRevision>>>
        _getCurrentRevision;

    private readonly Func<DocumentTreeRevisionId, bool, CancellationToken, bool, Task<Result<CompiledMarkdown>>>
        _compilePageMarkdown;

    // Translations are fetched alongside the source so the compare pane can render a page the
    // moment it arrives; the service is cached, so this does not re-read the database per page.
    private readonly Func<DocumentInstanceId, PageId, CancellationToken, Task<TranslatedPageMarkdown?>>
        _getPageTranslation;

    // Page list cache: LoadPageAsync runs once per page, so re-listing the whole document for
    // every page would multiply the database round-trips by the window size. A stream instance
    // lives for one reading session, so a stale list is not a concern.
    private Page[]? _pages;

    public BookReadingStream(HostServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _listPages = services.Pages.ListPagesAsync;
        _getCurrentRevision = services.DocumentTrees.GetCurrentRevisionAsync;
        _compilePageMarkdown = services.DocumentMarkdown.CompilePageMarkdownAsync;
        _getPageTranslation = services.PageTranslations.GetPageTranslationAsync;
    }

    // Seam for tests: the host calls the stream actually depends on, so a test can drive
    // placeholders, translations and cancellation without composing a whole HostServices graph.
    internal BookReadingStream(
        Func<DocumentInstanceId, CancellationToken, Task<Result<IReadOnlyList<Page>>>> listPages,
        Func<DocumentInstanceId, PageId, CancellationToken, Task<Result<DocumentTreeRevision>>> getCurrentRevision,
        Func<DocumentTreeRevisionId, bool, CancellationToken, bool, Task<Result<CompiledMarkdown>>>
            compilePageMarkdown,
        Func<DocumentInstanceId, PageId, CancellationToken, Task<TranslatedPageMarkdown?>>? getPageTranslation = null)
    {
        _listPages = listPages;
        _getCurrentRevision = getCurrentRevision;
        _compilePageMarkdown = compilePageMarkdown;
        _getPageTranslation = getPageTranslation ?? ((_, _, _) => Task.FromResult<TranslatedPageMarkdown?>(null));
    }

    public async Task<IReadOnlyList<int>> ListPageIndicesAsync(
        DocumentInstanceId documentInstanceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Page[]? pages = await GetPagesAsync(documentInstanceId, cancellationToken).ConfigureAwait(false);
        if (pages is null)
        {
            // The contract has no error channel: an unreadable document reports no pages rather
            // than interrupting the reader with an exception the view cannot represent.
            return [];
        }

        return pages.Select(page => page.PageIndex).ToArray();
    }

    public async Task<BookReadingPage> LoadPageAsync(
        DocumentInstanceId documentInstanceId, int pageIndex, int pageCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Page[]? pages = await GetPagesAsync(documentInstanceId, cancellationToken).ConfigureAwait(false);
        Page? page = pages?.FirstOrDefault(candidate => candidate.PageIndex == pageIndex);
        if (page is null)
        {
            return new BookReadingPage(pageIndex, pageCount, false,
                BookReadingHtml.CompilePlaceholderHtml(pageIndex, pageCount));
        }

        Result<DocumentTreeRevision> revision =
            await _getCurrentRevision(page.DocumentInstanceId, page.PageId, cancellationToken).ConfigureAwait(false);
        if (revision.IsFailure)
        {
            return new BookReadingPage(pageIndex, pageCount, false,
                BookReadingHtml.CompilePlaceholderHtml(pageIndex, pageCount));
        }

        Result<CompiledMarkdown> compiled = await _compilePageMarkdown(
                revision.Value.TreeRevisionId, false, cancellationToken, true)
            .ConfigureAwait(false);
        TranslatedPageMarkdown? translation =
            await _getPageTranslation(page.DocumentInstanceId, page.PageId, cancellationToken).ConfigureAwait(false);
        string html = compiled.IsFailure
            ? BookReadingHtml.CompilePlaceholderHtml(pageIndex, pageCount)
            : BookReadingHtml.CompilePageHtml(compiled.Value, pageIndex, pageCount);
        // IsPrepend is the view model's call; the stream only reports page content.
        return new BookReadingPage(
            pageIndex, pageCount, false, html, BookReadingHtml.CompileTranslationHtml(translation));
    }

    // Returns null when the page list cannot be read; only a successful list is cached.
    private async Task<Page[]?> GetPagesAsync(
        DocumentInstanceId documentInstanceId, CancellationToken cancellationToken)
    {
        if (_pages is not null)
        {
            return _pages;
        }

        Result<IReadOnlyList<Page>> listed = await _listPages(documentInstanceId, cancellationToken)
            .ConfigureAwait(false);
        if (listed.IsFailure)
        {
            return null;
        }

        _pages = listed.Value.OrderBy(page => page.PageIndex).ToArray();
        return _pages;
    }
}
