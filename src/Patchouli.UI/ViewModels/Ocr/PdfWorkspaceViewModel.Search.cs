using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Core.Search;
using Patchouli.Host;
using Patchouli.Host.Composition;
using Patchouli.UI.ViewModels.Core;

namespace Patchouli.UI.ViewModels;

/// <summary>
/// Whole-book search sidebar (全书搜索) of the PDF workspace. Matching semantics are the shared
/// <see cref="ISearchService"/> document-scoped search — the same query/profile/rewrite pipeline
/// the library-wide evidence search uses, pinned to the loaded <see cref="DocumentInstanceId"/>.
/// Results are flattened matched units; each row jumps to its page and highlights its source box.
/// A per-search generation counter, a linked cancellation source and library/document identity
/// checks keep late results from a superseded query, a cleared workspace or a switched
/// library/document from ever reaching the view.
/// </summary>
public sealed partial class PdfWorkspaceViewModel
{
    // Keep requests bounded by page count while retrieving every match within each page.
    private const int BookSearchPageSize = 100;

    private int _bookSearchGeneration;
    private CancellationTokenSource? _bookSearchCancellation;
    private BookSearchResultViewModel? _selectedBookSearchRow;

    private AsyncCommand? _showSearchTabCommand;
    private AsyncCommand? _runBookSearchCommand;
    private AsyncCommand? _previousBookSearchMatchCommand;
    private AsyncCommand? _nextBookSearchMatchCommand;

    /// <summary>Test seam: intercepts whole-book search requests so tests can serve canned or
    /// gated result pages without a populated search index.</summary>
    internal Func<SearchRequest, CancellationToken, Task<Result<SearchResultPage>>>? BookSearchHandler { get; set; }

    /// <summary>Search input of the 全书搜索 sidebar tab.</summary>
    [ObservableProperty]
    public partial string BookSearchQuery { get; set; } = string.Empty;

    partial void OnBookSearchQueryChanged(string value)
    {
        ResetBookSearchState();
    }

    /// <summary>Flattened matched units of the last completed whole-book search.</summary>
    private readonly SnapshotObservableCollection<BookSearchResultViewModel> _bookSearchResults = new();

    [ExcludeFromDerivedGeneration]
    public ObservableCollection<BookSearchResultViewModel> BookSearchResults => _bookSearchResults;

    /// <summary>True while a whole-book search is in flight.</summary>
    [ObservableProperty]
    public partial bool IsBookSearchRunning { get; private set; }

    /// <summary>One-line status under the input: idle hint, running state, hit counts or error.</summary>
    [ObservableProperty]
    public partial string BookSearchStatusText { get; private set; } =
        string.Empty;

    /// <summary>True when the result list is empty (drives the placeholder text).</summary>
    [ObservableProperty]
    public partial bool HasNoBookSearchResults { get; private set; } = true;

    /// <summary>"current / total" label beside the previous/next match arrows.</summary>
    [ObservableProperty]
    public partial string BookSearchMatchPositionText { get; private set; } =
        string.Empty;

    /// <summary>The match the previous/next arrows step from; also the highlighted result row.</summary>
    [ObservableProperty]
    public partial BookSearchResultViewModel? SelectedBookSearchResult { get; private set; }

    partial void OnSelectedBookSearchResultChanged(BookSearchResultViewModel? value)
    {
        if (!ReferenceEquals(_selectedBookSearchRow, value))
        {
            if (_selectedBookSearchRow is not null)
            {
                _selectedBookSearchRow.IsSelected = false;
            }

            _selectedBookSearchRow = value;
        }

        if (value is not null)
        {
            value.IsSelected = true;
        }

        UpdateBookSearchMatchPosition();
    }

    /// <summary>True while the whole-book search tab is the active sidebar surface.</summary>
    public bool IsSearchTabActive => !IsEditMode && !IsBookReadingMode && ActiveSidebarTab == SidebarTab.Search;

    /// <summary>Sidebar menu entry: opens the whole-book search tab. Routed through the shared
    /// tab switch so a pending edit session is resolved before leaving its surface.</summary>
    [ExcludeFromDerivedGeneration]
    public AsyncCommand ShowSearchTabCommand => _showSearchTabCommand ??= new AsyncCommand(ShowSearchTabAsync);

    /// <summary>Runs the whole-book search (search button and Enter in the input).</summary>
    [ExcludeFromDerivedGeneration]
    public AsyncCommand RunBookSearchCommand => _runBookSearchCommand ??= new AsyncCommand(RunBookSearchAsync);

    /// <summary>Steps to the previous match, wrapping around at the first.</summary>
    [ExcludeFromDerivedGeneration]
    public AsyncCommand PreviousBookSearchMatchCommand => _previousBookSearchMatchCommand ??=
        new AsyncCommand(() => MoveBookSearchMatchAsync(-1), () => BookSearchResults.Count > 0);

    /// <summary>Steps to the next match, wrapping around at the last.</summary>
    [ExcludeFromDerivedGeneration]
    public AsyncCommand NextBookSearchMatchCommand => _nextBookSearchMatchCommand ??=
        new AsyncCommand(() => MoveBookSearchMatchAsync(1), () => BookSearchResults.Count > 0);

    private async Task ShowSearchTabAsync()
    {
        ExitBookReading();
        IsSidebarOpen = true;
        await SwitchSidebarTabAsync(SidebarTab.Search);
    }

    private async Task RunBookSearchAsync()
    {
        using IDisposable? operation = EnterOperation();
        if (operation is null)
        {
            return;
        }

        int generation = ++_bookSearchGeneration;
        CancellationTokenSource cancellation = ReplaceBookSearchCancellation();
        CancellationToken token = cancellation.Token;
        IsBookSearchRunning = false;
        string query = BookSearchQuery.Trim();
        if (query.Length == 0)
        {
            ClearBookSearchResults();
            BookSearchStatusText = "请输入搜索词。";
            return;
        }

        if (!TryGetDocumentInstanceId(out DocumentInstanceId documentId))
        {
            ClearBookSearchResults();
            BookSearchStatusText = "当前题录没有可搜索的文档。";
            return;
        }

        IsBookSearchRunning = true;
        BookSearchStatusText = "正在搜索全书...";
        try
        {
            HostServices services = await _main.ServicesAsync();
            int libraryGeneration = _main.LibraryGeneration;
            List<BookSearchResultViewModel> collected = [];
            string? cursor = null;
            string indexStatus = SearchIndexStatusValue.Current;
            string? affectedScopes = null;
            int matchedPages = 0;
            do
            {
                SearchRequest request = new(query, documentId, BookSearchPageSize, cursor)
                {
                    IncludeAllMatchedUnits = true,
                    IncludeAllDocumentPages = true
                };
                Result<SearchResultPage> page = BookSearchHandler is { } handler
                    ? await handler(request, token)
                    : await Task.Run(() => services.Search.SearchLibraryAsync(request, token), token);
                token.ThrowIfCancellationRequested();
                if (page.IsFailure)
                {
                    if (IsCurrentBookSearchContext(generation, services, libraryGeneration, documentId))
                    {
                        ClearBookSearchResults();
                        BookSearchStatusText = $"ERROR {page.ErrorCode}: {page.ErrorMessage}";
                        Status = BookSearchStatusText;
                    }

                    return;
                }

                matchedPages += page.Value.Results.Count;
                int firstIndex = collected.Count;
                List<BookSearchResultViewModel> batch = await Task.Run(() =>
                {
                    List<BookSearchResultViewModel> rows = [];
                    foreach (SearchPageResult hit in page.Value.Results)
                    {
                        foreach (SearchMatchedUnit unit in hit.MatchedUnits.Where(unit => unit.IsMatch))
                        {
                            rows.Add(new BookSearchResultViewModel(
                                new SearchMatchedUnitViewModel(
                                    unit.UnitId.ToString(), unit.Text, unit.BoxType, unit.Ordinal,
                                    unit.IsMatch, hit.DocumentInstanceId, hit.PageIndex, unit.BoxId,
                                    unit.TreeRevisionId),
                                query, hit.PageLabel, firstIndex + rows.Count));
                        }
                    }

                    return rows;
                }, token);
                collected.AddRange(batch);

                cursor = page.Value.NextCursor;
                indexStatus = page.Value.IndexStatus;
                affectedScopes = page.Value.AffectedScopesSummary;
            } while (cursor is not null && !token.IsCancellationRequested);

            if (!IsCurrentBookSearchContext(generation, services, libraryGeneration, documentId))
            {
                return;
            }

            ReplaceBookSearchResults(collected);
            string status = collected.Count == 0
                ? "没有命中结果。"
                : $"{collected.Count} 处 · {matchedPages} 页";

            if (indexStatus != SearchIndexStatusValue.Current)
            {
                status += $"（索引状态：{indexStatus}）";
            }

            if (!string.IsNullOrWhiteSpace(affectedScopes))
            {
                status += $" {affectedScopes}";
            }

            BookSearchStatusText = status;
            Status = collected.Count == 0
                ? $"全书搜索完成：没有命中「{query}」。"
                : $"全书搜索完成：{collected.Count} 处命中「{query}」。";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (Volatile.Read(ref _bookSearchGeneration) == generation)
            {
                IsBookSearchRunning = false;
            }
        }
    }

    /// <summary>
    /// Drops the in-flight search and every result. Called when a new search starts, when the
    /// watcher notices the loaded document being cleared, and by the workspace teardown hook so
    /// late results can never populate a different document's tab.
    /// </summary>
    internal void ResetBookSearchState()
    {
        Interlocked.Increment(ref _bookSearchGeneration);
        CancellationTokenSource? cancellation = _bookSearchCancellation;
        _bookSearchCancellation = null;
        if (cancellation is not null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }

        ClearBookSearchResults();
        IsBookSearchRunning = false;
        BookSearchStatusText = string.Empty;
    }

    /// <summary>Row click / arrow-step navigation: marks the match current, jumps to its page and
    /// highlights the source box. Page navigation goes through <see cref="GoToPageAsync"/> so an
    /// active edit session resolves its draft and keeps editing on the destination page.</summary>
    internal async Task JumpToBookSearchResultAsync(BookSearchResultViewModel result)
    {
        if (!BookSearchResults.Contains(result))
        {
            return;
        }

        int pageNumber = result.PageIndex + 1;
        if (PageIndex != result.PageIndex)
        {
            await GoToPageAsync(pageNumber);
        }

        if (PageIndex != result.PageIndex || !BookSearchResults.Contains(result))
        {
            return;
        }

        SelectedBookSearchResult = result;
        if (!TryHighlightBox(result.BoxId))
        {
            Status = $"已跳转到第 {pageNumber} 页；当前版本中没有对应边界框。";
        }
    }

    private Task MoveBookSearchMatchAsync(int delta)
    {
        if (BookSearchResults.Count == 0)
        {
            return Task.CompletedTask;
        }

        int current = SelectedBookSearchResult?.Index ?? (delta > 0 ? -1 : 0);
        int next = (current + delta + BookSearchResults.Count) % BookSearchResults.Count;
        return JumpToBookSearchResultAsync(BookSearchResults[next]);
    }

    private bool IsCurrentBookSearchContext(int generation, HostServices services, int libraryGeneration,
        DocumentInstanceId documentId)
    {
        return Volatile.Read(ref _bookSearchGeneration) == generation &&
               _main.IsCurrentLibraryContext(services, libraryGeneration) &&
               TryGetDocumentInstanceId(out DocumentInstanceId current) &&
               current.Equals(documentId);
    }

    private CancellationTokenSource ReplaceBookSearchCancellation()
    {
        CancellationTokenSource? previous = _bookSearchCancellation;
        _bookSearchCancellation = null;
        if (previous is not null)
        {
            previous.Cancel();
            previous.Dispose();
        }

        _bookSearchCancellation = _workspaceCancellation.IsCancellationRequested
            ? new CancellationTokenSource()
            : CancellationTokenSource.CreateLinkedTokenSource(_workspaceCancellation.Token);
        return _bookSearchCancellation;
    }

    private void ClearBookSearchResults()
    {
        SelectedBookSearchResult = null;
        BookSearchResults.Clear();
        HasNoBookSearchResults = true;
        UpdateBookSearchMatchPosition();
        NotifyBookSearchMatchCommands();
    }

    private void ReplaceBookSearchResults(List<BookSearchResultViewModel> results)
    {
        SelectedBookSearchResult = null;
        _bookSearchResults.ReplaceWith(results);

        HasNoBookSearchResults = results.Count == 0;
        UpdateBookSearchMatchPosition();
        NotifyBookSearchMatchCommands();
    }

    private void UpdateBookSearchMatchPosition()
    {
        BookSearchMatchPositionText = BookSearchResults.Count == 0
            ? string.Empty
            : $"{(SelectedBookSearchResult?.Index ?? -1) + 1} / {BookSearchResults.Count}";
    }

    private void NotifyBookSearchMatchCommands()
    {
        _previousBookSearchMatchCommand?.NotifyCanExecuteChanged();
        _nextBookSearchMatchCommand?.NotifyCanExecuteChanged();
    }
}
