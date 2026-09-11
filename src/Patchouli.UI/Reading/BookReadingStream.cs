using System.Runtime.CompilerServices;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;

namespace Patchouli.UI.Reading;

/// <summary>
/// Host-backed <see cref="IBookReadingStream"/>. Pages are streamed one at a time as soon as
/// their committed revision has been compiled, so a large book starts rendering without waiting
/// for a whole-book compile. Pages at or after <c>startPageIndex</c> come first in reading order;
/// the earlier pages follow with <see cref="BookReadingPage.IsPrepend"/> set so the view can insert
/// them above the current scroll position.
/// </summary>
public sealed class BookReadingStream : IBookReadingStream
{
    private readonly Func<DocumentInstanceId, CancellationToken, Task<Result<IReadOnlyList<Page>>>> _listPages;

    private readonly Func<DocumentInstanceId, PageId, CancellationToken, Task<Result<DocumentTreeRevision>>>
        _getCurrentRevision;

    private readonly Func<DocumentTreeRevisionId, bool, CancellationToken, bool, Task<Result<CompiledMarkdown>>>
        _compilePageMarkdown;

    public BookReadingStream(HostServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _listPages = services.Pages.ListPagesAsync;
        _getCurrentRevision = services.DocumentTrees.GetCurrentRevisionAsync;
        _compilePageMarkdown = services.DocumentMarkdown.CompilePageMarkdownAsync;
    }

    // Seam for tests: the three host calls the stream actually depends on, so a test can drive
    // ordering, placeholders and cancellation without composing a whole HostServices graph.
    internal BookReadingStream(
        Func<DocumentInstanceId, CancellationToken, Task<Result<IReadOnlyList<Page>>>> listPages,
        Func<DocumentInstanceId, PageId, CancellationToken, Task<Result<DocumentTreeRevision>>> getCurrentRevision,
        Func<DocumentTreeRevisionId, bool, CancellationToken, bool, Task<Result<CompiledMarkdown>>>
            compilePageMarkdown)
    {
        _listPages = listPages;
        _getCurrentRevision = getCurrentRevision;
        _compilePageMarkdown = compilePageMarkdown;
    }

    public async IAsyncEnumerable<BookReadingPage> StreamPagesAsync(
        DocumentInstanceId documentInstanceId,
        int startPageIndex,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Result<IReadOnlyList<Page>> listed = await _listPages(documentInstanceId, cancellationToken)
            .ConfigureAwait(false);
        if (listed.IsFailure)
        {
            // The contract has no error channel: an unreadable document streams no pages rather
            // than interrupting the reader with an exception the view cannot represent.
            yield break;
        }

        Page[] pages = listed.Value.OrderBy(page => page.PageIndex).ToArray();
        if (pages.Length == 0)
        {
            yield break;
        }

        int start = Math.Clamp(startPageIndex, 0, pages.Length - 1);
        for (int index = start; index < pages.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return await CompilePageAsync(pages[index], pages.Length, false, cancellationToken)
                .ConfigureAwait(false);
        }

        for (int index = 0; index < start; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return await CompilePageAsync(pages[index], pages.Length, true, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<BookReadingPage> CompilePageAsync(
        Page page,
        int pageCount,
        bool isPrepend,
        CancellationToken cancellationToken)
    {
        Result<DocumentTreeRevision> revision =
            await _getCurrentRevision(page.DocumentInstanceId, page.PageId, cancellationToken).ConfigureAwait(false);
        if (revision.IsFailure)
        {
            return new BookReadingPage(page.PageIndex, pageCount, isPrepend,
                BookReadingHtml.CompilePlaceholderHtml(page.PageIndex, pageCount));
        }

        Result<CompiledMarkdown> compiled = await _compilePageMarkdown(
                revision.Value.TreeRevisionId, false, cancellationToken, true)
            .ConfigureAwait(false);
        string html = compiled.IsFailure
            ? BookReadingHtml.CompilePlaceholderHtml(page.PageIndex, pageCount)
            : BookReadingHtml.CompilePageHtml(compiled.Value, page.PageIndex, pageCount);
        return new BookReadingPage(page.PageIndex, pageCount, isPrepend, html);
    }
}
