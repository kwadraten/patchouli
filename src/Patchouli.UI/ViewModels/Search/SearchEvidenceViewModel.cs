using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Search;
using Patchouli.UI.Services;
using Patchouli.Host.Composition;

namespace Patchouli.UI.ViewModels;

public sealed class SearchEvidenceViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private SearchModeOption _selectedMode;
    private bool _isSearching;
    private bool _isAdvancedSearchOpen;
    private bool _suppressHitExpansionChanged;
    private string _query = "";
    private BibliographicSearchFilterOptions? _filterOptions;

    public string DocumentInstanceId { get; set; } = "";
    public string UnitId { get; set; } = "";
    public string VersionedUri { get; set; } = "";
    public string Markdown { get; set; } = "";
    public string Output { get; set; } = "";
    public string IndexStatus { get; private set; } = "";
    public string AffectedScopesSummary { get; private set; } = "";
    public string EstimatedTotalText { get; private set; } = "";
    public ObservableCollection<string> SearchUnits { get; } = new();
    public ObservableCollection<LibraryItemViewModel> BibliographicResults { get; } = new();
    public bool HasBibliographicResults => BibliographicResults.Count > 0;
    public ObservableCollection<SearchFilterRowViewModel> FilterRows { get; } = new();
    public bool HasFilterRows => FilterRows.Count > 0;
    public ObservableCollection<SearchHitItemViewModel> FullTextResults { get; } = new();
    public bool HasResults => IsBibliographicMode ? HasBibliographicResults : FullTextResults.Count > 0;
    public bool HasNoResults => !HasResults && !string.IsNullOrWhiteSpace(Query);
    public bool AreAllHitsExpanded => FullTextResults.Count > 0 && FullTextResults.All(hit => hit.IsExpanded);
    public string ToggleAllHitsExpandedText => AreAllHitsExpanded ? "全部收起" : "全部展开";

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

    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            if (_isSearching == value)
            {
                return;
            }

            _isSearching = value;
            Raise();
        }
    }

    public IReadOnlyList<SearchModeOption> ModeOptions { get; } =
    [
        new(SearchMode.Bibliographic, "元数据筛选", "在书库题录元数据范围内筛选"),
        new(SearchMode.FullText, "全文搜索", "在 OCR 全文索引中检索")
    ];

    public SearchEvidenceViewModel(MainWindowViewModel m)
    {
        _main = m;
        _selectedMode = ModeOptions[1];
        RebuildCommand = new AsyncCommand(async () =>
        {
            HostServices s = await _main.ServicesAsync();
            Result a = await s.SearchUnits.RebuildForDocumentInstanceAsync(
                Patchouli.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId));
            Result b = await s.SearchIndex.RebuildFtsForDocumentInstanceAsync(
                Patchouli.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId));
            Output = a.IsSuccess && b.IsSuccess ? "搜索单元和 FTS 已重建。" : $"ERROR {a.ErrorCode ?? b.ErrorCode}";
            Raise(nameof(Output));
            await _main.LogOperationAsync("rebuild_search_fts", Output);
        });
        SearchCommand = new AsyncCommand(SearchAsync);
        MarkdownCommand = new AsyncCommand(async () =>
        {
            Result<EvidencePageText> r = await ResolveMarkdownAsync(VersionedUri);
            Markdown = r.IsSuccess ? r.Value.Markdown : "";
            Output = r.IsSuccess ? Markdown : $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
            Raise(nameof(Markdown));
            Raise(nameof(Output));
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

            Raise(nameof(Output));
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
        FullTextResults.CollectionChanged += OnFullTextResultsChanged;
        FilterRows.Add(new SearchFilterRowViewModel(this, SearchFilterKeyOption.All[0]));
    }

    public string Query
    {
        get => _query;
        set
        {
            if (_query == value)
            {
                return;
            }

            _query = value;
            Raise();
            foreach (SearchFilterRowViewModel row in FilterRows)
            {
                row.RefreshValueFromQuery();
            }
        }
    }

    public SearchModeOption SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (_selectedMode == value)
            {
                return;
            }

            _selectedMode = value;
            Raise();
            Raise(nameof(Mode));
            Raise(nameof(IsBibliographicMode));
            Raise(nameof(IsFullTextMode));
            Raise(nameof(HasResults));
            Raise(nameof(HasNoResults));
        }
    }

    public SearchMode Mode => _selectedMode.Mode;
    public string ModeDisplay => _selectedMode.Label;
    public bool IsBibliographicMode => Mode == SearchMode.Bibliographic;
    public bool IsFullTextMode => Mode == SearchMode.FullText;

    public bool IsAdvancedSearchOpen
    {
        get => _isAdvancedSearchOpen;
        private set
        {
            if (_isAdvancedSearchOpen == value)
            {
                return;
            }

            _isAdvancedSearchOpen = value;
            Raise();
            Raise(nameof(AdvancedSearchToggleText));
        }
    }

    public string AdvancedSearchToggleText => "高级搜索";

    public void AddFilterRow()
    {
        FilterRows.Add(new SearchFilterRowViewModel(this));
        Raise(nameof(HasFilterRows));
    }

    public void RemoveFilterRow(SearchFilterRowViewModel row)
    {
        FilterRows.Remove(row);
        Raise(nameof(HasFilterRows));
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

    public bool ShowItemTypeColumn => GetColumnVisibility("ItemType");
    public bool ShowYearColumn => GetColumnVisibility("Year");
    public bool ShowAuthorColumn => GetColumnVisibility("Author");
    public bool ShowTitleColumn => GetColumnVisibility("Title");
    public bool ShowSourceColumn => GetColumnVisibility("Source");
    public bool ShowStatusColumn => GetColumnVisibility("Status");
    public bool ShowPagesColumn => GetColumnVisibility("Pages");
    public bool ShowFileColumn => GetColumnVisibility("File");

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
            Raise(nameof(Output));
            await _main.LogOperationAsync("copy_evidence_uri", Output);
            return;
        }

        try
        {
            await _main.Clipboard.SetTextAsync(versionedUri);
            VersionedUri = versionedUri;
            Output = "Copied Evidence URI";
            Raise(nameof(VersionedUri));
        }
        catch (Exception ex)
        {
            Output = $"ERROR clipboard_unavailable: {ex.Message}";
        }

        Raise(nameof(Output));
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
            Raise(nameof(Output));
            await _main.LogOperationAsync("copy_search_result_evidence_markdown", Output);
            return;
        }

        Result<EvidencePageText> markdown = await ResolveMarkdownAsync(versionedUri);
        if (markdown.IsFailure)
        {
            Output = $"ERROR {markdown.ErrorCode}: {markdown.ErrorMessage}";
            Raise(nameof(Output));
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
            Raise(nameof(VersionedUri));
            Raise(nameof(Markdown));
            _main.Report("已复制证据 Markdown。");
        }
        catch (Exception ex)
        {
            Output = $"ERROR clipboard_unavailable: {ex.Message}";
        }

        Raise(nameof(Output));
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

    private async Task SearchAsync()
    {
        if (IsSearching)
        {
            return;
        }

        IsSearching = true;
        try
        {
            await Task.Delay(50);
            if (IsBibliographicMode)
            {
                await SearchBibliographicAsync();
                return;
            }

            await SearchFullTextAsync();
        }
        finally
        {
            IsSearching = false;
        }
    }

    private async Task SearchBibliographicAsync()
    {
        BibliographicItemSearch request = BuildBibliographicSearch();
        if (request.Query is null && request.Filters.Count == 0)
        {
            ClearResults();
            Output = "";
            RaiseResultProperties();
            _main.Report("请输入搜索词或添加筛选条件。");
            return;
        }

        HostServices services = await _main.ServicesAsync();
        Result<IReadOnlyList<LibraryItemRow>> result = await Task.Run(() =>
            services.LibraryItems.SearchRowsAsync(request));
        if (result.IsFailure)
        {
            ClearResults();
            IndexStatus = "";
            AffectedScopesSummary = "";
            EstimatedTotalText = "";
            Output = $"ERROR {result.ErrorCode}: {result.ErrorMessage}";
            RaiseResultProperties();
            _main.Report(Output);
            return;
        }

        BibliographicResults.Clear();
        foreach (LibraryItemRow row in result.Value)
        {
            BibliographicResults.Add(CreateItemViewModel(row));
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
    }

    private async Task SearchFullTextAsync()
    {
        if (string.IsNullOrWhiteSpace(Query))
        {
            ClearResults();
            Output = "";
            RaiseResultProperties();
            _main.Report("请输入搜索词。");
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
        Result<SearchResultPage> r = await Task.Run(() => services.Search.SearchLibraryAsync(
            new SearchRequest(Query) { ItemFilters = itemFilters }));
        if (r.IsFailure)
        {
            ClearResults();
            IndexStatus = "";
            AffectedScopesSummary = "";
            EstimatedTotalText = "";
            Output = $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
            RaiseResultProperties();
            _main.Report(Output);
            return;
        }

        Dictionary<ItemId, IReadOnlyList<SearchPageResult>> pagesByItem = r.Value.Results
            .GroupBy(page => page.ItemId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<SearchPageResult>)group.ToArray());
        Result<IReadOnlyList<LibraryItemRow>> items = await Task.Run(() =>
            services.LibraryItems.GetRowsByIdsAsync(pagesByItem.Keys));
        Dictionary<ItemId, LibraryItemRow> rowsByItem = items.IsSuccess
            ? items.Value.ToDictionary(row => row.ItemId)
            : new Dictionary<ItemId, LibraryItemRow>();

        List<SearchHitItemViewModel> hits = new();
        SearchUnits.Clear();
        string? firstMatchedUnit = default;
        foreach ((ItemId itemId, IReadOnlyList<SearchPageResult> itemPages) in pagesByItem)
        {
            List<SearchHitSnippetViewModel> snippets = new();
            foreach (SearchPageResult page in itemPages.OrderBy(page => page.PageIndex))
            {
                foreach (SearchMatchedUnit unit in page.MatchedUnits)
                {
                    SearchUnits.Add($"{unit.UnitId} | {unit.Text}");
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

        RaiseHitExpansionProperties();
        AllHitsExpansionChanged?.Invoke(expanded);
    }

    private void OnFullTextResultsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (SearchHitItemViewModel hit in e.OldItems)
            {
                hit.PropertyChanged -= OnHitExpandedChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (SearchHitItemViewModel hit in e.NewItems)
            {
                hit.PropertyChanged += OnHitExpandedChanged;
            }
        }

        RaiseHitExpansionProperties();
    }

    private void OnHitExpandedChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SearchHitItemViewModel.IsExpanded))
        {
            RaiseHitExpansionProperties();
            if (!_suppressHitExpansionChanged)
            {
                HitExpansionChanged?.Invoke((SearchHitItemViewModel)sender!);
            }
        }
    }

    private void RaiseHitExpansionProperties()
    {
        Raise(nameof(AreAllHitsExpanded));
        Raise(nameof(ToggleAllHitsExpandedText));
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
        Raise(nameof(UnitId));
        Raise(nameof(VersionedUri));
        Raise(nameof(IndexStatus));
        Raise(nameof(AffectedScopesSummary));
        Raise(nameof(EstimatedTotalText));
    }

    private void RaiseResultProperties()
    {
        Raise(nameof(UnitId));
        Raise(nameof(VersionedUri));
        Raise(nameof(IndexStatus));
        Raise(nameof(AffectedScopesSummary));
        Raise(nameof(EstimatedTotalText));
        Raise(nameof(BibliographicResults));
        Raise(nameof(HasBibliographicResults));
        Raise(nameof(HasResults));
        Raise(nameof(HasNoResults));
        Raise(nameof(FullTextResults));
        Raise(nameof(Output));
    }
}
