using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Search;
using Patchouli.UI.Services;
using Patchouli.Host.Composition;
using Patchouli.UI.ViewModels.Core;

namespace Patchouli.UI.ViewModels;

public sealed partial class SearchEvidenceViewModel : ViewModelBase
{
    private static readonly TimeSpan DefaultSearchThrottle = TimeSpan.FromMilliseconds(50);

    private readonly MainWindowViewModel _main;
    private readonly IScheduler _timingScheduler;
    private readonly IScheduler _uiScheduler;
    private readonly TimeSpan _searchThrottle;
    private readonly Subject<Unit> _searchRequests = new();
    private int _activeSearchId;
    private bool _suppressHitExpansionChanged;
    private BibliographicSearchFilterOptions? _filterOptions;

    public string DocumentInstanceId { get; set; } = "";
    [ObservableProperty] public partial string UnitId { get; private set; } = "";
    [ObservableProperty] public partial string VersionedUri { get; private set; } = "";
    [ObservableProperty] public partial string Markdown { get; internal set; } = "";
    [ObservableProperty] public partial string Output { get; internal set; } = "";
    [ObservableProperty] public partial string IndexStatus { get; private set; } = "";
    [ObservableProperty] public partial string AffectedScopesSummary { get; private set; } = "";
    [ObservableProperty] public partial string EstimatedTotalText { get; private set; } = "";
    public ObservableCollection<string> SearchUnits { get; } = new();
    public ObservableCollection<LibraryItemViewModel> BibliographicResults { get; } = new();
    [ObservableProperty] public partial bool HasBibliographicResults { get; private set; }
    public ObservableCollection<SearchFilterRowViewModel> FilterRows { get; } = new();
    [ObservableProperty] public partial bool HasFilterRows { get; private set; }
    public ObservableCollection<SearchHitItemViewModel> FullTextResults { get; } = new();

    [ObservableProperty] public partial bool HasResults { get; private set; }

    [ObservableProperty] public partial bool HasNoResults { get; private set; }

    [ObservableProperty] public partial bool AreAllHitsExpanded { get; private set; }

    [ObservableProperty] public partial string ToggleAllHitsExpandedText { get; private set; } = "全部展开";

    public event Action<SearchHitItemViewModel>? HitExpansionChanged;

    /// <summary>Raised once after all hit rows were expanded or collapsed together.</summary>
    public event Action<bool>? AllHitsExpansionChanged;

    public AsyncCommand RebuildCommand { get; }
    public AsyncCommand SearchCommand { get; }
    public AsyncCommand MarkdownCommand { get; }
    public AsyncCommand CopyMarkdownCommand { get; }
    public AsyncCommand ToggleAdvancedSearchCommand { get; }
    public AsyncCommand OpenAdvancedSearchCommand { get; }
    public RelayCommand AddFilterRowCommand { get; }
    public RelayCommand ToggleAllHitsExpandedCommand { get; }

    [ObservableProperty] public partial bool IsSearching { get; private set; }

    public IReadOnlyList<SearchModeOption> ModeOptions { get; } =
    [
        new(SearchMode.Bibliographic, "元数据筛选", "在书库题录元数据范围内筛选"),
        new(SearchMode.FullText, "全文搜索", "在 OCR 全文索引中检索")
    ];

    public SearchEvidenceViewModel(MainWindowViewModel m)
        : this(
            m,
            TaskPoolScheduler.Default,
            SynchronizationContext.Current is { } synchronizationContext
                ? new SynchronizationContextScheduler(synchronizationContext)
                : CurrentThreadScheduler.Instance)
    {
    }

    internal SearchEvidenceViewModel(
        MainWindowViewModel m,
        IScheduler timingScheduler,
        IScheduler uiScheduler,
        TimeSpan? searchThrottle = null)
    {
        _main = m;
        _timingScheduler = timingScheduler;
        _uiScheduler = uiScheduler;
        _searchThrottle = searchThrottle ?? DefaultSearchThrottle;

        Register(_searchRequests);

        SelectedMode = ModeOptions[1];
        RebuildCommand = new AsyncCommand(async () =>
        {
            HostServices s = await _main.ServicesAsync();
            Result a = await s.SearchUnits.RebuildForDocumentInstanceAsync(
                Patchouli.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId));
            Result b = await s.SearchIndex.RebuildFtsForDocumentInstanceAsync(
                Patchouli.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId));
            Output = a.IsSuccess && b.IsSuccess ? "搜索单元和 FTS 已重建。" : $"ERROR {a.ErrorCode ?? b.ErrorCode}";
            await _main.LogOperationAsync("rebuild_search_fts", Output);
        });
        SearchCommand = new AsyncCommand(ExecuteSearchCommandAsync);
        MarkdownCommand = new AsyncCommand(async () =>
        {
            Result<EvidencePageText> r = await ResolveMarkdownAsync(VersionedUri);
            Markdown = r.IsSuccess ? r.Value.Markdown : "";
            Output = r.IsSuccess ? Markdown : $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
        });
        CopyMarkdownCommand = new AsyncCommand(async () =>
        {
            if (string.IsNullOrWhiteSpace(Markdown))
            {
                Output = "ERROR validation_failed: 请先生成证据 Markdown。";
            }
            else
            {
                try
                {
                    await _main.Clipboard.SetTextAsync(Markdown);
                    Output = "Copied Evidence Markdown";
                }
                catch (Exception ex)
                {
                    Output = $"ERROR clipboard_unavailable: {ex.Message}";
                }
            }

            await _main.LogOperationAsync("copy_evidence_markdown", Output);
        });
        ToggleAdvancedSearchCommand = new AsyncCommand(async () =>
        {
            IsAdvancedSearchOpen = !IsAdvancedSearchOpen;
            if (IsAdvancedSearchOpen)
            {
                await EnsureFilterOptionsAsync();
            }
        });
        OpenAdvancedSearchCommand = new AsyncCommand(async () =>
        {
            IsAdvancedSearchOpen = true;
            await EnsureFilterOptionsAsync();
        });
        AddFilterRowCommand = new RelayCommand(_ => AddFilterRow());
        ToggleAllHitsExpandedCommand = new RelayCommand(_ => SetAllHitsExpanded(!AreAllHitsExpanded));

        Register(ReactiveUiFlow.SubscribeLatest(
            _searchRequests,
            _searchThrottle,
            _timingScheduler,
            _uiScheduler,
            ExecuteSearchAsync,
            exception => _main.ReportError($"搜索失败：{exception.Message}")));

        IObservable<EventPattern<PropertyChangedEventArgs>> propertyChanges = Observable
            .FromEventPattern<PropertyChangedEventHandler, PropertyChangedEventArgs>(
                h => PropertyChanged += h,
                h => PropertyChanged -= h);

        IObservable<Unit> modeChanged = propertyChanges
            .Where(e => e.EventArgs.PropertyName is nameof(SelectedMode) or nameof(IsBibliographicMode))
            .Select(_ => Unit.Default);

        IObservable<Unit> queryChanged = propertyChanges
            .Where(e => e.EventArgs.PropertyName == nameof(Query))
            .Select(_ => Unit.Default);

        IObservable<Unit> bibliographicRowsChanged = Observable
            .FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                h => BibliographicResults.CollectionChanged += h,
                h => BibliographicResults.CollectionChanged -= h)
            .Select(_ => Unit.Default);

        IObservable<Unit> filterRowsChanged = Observable
            .FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                h => FilterRows.CollectionChanged += h,
                h => FilterRows.CollectionChanged -= h)
            .Select(_ => Unit.Default);

        IObservable<EventPattern<NotifyCollectionChangedEventArgs>> fullTextCollectionChanges = Observable
            .FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                h => FullTextResults.CollectionChanged += h,
                h => FullTextResults.CollectionChanged -= h);

        IObservable<Unit> fullTextRowsChanged = fullTextCollectionChanges.Select(_ => Unit.Default);

        bibliographicRowsChanged
            .Select(_ => BibliographicResults.Count > 0)
            .BindOutput(this, has => HasBibliographicResults = has, ImmediateScheduler.Instance, null, true,
                BibliographicResults.Count > 0);

        filterRowsChanged
            .Select(_ => FilterRows.Count > 0)
            .BindOutput(this, has => HasFilterRows = has, ImmediateScheduler.Instance, null, true,
                FilterRows.Count > 0);

        IObservable<Unit> resultsTrigger = Observable.Merge(
            bibliographicRowsChanged,
            fullTextRowsChanged,
            modeChanged);

        resultsTrigger
            .Select(_ => IsBibliographicMode ? BibliographicResults.Count > 0 : FullTextResults.Count > 0)
            .BindOutput(this, has => HasResults = has, ImmediateScheduler.Instance, null, true,
                IsBibliographicMode ? BibliographicResults.Count > 0 : FullTextResults.Count > 0);

        Observable.Merge(resultsTrigger, queryChanged)
            .Select(_ =>
            {
                bool hasResults = IsBibliographicMode ? BibliographicResults.Count > 0 : FullTextResults.Count > 0;
                return !hasResults && !string.IsNullOrWhiteSpace(Query);
            })
            .BindOutput(this, no => HasNoResults = no, ImmediateScheduler.Instance, null, true,
                !(IsBibliographicMode ? BibliographicResults.Count > 0 : FullTextResults.Count > 0) &&
                !string.IsNullOrWhiteSpace(Query));

        IObservable<SearchHitItemViewModel?> hitChanges = fullTextCollectionChanges
            .Select(_ =>
            {
                SearchHitItemViewModel[] items = FullTextResults.ToArray();
                if (items.Length == 0)
                {
                    return Observable.Return<SearchHitItemViewModel?>(null);
                }

                IObservable<SearchHitItemViewModel?> itemChanges = items
                    .Select(item => Observable
                        .FromEventPattern<PropertyChangedEventHandler, PropertyChangedEventArgs>(
                            h => item.PropertyChanged += h,
                            h => item.PropertyChanged -= h)
                        .Where(e => e.EventArgs.PropertyName == nameof(SearchHitItemViewModel.IsExpanded))
                        .Select(_ => (SearchHitItemViewModel?)item))
                    .Merge();

                return itemChanges.StartWith((SearchHitItemViewModel?)null);
            })
            .Switch()
            .Publish()
            .RefCount();

        hitChanges
            .Select(_ => FullTextResults.Count > 0 && FullTextResults.All(hit => hit.IsExpanded))
            .BindOutput(this, all => AreAllHitsExpanded = all, ImmediateScheduler.Instance, null, true,
                FullTextResults.Count > 0 && FullTextResults.All(hit => hit.IsExpanded));

        hitChanges
            .Select(_ => FullTextResults.Count > 0 && FullTextResults.All(hit => hit.IsExpanded) ? "全部收起" : "全部展开")
            .BindOutput(this, text => ToggleAllHitsExpandedText = text, ImmediateScheduler.Instance, null, true,
                FullTextResults.Count > 0 && FullTextResults.All(hit => hit.IsExpanded) ? "全部收起" : "全部展开");

        Register(hitChanges
            .ObserveOn(ImmediateScheduler.Instance)
            .Subscribe(item =>
            {
                if (item is not null && !_suppressHitExpansionChanged)
                {
                    HitExpansionChanged?.Invoke(item);
                }
            }));

        FilterRows.Add(new SearchFilterRowViewModel(this, SearchFilterKeyOption.All[0]));
    }

    [ObservableProperty] public partial string Query { get; set; } = "";

    partial void OnQueryChanged(string value)
    {
        foreach (SearchFilterRowViewModel row in FilterRows)
        {
            row.RefreshValueFromQuery();
        }
    }

    [ObservableProperty] public partial SearchModeOption SelectedMode { get; set; } = null!;

    public SearchMode Mode => SelectedMode.Mode;
    public string ModeDisplay => SelectedMode.Label;
    public bool IsBibliographicMode => Mode == SearchMode.Bibliographic;
    public bool IsFullTextMode => Mode == SearchMode.FullText;

    [ObservableProperty] public partial bool IsAdvancedSearchOpen { get; private set; }

    partial void OnIsAdvancedSearchOpenChanged(bool value)
    {
        Raise(nameof(AdvancedSearchToggleText));
    }

    public string AdvancedSearchToggleText => "高级搜索";

    public void AddFilterRow()
    {
        FilterRows.Add(new SearchFilterRowViewModel(this));
    }

    public void RemoveFilterRow(SearchFilterRowViewModel row)
    {
        FilterRows.Remove(row);
    }

    public IReadOnlyList<SearchFilterOption> FilterOptionsFor(string key)
    {
        BibliographicSearchFilterOptions? options = _filterOptions;
        if (options is null)
        {
            return Array.Empty<SearchFilterOption>();
        }

        return key switch
        {
            BibliographicSearchFilterKeys.ItemType => options.ItemTypes,
            BibliographicSearchFilterKeys.ItemStatus => options.ItemStatuses,
            BibliographicSearchFilterKeys.PrimaryDocumentOcrIndexStatus => options.OcrIndexStatuses,
            BibliographicSearchFilterKeys.CollectionId => options.Collections,
            BibliographicSearchFilterKeys.Citable =>
            [
                new SearchFilterOption("true", "可引用"),
                new SearchFilterOption("false", "不可引用")
            ],
            _ => Array.Empty<SearchFilterOption>()
        };
    }

    public async Task EnsureFilterOptionsAsync()
    {
        if (_filterOptions is not null)
        {
            return;
        }

        await LoadFilterOptionsAsync();
    }

    /// <summary>
    /// Re-reads the filter option catalogs after a committed Library change so an open advanced
    /// search observes collection create/rename/dissolve without a tab reload. A no-op until the
    /// options were first requested, so the reload stays cheap and lazy.
    /// </summary>
    public async Task ReloadFilterOptionsAsync()
    {
        if (_filterOptions is null)
        {
            return;
        }

        await LoadFilterOptionsAsync();
    }

    private async Task LoadFilterOptionsAsync()
    {
        HostServices services = await _main.ServicesAsync();
        Result<BibliographicSearchFilterOptions> options = await services.LibraryItems.GetSearchFilterOptionsAsync();
        if (options.IsFailure)
        {
            _main.Report($"筛选选项加载失败：{options.ErrorMessage}");
            return;
        }

        _filterOptions = options.Value;
        foreach (SearchFilterRowViewModel row in FilterRows)
        {
            row.RefreshAvailableValues();
        }
    }

    public BibliographicItemSearch BuildBibliographicSearch()
    {
        List<BibliographicSearchFilter> filters = new();
        foreach (SearchFilterRowViewModel row in FilterRows)
        {
            BibliographicSearchFilter? filter = row.ToFilter();
            if (filter is not null)
            {
                filters.Add(filter);
            }
        }

        return new BibliographicItemSearch(
            string.IsNullOrWhiteSpace(Query) ? null : Query.Trim(),
            filters);
    }

    [ExcludeFromDerivedGeneration] public bool ShowItemTypeColumn => GetColumnVisibility("ItemType");
    [ExcludeFromDerivedGeneration] public bool ShowYearColumn => GetColumnVisibility("Year");
    [ExcludeFromDerivedGeneration] public bool ShowAuthorColumn => GetColumnVisibility("Author");
    [ExcludeFromDerivedGeneration] public bool ShowTitleColumn => GetColumnVisibility("Title");
    [ExcludeFromDerivedGeneration] public bool ShowSourceColumn => GetColumnVisibility("Source");
    [ExcludeFromDerivedGeneration] public bool ShowStatusColumn => GetColumnVisibility("Status");
    [ExcludeFromDerivedGeneration] public bool ShowPagesColumn => GetColumnVisibility("Pages");
    [ExcludeFromDerivedGeneration] public bool ShowFileColumn => GetColumnVisibility("File");

    public bool TryGetColumnWidth(string key, out double width)
    {
        return _main.AppOptions.Ui.LibraryGridColumnWidths.TryGetValue(key, out width);
    }

    public bool TryGetColumnOrder(string key, out int order)
    {
        return _main.AppOptions.Ui.LibraryGridColumnOrder.TryGetValue(key, out order);
    }

    public async Task CopyVersionedUriAsync(string? versionedUri)
    {
        if (string.IsNullOrWhiteSpace(versionedUri))
        {
            Output = "ERROR validation_failed: 缺少版本化证据 URI。";
            await _main.LogOperationAsync("copy_evidence_uri", Output);
            return;
        }

        try
        {
            await _main.Clipboard.SetTextAsync(versionedUri);
            VersionedUri = versionedUri;
            Output = "Copied Evidence URI";
        }
        catch (Exception ex)
        {
            Output = $"ERROR clipboard_unavailable: {ex.Message}";
        }

        await _main.LogOperationAsync("copy_evidence_uri", Output);
    }

    public async Task CopyVersionedUriForSearchUnitAsync(SearchMatchedUnitViewModel unit)
    {
        string uri = unit.VersionedUri;
        string text = $"{uri}\n\n> {unit.Text}";
        await CopyVersionedUriAsync(text);
    }

    public async Task CopyEvidenceMarkdownAsync(string? versionedUri)
    {
        if (string.IsNullOrWhiteSpace(versionedUri))
        {
            Output = "ERROR validation_failed: 缺少版本化证据 URI。";
            await _main.LogOperationAsync("copy_search_result_evidence_markdown", Output);
            return;
        }

        Result<EvidencePageText> markdown = await ResolveMarkdownAsync(versionedUri);
        if (markdown.IsFailure)
        {
            Output = $"ERROR {markdown.ErrorCode}: {markdown.ErrorMessage}";
            _main.Report(Output);
            await _main.LogOperationAsync("copy_search_result_evidence_markdown", Output);
            return;
        }

        try
        {
            await _main.Clipboard.SetTextAsync(markdown.Value.Markdown);
            VersionedUri = versionedUri;
            Markdown = markdown.Value.Markdown;
            Output = "Copied Evidence Markdown";
            _main.Report("已复制证据 Markdown。");
        }
        catch (Exception ex)
        {
            Output = $"ERROR clipboard_unavailable: {ex.Message}";
        }

        await _main.LogOperationAsync("copy_search_result_evidence_markdown", Output);
    }

    public async Task CopyEvidenceMarkdownForSearchUnitAsync(SearchMatchedUnitViewModel unit)
    {
        await CopyEvidenceMarkdownAsync(unit.VersionedUri);
    }

    public string BuildVersionedUri(SearchMatchedUnitViewModel unit)
    {
        return unit.VersionedUri;
    }

    public void RaiseMarkdown()
    {
        Raise(nameof(Markdown));
    }

    public void RaiseOutput()
    {
        Raise(nameof(Output));
    }

    public void RaiseColumnVisibility()
    {
        Raise(nameof(ShowItemTypeColumn));
        Raise(nameof(ShowYearColumn));
        Raise(nameof(ShowAuthorColumn));
        Raise(nameof(ShowTitleColumn));
        Raise(nameof(ShowSourceColumn));
        Raise(nameof(ShowStatusColumn));
        Raise(nameof(ShowPagesColumn));
        Raise(nameof(ShowFileColumn));
    }

    private bool GetColumnVisibility(string key)
    {
        return !_main.AppOptions.Ui.LibraryGridVisibleColumns.TryGetValue(key, out bool visible) || visible;
    }

    private async Task<Result<EvidencePageText>> ResolveMarkdownAsync(string? versionedUri)
    {
        if (string.IsNullOrWhiteSpace(versionedUri))
        {
            return Result<EvidencePageText>.Failure(AppErrorCodes.ValidationFailed, "缺少版本化证据 URI。");
        }

        PatchouliNavigationParseResult parsed = PatchouliUriNavigationParser.ParseInput(versionedUri);
        if (!parsed.IsSuccess || parsed.Target is not { Kind: PatchouliNavigationKind.TextPage } target)
        {
            return Result<EvidencePageText>.Failure(AppErrorCodes.ValidationFailed, "无法解析版本化证据 URI。");
        }

        HostServices services = await _main.ServicesAsync();
        return await services.VersionedEvidenceReader.GetBoxTextAsync(
            Patchouli.Core.Ids.DocumentInstanceId.Parse(target.ResourceId),
            (target.PageIndex ?? 0) + 1,
            target.RevisionId,
            target.BoxId);
    }

    private Task ExecuteSearchCommandAsync()
    {
        if (!_searchRequests.IsDisposed)
        {
            _searchRequests.OnNext(Unit.Default);
        }

        return Task.CompletedTask;
    }

    private async Task ExecuteSearchAsync(CancellationToken cancellationToken)
    {
        int searchId = Interlocked.Increment(ref _activeSearchId);
        IsSearching = true;
        try
        {
            if (IsBibliographicMode)
            {
                await SearchBibliographicAsync(cancellationToken);
            }
            else
            {
                await SearchFullTextAsync(cancellationToken);
            }
        }
        finally
        {
            if (Volatile.Read(ref _activeSearchId) == searchId)
            {
                IsSearching = false;
            }
        }
    }

    private async Task RunOnUiThreadAsync(Action action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource<Unit> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IDisposable scheduled = _uiScheduler.Schedule(() =>
        {
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(cancellationToken);
                }
                else
                {
                    action();
                    tcs.TrySetResult(Unit.Default);
                }
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        if (!cancellationToken.CanBeCanceled)
        {
            await tcs.Task.ConfigureAwait(false);
            return;
        }

        CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            scheduled.Dispose();
            tcs.TrySetCanceled(cancellationToken);
        });

        try
        {
            await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            registration.Dispose();
        }
    }

    private async Task SearchBibliographicAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BibliographicItemSearch request = BuildBibliographicSearch();
        if (request.Query is null && request.Filters.Count == 0)
        {
            await RunOnUiThreadAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ClearResults();
                Output = "";
                RaiseResultProperties();
                _main.Report("请输入搜索词或添加筛选条件。");
            }, cancellationToken);
            return;
        }

        HostServices services = await _main.ServicesAsync();
        cancellationToken.ThrowIfCancellationRequested();
        Result<IReadOnlyList<LibraryItemRow>> result = await Task.Run(() =>
            services.LibraryItems.SearchRowsAsync(request, cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (result.IsFailure)
        {
            await RunOnUiThreadAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ClearResults();
                IndexStatus = "";
                AffectedScopesSummary = "";
                EstimatedTotalText = "";
                Output = $"ERROR {result.ErrorCode}: {result.ErrorMessage}";
                RaiseResultProperties();
                _main.Report(Output);
            }, cancellationToken);
            return;
        }

        List<LibraryItemViewModel> items = new();
        foreach (LibraryItemRow row in result.Value)
        {
            items.Add(CreateItemViewModel(row));
        }

        await RunOnUiThreadAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            BibliographicResults.Clear();
            foreach (LibraryItemViewModel item in items)
            {
                BibliographicResults.Add(item);
            }

            UnitId = "";
            VersionedUri = "";
            IndexStatus = "";
            AffectedScopesSummary = "";
            EstimatedTotalText = $"{result.Value.Count} 条题录";
            Output = "";
            RaiseColumnVisibility();
            RaiseResultProperties();
            _main.Report($"筛选完成：{result.Value.Count} 条题录。");
        }, cancellationToken);
    }

    private async Task SearchFullTextAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(Query))
        {
            await RunOnUiThreadAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ClearResults();
                Output = "";
                RaiseResultProperties();
                _main.Report("请输入搜索词。");
            }, cancellationToken);
            return;
        }

        List<BibliographicSearchFilter> itemFilters = new();
        foreach (SearchFilterRowViewModel row in FilterRows)
        {
            if (row.IsKeyword)
            {
                continue;
            }

            BibliographicSearchFilter? filter = row.ToFilter();
            if (filter is not null)
            {
                itemFilters.Add(filter);
            }
        }

        HostServices services = await _main.ServicesAsync();
        cancellationToken.ThrowIfCancellationRequested();

        Result<SearchResultPage> r = await Task.Run(() => services.Search.SearchLibraryAsync(
            new SearchRequest(Query) { ItemFilters = itemFilters }, cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (r.IsFailure)
        {
            await RunOnUiThreadAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ClearResults();
                IndexStatus = "";
                AffectedScopesSummary = "";
                EstimatedTotalText = "";
                Output = $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
                RaiseResultProperties();
                _main.Report(Output);
            }, cancellationToken);
            return;
        }

        Dictionary<ItemId, IReadOnlyList<SearchPageResult>> pagesByItem = r.Value.Results
            .GroupBy(page => page.ItemId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<SearchPageResult>)group.ToArray());
        Result<IReadOnlyList<LibraryItemRow>> items = await Task.Run(() =>
            services.LibraryItems.GetRowsByIdsAsync(pagesByItem.Keys, cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        Dictionary<ItemId, LibraryItemRow> rowsByItem = items.IsSuccess
            ? items.Value.ToDictionary(row => row.ItemId)
            : new Dictionary<ItemId, LibraryItemRow>();

        List<SearchHitItemViewModel> hits = new();
        List<string> units = new();
        string? firstMatchedUnit = default;
        foreach ((ItemId itemId, IReadOnlyList<SearchPageResult> itemPages) in pagesByItem)
        {
            List<SearchHitSnippetViewModel> snippets = new();
            foreach (SearchPageResult page in itemPages.OrderBy(page => page.PageIndex))
            {
                foreach (SearchMatchedUnit unit in page.MatchedUnits)
                {
                    units.Add($"{unit.UnitId} | {unit.Text}");
                    SearchMatchedUnitViewModel unitVm = new(
                        unit.UnitId.ToString(),
                        unit.Text,
                        unit.BoxType,
                        unit.Ordinal,
                        unit.IsMatch,
                        page.DocumentInstanceId,
                        page.PageIndex,
                        unit.BoxId,
                        unit.TreeRevisionId);
                    snippets.Add(new SearchHitSnippetViewModel(unitVm, Query, JumpToHitAsync));
                    firstMatchedUnit ??= unit.UnitId.ToString();
                }
            }

            hits.Add(rowsByItem.TryGetValue(itemId, out LibraryItemRow? row)
                ? SearchHitItemViewModel.HitItem(CreateItemViewModel(row), snippets)
                : SearchHitItemViewModel.FallbackItem(itemPages[0].ItemTitle, snippets));
        }

        await RunOnUiThreadAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            SearchUnits.Clear();
            foreach (string unit in units)
            {
                SearchUnits.Add(unit);
            }

            FullTextResults.Clear();
            foreach (SearchHitItemViewModel hit in hits)
            {
                FullTextResults.Add(hit);
            }

            UnitId = firstMatchedUnit ?? "";
            VersionedUri = "";
            IndexStatus = r.Value.IndexStatus;
            AffectedScopesSummary = r.Value.AffectedScopesSummary ?? "";
            EstimatedTotalText = r.Value.EstimatedTotal?.ToString() ?? $"{r.Value.Results.Count} 页";
            Output = JsonSerializer.Serialize(r.Value, new JsonSerializerOptions { WriteIndented = true });
            RaiseColumnVisibility();
            RaiseResultProperties();
            _main.Report(hits.Count > 0
                ? $"搜索完成：{hits.Count} 条题录命中，索引状态={IndexStatus}。"
                : $"搜索完成：没有命中结果，索引状态={IndexStatus}。");
        }, cancellationToken);
    }

    private Task JumpToHitAsync(SearchMatchedUnitViewModel unit)
    {
        return _main.NavigateToSearchHitAsync(unit.VersionedUri);
    }

    private LibraryItemViewModel CreateItemViewModel(LibraryItemRow row)
    {
        return new LibraryItemViewModel(
            row.ItemId.ToString(),
            row.Title,
            row.ItemType,
            row.Authors,
            row.Year ?? "",
            row.PublicationTitle ?? "",
            row.Publisher,
            row.DocumentInstanceId?.ToString(),
            row.FileAssetId,
            row.LinkedFileName ?? "",
            row.SourcePath,
            row.PageCount,
            row.SearchUnitCount,
            row.IndexStatus,
            RunOcrForItemAsync,
            item => _main.EditItemByIdAsync(item.ItemId),
            item => _main.ShowReadingAsync(item),
            createdAt: row.CreatedAt,
            primaryDocumentOcrIndexState: row.PrimaryDocumentOcrIndexState,
            hasOcrText: row.HasOcrText);
    }

    private Task RunOcrForItemAsync(LibraryItemViewModel item)
    {
        return _main.Shell.RunOcrForItemAsync(item);
    }

    private void SetAllHitsExpanded(bool expanded)
    {
        _suppressHitExpansionChanged = true;
        try
        {
            foreach (SearchHitItemViewModel hit in FullTextResults)
            {
                hit.IsExpanded = expanded;
            }
        }
        finally
        {
            _suppressHitExpansionChanged = false;
        }

        AllHitsExpansionChanged?.Invoke(expanded);
    }

    private void ClearResults()
    {
        BibliographicResults.Clear();
        SearchUnits.Clear();
        FullTextResults.Clear();
        UnitId = "";
        VersionedUri = "";
        IndexStatus = "";
        AffectedScopesSummary = "";
        EstimatedTotalText = "";
    }

    private void RaiseResultProperties()
    {
        Raise(nameof(BibliographicResults));
        Raise(nameof(FullTextResults));
    }
}
