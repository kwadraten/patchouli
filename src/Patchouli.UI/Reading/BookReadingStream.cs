using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Reading;

namespace Patchouli.UI.Reading;

/// <summary>
/// Host-backed <see cref="IBookReadingStream"/>. The view model asks for the page list once and
/// then compiles individual pages on demand as its window grows, so a large book never pays for a
/// whole-book compile when the reader only looks at a few pages. Pages without a committed
/// revision (or whose compile fails) fall back to a placeholder scene so the reading flow stays
/// continuous. Each page is projected into a structured <see cref="ReadingScene"/> for the source
/// and (when a translation exists) a matching translation scene whose missing blocks are muted
/// <see cref="ReadingBlock.UntranslatedKind"/> placeholders rather than source-text fallbacks.
/// </summary>
public sealed class BookReadingStream : IBookReadingStream
{
    private readonly Func<DocumentInstanceId, CancellationToken, Task<Result<IReadOnlyList<Page>>>> _listPages;

    private readonly Func<DocumentInstanceId, PageId, CancellationToken, Task<Result<DocumentTreeRevision>>>
        _getCurrentRevision;

    private readonly Func<DocumentTreeRevisionId, bool, CancellationToken, bool, Task<Result<CompiledMarkdown>>>
        _compilePageMarkdown;

    private readonly Func<DocumentTreeRevisionId, CancellationToken, Task<Result<IReadOnlyList<DocumentBox>>>>
        _listBoxes;

    // Fallback parser for a CompiledMarkdown that carries no pre-parsed Document model (the
    // compiler always fills it in production; tests and caches may not).
    private readonly Func<string, MarkdownDocumentModel> _parseMarkdown;

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
        _listBoxes = (revisionId, token) => services.DocumentTrees.ListBoxesAsync(revisionId, token);
        _parseMarkdown = services.Markdown.Parse;
        _getPageTranslation = services.PageTranslations.GetPageTranslationAsync;
    }

    // Seam for tests: the host calls the stream actually depends on, so a test can drive
    // placeholders, translations and cancellation without composing a whole HostServices graph.
    internal BookReadingStream(
        Func<DocumentInstanceId, CancellationToken, Task<Result<IReadOnlyList<Page>>>> listPages,
        Func<DocumentInstanceId, PageId, CancellationToken, Task<Result<DocumentTreeRevision>>> getCurrentRevision,
        Func<DocumentTreeRevisionId, bool, CancellationToken, bool, Task<Result<CompiledMarkdown>>>
            compilePageMarkdown,
        Func<DocumentTreeRevisionId, CancellationToken, Task<Result<IReadOnlyList<DocumentBox>>>>? listBoxes = null,
        Func<DocumentInstanceId, PageId, CancellationToken, Task<TranslatedPageMarkdown?>>? getPageTranslation = null,
        Func<string, MarkdownDocumentModel>? parseMarkdown = null)
    {
        _listPages = listPages;
        _getCurrentRevision = getCurrentRevision;
        _compilePageMarkdown = compilePageMarkdown;
        _listBoxes = listBoxes ??
                     ((_, _) => Task.FromResult(Result<IReadOnlyList<DocumentBox>>.Success([])));
        _parseMarkdown = parseMarkdown ?? (markdown => new MarkdownDocumentModel(
            string.IsNullOrWhiteSpace(markdown)
                ? []
                : [new MarkdownBlock("paragraph", markdown.Trim(), 0, markdown.Trim().Length)]));
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
            return Placeholder(pageIndex, pageCount);
        }

        Result<DocumentTreeRevision> revision =
            await _getCurrentRevision(page.DocumentInstanceId, page.PageId, cancellationToken).ConfigureAwait(false);
        if (revision.IsFailure)
        {
            return Placeholder(pageIndex, pageCount);
        }

        DocumentTreeRevisionId revisionId = revision.Value.TreeRevisionId;
        Result<CompiledMarkdown> compiled =
            await _compilePageMarkdown(revisionId, false, cancellationToken, true).ConfigureAwait(false);
        if (compiled.IsFailure)
        {
            return Placeholder(pageIndex, pageCount);
        }

        Result<IReadOnlyList<DocumentBox>> boxes =
            await _listBoxes(revisionId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<DocumentBox> pageBoxes = boxes.IsSuccess ? boxes.Value : [];
        MarkdownDocumentModel model = compiled.Value.Document ??
                                      _parseMarkdown(compiled.Value.Markdown);
        string imageKey = ImageKeyFor(page);
        ReadingScene source = model.Blocks.Count == 0
            ? PlaceholderScene(pageIndex)
            : ReadingSceneBuilder.Build(model, compiled.Value.SourceMap, pageBoxes, imageKey, pageIndex,
                compiled.Value.Markdown);

        TranslatedPageMarkdown? translation =
            await _getPageTranslation(page.DocumentInstanceId, page.PageId, cancellationToken).ConfigureAwait(false);
        ReadingScene? translated = BuildTranslationScene(translation, pageBoxes, imageKey, pageIndex);

        // IsPrepend is the view model's call; the stream only reports page content.
        return new BookReadingPage(pageIndex, pageCount, false, source, translated);
    }

    // A page with no committed content renders as a single muted note so the reading flow keeps a
    // line for the page (its rail badge needs a target) instead of silently skipping it.
    private static BookReadingPage Placeholder(int pageIndex, int pageCount)
    {
        return new BookReadingPage(pageIndex, pageCount, false, PlaceholderScene(pageIndex));
    }

    private static ReadingScene PlaceholderScene(int pageIndex)
    {
        return new ReadingScene(
        [
            new ReadingBlock(null, "paragraph", 0, "本页尚未识别文字。", PageIndex: pageIndex)
        ]);
    }

    // Projects a page's translated markdown through the same scene shape as the source, then
    // replaces every block whose source box is stale (StaleBoxIds) with a 未翻译 placeholder so a
    // partially translated page never falls back to source text for its untranslated blocks.
    // Returns null when the page has no translation at all; the reading view fills that page's
    // compare rows with per-block 未翻译 placeholders to keep the columns row-aligned.
    private ReadingScene? BuildTranslationScene(
        TranslatedPageMarkdown? translation,
        IReadOnlyList<DocumentBox> boxes,
        string imageKey,
        int pageIndex)
    {
        if (translation is null || string.IsNullOrWhiteSpace(translation.Markdown))
        {
            return null;
        }

        HashSet<DocumentBoxId> stale = [.. translation.Status.StaleBoxIds];
        ReadingScene built = ReadingSceneBuilder.Build(
            _parseMarkdown(translation.Markdown), translation.SourceMap, boxes, imageKey, pageIndex,
            translation.Markdown);
        List<ReadingBlock> blocks = new(built.Blocks.Count);
        foreach (ReadingBlock block in built.Blocks)
        {
            blocks.Add(block.BoxId is { } boxId && stale.Contains(boxId)
                ? ReadingBlock.Untranslated(boxId, pageIndex)
                : block);
        }

        return blocks.Count == 0 ? null : new ReadingScene(blocks);
    }

    // The image key names the owning page bitmap the host resolves; whole-book reading keys by
    // page index so the media cache can load each page render on demand.
    private static string ImageKeyFor(Page page)
    {
        return $"page:{page.PageIndex}";
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
