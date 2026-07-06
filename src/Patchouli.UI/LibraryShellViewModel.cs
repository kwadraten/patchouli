using System.Collections.ObjectModel;
using System.Text.Json;
using Dapper;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Import;
using Patchouli.Ocr.MinerU;

namespace Patchouli.UI;

public sealed class LibraryShellViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private bool _isReadingMode;

    public LibraryShellViewModel(MainWindowViewModel main)
    {
        _main = main;
        RefreshCommand = new AsyncCommand(RefreshItemsAsync);
        SaveMetadataCommand = new AsyncCommand(SaveMetadataAsync);
        CancelMetadataEditCommand = new AsyncCommand(CancelMetadataEditAsync);
        CloseDocumentTabCommand = new AsyncCommand(CloseDocumentTabAsync);
        ShowRecentItemsCommand = new AsyncCommand(ShowRecentItemsAsync);
        SwitchToLibraryListCommand = new AsyncCommand(SwitchToLibraryListAsync);
        SwitchToReadingModeCommand = new AsyncCommand(SwitchToReadingModeAsync);
    }

    public bool IsReadingMode
    {
        get => _isReadingMode;
        set
        {
            if (_isReadingMode == value) return;
            _isReadingMode = value;
            Raise();
            Raise(nameof(ShowLibraryList));
            Raise(nameof(ShowPdfReader));
            _main.RaiseShellSelectionChanged();
        }
    }

    public bool ShowLibraryList => !IsReadingMode;
    public bool ShowPdfReader => IsReadingMode && SelectedItem is not null;

    public AsyncCommand SwitchToLibraryListCommand { get; }
    public AsyncCommand SwitchToReadingModeCommand { get; }

    public Task SwitchToLibraryListAsync()
    {
        IsReadingMode = false;
        return Task.CompletedTask;
    }

    public async Task SwitchToReadingModeAsync()
    {
        if (SelectedItem is not null)
        {
            IsReadingMode = true;
            await _main.PdfPreview.LoadSelectedItemAsync(SelectedItem);
        }
    }

    public string LibraryName { get; set; } = "我的书库";
    public ObservableCollection<string> RecentItems { get; } = new();
    public ObservableCollection<string> RecentDocuments { get; } = new();
    public ObservableCollection<LibraryItemViewModel> Items { get; } = new();
    public string StatusText => _main.Status;
    public string MinerUToken { get; set; } = "";
    public bool ShowMetadataEditor { get; set; }
    public Func<MinerUConfiguration, IMinerUClient>? MinerUClientFactory { get; set; }
    public string EditTitle { get; set; } = "";
    public string EditAuthors { get; set; } = "";
    public string EditYear { get; set; } = "";
    public string EditPublicationTitle { get; set; } = "";
    public bool IsBusy { get; set; }
    private LibraryItemViewModel? _selectedItem;
    public LibraryItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (_selectedItem == value) return;
            _selectedItem = value;
            Raise();
            Raise(nameof(InspectorTitle));
            Raise(nameof(InspectorSubtitle));
            Raise(nameof(InspectorStatus));
            Raise(nameof(InspectorPath));
            Raise(nameof(HasSelectedItem));
            Raise(nameof(NoSelectedItem));
            _main.RaiseShellSelectionChanged();
        }
    }

    public bool HasSelectedItem => SelectedItem is not null;
    public bool NoSelectedItem => SelectedItem is null;
    public string InspectorTitle => SelectedItem?.Title ?? "未选择题录";
    public string InspectorSubtitle => SelectedItem is null ? "选择一个已导入题录以查看 OCR/索引状态。" : $"{SelectedItem.ItemType} / {SelectedItem.FileName}";
    public string InspectorStatus => SelectedItem?.OcrStatus ?? "未选择文档";
    public string InspectorPath => SelectedItem?.SourcePath ?? "";
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand SaveMetadataCommand { get; }
    public AsyncCommand CancelMetadataEditCommand { get; }
    public AsyncCommand CloseDocumentTabCommand { get; }
    public AsyncCommand ShowRecentItemsCommand { get; }

    public void NotifyMinerUTokenChanged()
    {
        Raise(nameof(MinerUToken));
    }

    public async Task RefreshItemsAsync()
    {
        var services = await _main.ServicesAsync();
        var library = await services.Library.GetCurrentLibraryAsync();
        if (library.IsSuccess && LibraryName != library.Value.DisplayName)
        {
            LibraryName = library.Value.DisplayName;
            Raise(nameof(LibraryName));
            _main.RaiseLibraryTitleChanged();
        }

        await using var connection = services.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();

        var rows = await connection.QueryAsync<LibraryItemRow>(
            """
            select
                i.item_id as ItemId,
                i.title as Title,
                i.item_type as ItemType,
                i.creators_json as CreatorsJson,
                coalesce(
                    (select group_concat(
                        case
                            when length(trim(coalesce(c.literal, ''))) > 0 then c.literal
                            else trim(coalesce(c.given, '') || ' ' || coalesce(c.particles, '') || ' ' || coalesce(c.family, '') || ' ' || coalesce(c.suffix, ''))
                        end,
                        ', '
                    )
                     from item_creators c
                     where c.item_id = i.item_id and c.role = 'author'
                     order by c.sequence_index),
                    ''
                ) as Authors,
                coalesce((select d.literal from item_dates d where d.item_id = i.item_id and d.role = 'issued'), i.date, '') as Year,
                coalesce(i.publication_title, '') as PublicationTitle,
                d.document_instance_id as DocumentInstanceId,
                f.file_asset_id as FileAssetId,
                coalesce(d.status, 'unknown') as DocumentStatus,
                coalesce(f.file_name, '') as FileName,
                coalesce(f.original_path, '') as SourcePath,
                (select count(1) from pages p where p.document_instance_id = d.document_instance_id) as PageCount,
                (select count(1) from search_units su where su.document_instance_id = d.document_instance_id and su.status = 'current') as SearchUnitCount,
                coalesce((select sis.status from search_index_status sis where sis.scope_type = 'document_instance' and sis.scope_id = d.document_instance_id), 'not_indexed') as IndexStatus
            from items i
            left join document_instances d on d.item_id = i.item_id and d.is_primary = 1
            left join file_assets f on f.file_asset_id = d.file_asset_id
            where i.deleted_at is null
            order by i.created_at desc, i.title;
            """);

        Items.Clear();
        RecentItems.Clear();
        RecentDocuments.Clear();
        foreach (var row in rows)
        {
            var item = new LibraryItemViewModel(
                row.ItemId,
                row.Title,
                row.ItemType,
                string.IsNullOrWhiteSpace(row.Authors) ? FormatCreators(row.CreatorsJson) : row.Authors,
                row.Year,
                row.PublicationTitle,
                row.DocumentInstanceId,
                row.FileAssetId,
                row.FileName,
                row.SourcePath,
                row.PageCount,
                row.SearchUnitCount,
                row.IndexStatus,
                RunOcrForItemAsync,
                EditMetadataForItemAsync);
            Items.Add(item);
            RecentItems.Add(row.Title);
            if (!string.IsNullOrWhiteSpace(row.FileName))
                RecentDocuments.Add(row.FileName);
        }

        if (SelectedItem is null || Items.All(item => item.ItemId != SelectedItem.ItemId))
            SelectedItem = Items.FirstOrDefault();
        Raise(nameof(Items));
        Raise(nameof(RecentItems));
        Raise(nameof(RecentDocuments));
        Raise(nameof(SelectedItem));
        Raise(nameof(InspectorTitle));
        Raise(nameof(InspectorSubtitle));
        Raise(nameof(InspectorStatus));
        Raise(nameof(InspectorPath));
        Raise(nameof(HasSelectedItem));
        Raise(nameof(NoSelectedItem));
    }

    public async Task RunOcrForItemAsync(LibraryItemViewModel item)
    {
        SelectedItem = item;
        var token = await ResolveMinerUTokenAsync();
        if (string.IsNullOrWhiteSpace(token))
        {
            item.OcrStatus = "需要 MinerU API token。请先在设置中完成配置后再重试 OCR。";
            await _main.OpenSettingsAsync("mineru", "运行 OCR 前需要 MinerU API token。请先在设置中完成配置。");
            _main.Report("运行 OCR 前需要 MinerU API token。请先在设置中完成配置。");
            Raise(nameof(InspectorStatus));
            return;
        }

        if (string.IsNullOrWhiteSpace(item.DocumentInstanceId) || string.IsNullOrWhiteSpace(item.SourcePath))
        {
            item.OcrStatus = "该题录没有可用于 OCR 的文档源。";
            _main.Report(item.OcrStatus);
            Raise(nameof(InspectorStatus));
            return;
        }

        IsBusy = true;
        item.OcrStatus = "OCR 正在运行...";
        Raise(nameof(IsBusy));
        Raise(nameof(InspectorStatus));
        try
        {
            var services = await _main.ServicesAsync();
            var workflow = MinerUClientFactory is null
                ? services.FirstRunWorkflow
                : services.CreateFirstRunWorkflow(MinerUClientFactory);

            var state = await workflow.RunMinerUExtractionAsync(
                _main.CreateMinerUConfiguration(token),
                item.SourcePath,
                Path.Combine(Path.GetTempPath(), "Patchouli", "mineru-cache"),
                item.DocumentInstanceId);

            item.OcrStatus = state.LastError ?? state.ProgressText;
            _main.Report(item.OcrStatus);
            await RefreshItemsAsync();
        }
        finally
        {
            IsBusy = false;
            Raise(nameof(IsBusy));
            Raise(nameof(InspectorStatus));
        }
    }

    private async Task<string> ResolveMinerUTokenAsync()
    {
        if (!string.IsNullOrWhiteSpace(MinerUToken))
            return MinerUToken.Trim();

        if (!_main.HasOpenRuntimeDatabase)
            return "";

        var persisted = await _main.GetPersistedMinerUTokenAsync();
        MinerUToken = persisted;
        NotifyMinerUTokenChanged();
        return persisted;
    }

    public Task EditMetadataForItemAsync(LibraryItemViewModel item)
    {
        SelectedItem = item;
        EditTitle = item.Title;
        EditAuthors = item.Authors;
        EditYear = item.Year;
        EditPublicationTitle = item.PublicationTitle;
        ShowMetadataEditor = true;
        Raise(nameof(EditTitle));
        Raise(nameof(EditAuthors));
        Raise(nameof(EditYear));
        Raise(nameof(EditPublicationTitle));
        Raise(nameof(ShowMetadataEditor));
        return Task.CompletedTask;
    }

    public async Task SaveMetadataAsync()
    {
        if (SelectedItem is null) return;
        var title = string.IsNullOrWhiteSpace(EditTitle) ? SelectedItem.FileName : EditTitle.Trim();
        var authors = EditAuthors.Trim();
        var year = string.IsNullOrWhiteSpace(EditYear) ? null : EditYear.Trim();
        var publicationTitle = string.IsNullOrWhiteSpace(EditPublicationTitle) ? null : EditPublicationTitle.Trim();
        var services = await _main.ServicesAsync();
        var updated = await services.Items.UpdateItemAsync(
            Patchouli.Core.Ids.ItemId.Parse(SelectedItem.ItemId),
            new UpdateItemRequest(
                SelectedItem.ItemType,
                title,
                PublicationTitle: publicationTitle,
                Date: year,
                Creators: string.IsNullOrWhiteSpace(authors)
                    ? Array.Empty<ItemCreatorInput>()
                    : authors.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(name => new ItemCreatorInput(ItemCreatorRoles.Author, Literal: name))
                        .ToArray(),
                Dates: string.IsNullOrWhiteSpace(year)
                    ? Array.Empty<ItemDateInput>()
                    : new[] { new ItemDateInput(ItemDateRoles.Issued, Literal: year) }));

        if (updated.IsFailure)
        {
            _main.Report(updated.ErrorMessage ?? "题录信息更新失败。");
            return;
        }

        ShowMetadataEditor = false;
        Raise(nameof(ShowMetadataEditor));
        _main.Report($"题录信息已更新：{title}");
        await RefreshItemsAsync();
    }

    public Task CancelMetadataEditAsync()
    {
        ShowMetadataEditor = false;
        Raise(nameof(ShowMetadataEditor));
        return Task.CompletedTask;
    }

    public Task CloseDocumentTabAsync()
    {
        SelectedItem = null;
        IsReadingMode = false;
        _main.PdfPreview.Clear();
        return Task.CompletedTask;
    }

    public async Task ShowRecentItemsAsync()
    {
        await RefreshItemsAsync();
        _main.Report("正在显示最近项目。");
    }

    public async void Refresh()
    {
        try { await RefreshItemsAsync(); } catch { }
        Raise(nameof(StatusText));
        Raise(nameof(LibraryName));
    }

    private sealed class LibraryItemRow
    {
        public string ItemId { get; set; } = "";
        public string Title { get; set; } = "";
        public string ItemType { get; set; } = "";
        public string CreatorsJson { get; set; } = "[]";
        public string Authors { get; set; } = "";
        public string Year { get; set; } = "";
        public string PublicationTitle { get; set; } = "";
        public string? DocumentInstanceId { get; set; }
        public string? FileAssetId { get; set; }
        public string DocumentStatus { get; set; } = "";
        public string FileName { get; set; } = "";
        public string SourcePath { get; set; } = "";
        public int PageCount { get; set; }
        public int SearchUnitCount { get; set; }
        public string IndexStatus { get; set; } = "";
    }

    private static string FormatCreators(string creatorsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(creatorsJson) ? "[]" : creatorsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return "";
            return string.Join(", ", document.RootElement.EnumerateArray()
                .Select(element =>
                    element.TryGetProperty("name", out var name) ? name.GetString() :
                    element.TryGetProperty("Name", out var upperName) ? upperName.GetString() :
                    null)
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        }
        catch
        {
            return "";
        }
    }
}

public sealed class LibraryItemViewModel : ViewModelBase
{
    private string _ocrStatus;

    public LibraryItemViewModel(
        string itemId,
        string title,
        string itemType,
        string authors,
        string year,
        string publicationTitle,
        string? documentInstanceId,
        string? fileAssetId,
        string fileName,
        string sourcePath,
        int pageCount,
        int searchUnitCount,
        string indexStatus,
        Func<LibraryItemViewModel, Task> runOcr,
        Func<LibraryItemViewModel, Task> editMetadata)
    {
        ItemId = itemId;
        Title = title;
        ItemType = itemType;
        Authors = authors;
        Year = year;
        PublicationTitle = publicationTitle;
        DocumentInstanceId = documentInstanceId;
        FileAssetId = fileAssetId;
        FileName = fileName;
        SourcePath = sourcePath;
        PageCount = pageCount;
        SearchUnitCount = searchUnitCount;
        IndexStatus = indexStatus;
        _ocrStatus = searchUnitCount > 0 ? $"已索引（{searchUnitCount} 个单元，{indexStatus}）" : $"未索引（{indexStatus}）";
        RunOcrCommand = new AsyncCommand(() => runOcr(this));
        EditMetadataCommand = new AsyncCommand(() => editMetadata(this));
    }

    public string ItemId { get; }
    public string Title { get; }
    public string ItemType { get; }
    public string Authors { get; }
    public string Year { get; }
    public string PublicationTitle { get; }
    public string? DocumentInstanceId { get; }
    public string? FileAssetId { get; }
    public string FileName { get; }
    public string SourcePath { get; }
    public int PageCount { get; }
    public int SearchUnitCount { get; }
    public string IndexStatus { get; }
    public string PageCountDisplay => PageCount <= 0 ? "-" : PageCount.ToString();
    public AsyncCommand RunOcrCommand { get; }
    public AsyncCommand EditMetadataCommand { get; }

    public string OcrStatus
    {
        get => _ocrStatus;
        set
        {
            if (_ocrStatus == value) return;
            _ocrStatus = value;
            Raise();
        }
    }
}
