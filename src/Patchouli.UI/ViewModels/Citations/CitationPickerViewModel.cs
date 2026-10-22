using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Search;
using Patchouli.Host.CitationInterop;

namespace Patchouli.UI.ViewModels.Citations;

/// <summary>One search result row in the citation picker.</summary>
public sealed partial class CitationPickerResultViewModel : ViewModelBase
{
    public CitationPickerResultViewModel(LibraryItemRow row, ICommand addCommand)
    {
        Row = row;
        AddCommand = addCommand;
    }

    public LibraryItemRow Row { get; }

    public ICommand AddCommand { get; }

    [ExcludeFromDerivedGeneration] public ItemId ItemId => Row.ItemId;

    [ExcludeFromDerivedGeneration] public string Title => Row.Title;

    [ExcludeFromDerivedGeneration]
    public string MetaLine
    {
        get
        {
            List<string> parts = new();
            if (!string.IsNullOrWhiteSpace(Row.Authors))
            {
                parts.Add(Row.Authors);
            }

            if (!string.IsNullOrWhiteSpace(Row.Year))
            {
                parts.Add(Row.Year!);
            }

            if (!string.IsNullOrWhiteSpace(Row.PublicationTitle))
            {
                parts.Add(Row.PublicationTitle!);
            }

            return string.Join(" · ", parts);
        }
    }

    [ObservableProperty] public partial bool IsPicked { get; set; }
}

/// <summary>One picked entry with its editable CAYW modifiers.</summary>
public sealed partial class CitationPickerEntryViewModel : ViewModelBase
{
    public CitationPickerEntryViewModel(
        LibraryItemRow row,
        ICommand removeCommand,
        ICommand moveUpCommand,
        ICommand moveDownCommand)
    {
        Row = row;
        RemoveCommand = removeCommand;
        MoveUpCommand = moveUpCommand;
        MoveDownCommand = moveDownCommand;
    }

    public LibraryItemRow Row { get; }

    public ICommand RemoveCommand { get; }

    public ICommand MoveUpCommand { get; }

    public ICommand MoveDownCommand { get; }

    [ExcludeFromDerivedGeneration] public ItemId ItemId => Row.ItemId;

    [ExcludeFromDerivedGeneration] public string Title => Row.Title;

    [ExcludeFromDerivedGeneration] public string MetaLine => $"{Row.Authors} {Row.Year}".Trim();

    [ObservableProperty] public partial string Locator { get; set; } = "";

    [ObservableProperty] public partial string Label { get; set; } = "";

    [ObservableProperty] public partial string Prefix { get; set; } = "";

    [ObservableProperty] public partial string Suffix { get; set; } = "";

    [ObservableProperty] public partial bool SuppressAuthor { get; set; }

    public CitationBridgePickEntry ToBridgeEntry()
    {
        return new CitationBridgePickEntry(
            ItemId,
            NullIfWhiteSpace(Locator),
            NullIfWhiteSpace(Label),
            NullIfWhiteSpace(Prefix),
            NullIfWhiteSpace(Suffix),
            SuppressAuthor);
    }

    private static string? NullIfWhiteSpace(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

/// <summary>
/// The CAYW citation picker (issue #14 §4/§8 in the plan): debounced library
/// search with paging, multi-pick with reorder, and per-entry locator / label /
/// prefix / suffix / suppress-author editing. Closing the window without
/// confirming is a cancellation, which the CAYW handler turns into an empty
/// (successful) response.
/// </summary>
public sealed partial class CitationPickerViewModel : ViewModelBase
{
    private const int PageSize = 50;

    private readonly ILibraryItemQueryService _query;
    private readonly TimeSpan _debounce;
    private CancellationTokenSource? _searchCancellation;
    private LibraryItemCursor? _nextCursor;
    private int _searchLimit = PageSize;

    public CitationPickerViewModel(ILibraryItemQueryService query, TimeSpan? debounce = null)
    {
        _query = query;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(300);

        AddResultCommand = new CommunityToolkit.Mvvm.Input.RelayCommand<CitationPickerResultViewModel>(AddResult);
        RemoveEntryCommand = new CommunityToolkit.Mvvm.Input.RelayCommand<CitationPickerEntryViewModel>(RemoveEntry);
        MoveUpCommand =
            new CommunityToolkit.Mvvm.Input.RelayCommand<CitationPickerEntryViewModel>(entry => MoveEntry(entry, -1));
        MoveDownCommand =
            new CommunityToolkit.Mvvm.Input.RelayCommand<CitationPickerEntryViewModel>(entry => MoveEntry(entry, +1));
        LoadMoreCommand = new AsyncCommand(LoadMoreAsync, () => HasMore && !IsSearching);
        ConfirmCommand = new RelayCommand(_ => Confirm());
        CancelCommand = new RelayCommand(_ => Cancel());
    }

    public ObservableCollection<CitationPickerResultViewModel> Results { get; } = new();

    public ObservableCollection<CitationPickerEntryViewModel> Picked { get; } = new();

    public ICommand AddResultCommand { get; }

    public ICommand RemoveEntryCommand { get; }

    public ICommand MoveUpCommand { get; }

    public ICommand MoveDownCommand { get; }

    public AsyncCommand LoadMoreCommand { get; }

    public ICommand ConfirmCommand { get; }

    public ICommand CancelCommand { get; }

    /// <summary>Set by the window (DialogCloseBinding) to close with a result.</summary>
    public Action<CitationPickBridgeResult>? RequestClose { get; set; }

    [ObservableProperty] public partial string SearchText { get; set; } = "";

    [ObservableProperty] public partial string StatusText { get; set; } = "";

    [ObservableProperty] public partial bool IsSearching { get; set; }

    [ObservableProperty] public partial bool HasMore { get; set; }

    public bool HasPicked => Picked.Count > 0;

    public string PickedSummary => Picked.Count == 0 ? "尚未选择条目" : $"已选 {Picked.Count} 条";

    partial void OnSearchTextChanged(string value)
    {
        ScheduleSearch();
    }

    /// <summary>Loads the initial (most recent) result page.</summary>
    public Task InitializeAsync()
    {
        return RunSearchAsync(true);
    }

    /// <summary>Runs a search immediately, bypassing the debounce (tests).</summary>
    public Task SearchNowAsync()
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        return RunSearchAsync(true, _searchCancellation.Token);
    }

    private void ScheduleSearch()
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        CancellationToken token = _searchCancellation.Token;
        _ = DebouncedSearchAsync(token);
    }

    private async Task DebouncedSearchAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(_debounce, token);
            await RunSearchAsync(true, token);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer keystroke.
        }
    }

    private async Task RunSearchAsync(bool reset, CancellationToken cancellationToken = default)
    {
        if (reset)
        {
            _nextCursor = null;
            _searchLimit = PageSize;
            Results.Clear();
        }

        string text = SearchText.Trim();
        IsSearching = true;
        StatusText = "";
        try
        {
            IReadOnlyList<LibraryItemRow> rows;
            if (text.Length == 0)
            {
                Result<LibraryItemPage> page =
                    await _query.ListRowsAsync(PageSize, _nextCursor, null, cancellationToken);
                if (page.IsFailure)
                {
                    StatusText = $"搜索失败：{page.ErrorMessage}";
                    HasMore = false;
                    return;
                }

                rows = page.Value.Rows;
                _nextCursor = page.Value.NextCursor;
                HasMore = _nextCursor is not null;
            }
            else
            {
                Result<IReadOnlyList<LibraryItemRow>> found = await _query.SearchRowsAsync(
                    new BibliographicItemSearch(text, [], _searchLimit), cancellationToken);
                if (found.IsFailure)
                {
                    StatusText = $"搜索失败：{found.ErrorMessage}";
                    HasMore = false;
                    return;
                }

                rows = found.Value;
                HasMore = rows.Count >= _searchLimit && _searchLimit < 500;
            }

            foreach (LibraryItemRow row in rows)
            {
                if (string.Equals(row.ItemType, "general", StringComparison.Ordinal))
                {
                    continue;
                }

                if (Results.Any(result => result.ItemId == row.ItemId))
                {
                    continue;
                }

                CitationPickerResultViewModel result = new(row, AddResultCommand)
                {
                    IsPicked = Picked.Any(entry => entry.ItemId == row.ItemId)
                };
                Results.Add(result);
            }

            if (Results.Count == 0)
            {
                StatusText = text.Length == 0 ? "书库中没有可引用的条目。" : $"没有找到与「{text}」匹配的条目。";
            }
        }
        catch (OperationCanceledException)
        {
            // Window closing or a newer search superseded this one.
        }
        finally
        {
            IsSearching = false;
            LoadMoreCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task LoadMoreAsync()
    {
        if (SearchText.Trim().Length > 0)
        {
            // Search has no cursor; widen the limit and top up (RunSearchAsync
            // de-duplicates rows already shown).
            _searchLimit += PageSize;
        }

        await RunSearchAsync(false);
    }

    private void AddResult(CitationPickerResultViewModel? result)
    {
        if (result is null)
        {
            return;
        }

        if (Picked.Any(entry => entry.ItemId == result.ItemId))
        {
            StatusText = "该条目已在引用列表中。";
            return;
        }

        if (Picked.Count >= CitationInteropService.MaxPickEntries)
        {
            // Refuse here, at the add path: picking past the service's pick
            // cap would only surface as a whole-pick rejection at confirm
            // time (ValidatePickAsync), after the user built the list.
            StatusText = $"一次引用最多可选择 {CitationInteropService.MaxPickEntries} 条，请先移除部分条目。";
            return;
        }

        Picked.Add(new CitationPickerEntryViewModel(result.Row, RemoveEntryCommand, MoveUpCommand, MoveDownCommand));
        result.IsPicked = true;
        Raise(nameof(HasPicked));
        Raise(nameof(PickedSummary));
    }

    private void RemoveEntry(CitationPickerEntryViewModel? entry)
    {
        if (entry is null || !Picked.Remove(entry))
        {
            return;
        }

        CitationPickerResultViewModel? result = Results.FirstOrDefault(candidate => candidate.ItemId == entry.ItemId);
        if (result is not null)
        {
            result.IsPicked = false;
        }

        Raise(nameof(HasPicked));
        Raise(nameof(PickedSummary));
    }

    private void MoveEntry(CitationPickerEntryViewModel? entry, int delta)
    {
        if (entry is null)
        {
            return;
        }

        int index = Picked.IndexOf(entry);
        int target = index + delta;
        if (index < 0 || target < 0 || target >= Picked.Count)
        {
            return;
        }

        Picked.Move(index, target);
    }

    private void Confirm()
    {
        CitationBridgePickEntry[] entries = Picked.Select(static entry => entry.ToBridgeEntry()).ToArray();
        RequestClose?.Invoke(entries.Length == 0
            ? CitationPickBridgeResult.CancelledResult
            : new CitationPickBridgeResult(CitationPickOutcome.Confirmed, entries));
    }

    private void Cancel()
    {
        RequestClose?.Invoke(CitationPickBridgeResult.CancelledResult);
    }
}
