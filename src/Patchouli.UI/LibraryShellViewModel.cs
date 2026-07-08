using System.Collections.ObjectModel;
using System.Text.Json;
using Dapper;
using Patchouli.Core.Credentials;
using Patchouli.Core.Ids;
using Patchouli.Core.Import;
using Patchouli.Ocr;
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
            Raise(nameof(ShowPdfWorkspace));
            _main.RaiseShellSelectionChanged();
        }
    }

    public bool ShowLibraryList => !IsReadingMode;
    public bool ShowPdfWorkspace => IsReadingMode && SelectedItem is not null;

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
            await _main.PdfWorkspace.LoadSelectedItemAsync(SelectedItem);
        }
    }

    public string LibraryName { get; set; } = "我的书库";
    public ObservableCollection<string> RecentItems { get; } = new();
    public ObservableCollection<string> RecentDocuments { get; } = new();
    public ObservableCollection<LibraryItemViewModel> Items { get; } = new();
    public string StatusText => _main.Status;
    public string MinerUToken { get; set; } = "";
    public Func<MinerUConfiguration, IMinerUClient>? MinerUClientFactory { get; set; }
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

        var rowsResult = await services.LibraryItems.ListRowsAsync();
        if (rowsResult.IsFailure)
        {
            throw new InvalidOperationException(rowsResult.ErrorMessage);
        }

        await using var connection = services.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        var sourcePaths = (await connection.QueryAsync<(string DocumentInstanceId, string SourcePath, string? FileAssetId)>(
            """
            select di.document_instance_id as DocumentInstanceId,
                   coalesce(fa.original_path, '') as SourcePath,
                   fa.file_asset_id as FileAssetId
            from document_instances di
            left join file_assets fa on fa.file_asset_id = di.file_asset_id;
            """))
            .ToDictionary(value => value.DocumentInstanceId, value => value, StringComparer.Ordinal);

        Items.Clear();
        RecentItems.Clear();
        RecentDocuments.Clear();
        foreach (var row in rowsResult.Value)
        {
            var source = row.DocumentInstanceId is not null && sourcePaths.TryGetValue(row.DocumentInstanceId.ToString(), out var value)
                ? value
                : default;
            var item = new LibraryItemViewModel(
                row.ItemId.ToString(),
                row.Title,
                row.ItemType,
                row.Authors,
                row.Year ?? "",
                row.PublicationTitle ?? "",
                row.DocumentInstanceId?.ToString(),
                source.FileAssetId,
                row.LinkedFileName ?? "",
                source.SourcePath ?? "",
                row.PageCount,
                row.SearchUnitCount,
                row.IndexStatus,
                RunOcrForItemAsync,
                EditMetadataForItemAsync,
                ViewPdfForItemAsync);
            Items.Add(item);
            RecentItems.Add(row.Title);
            if (!string.IsNullOrWhiteSpace(row.LinkedFileName))
                RecentDocuments.Add(row.LinkedFileName);
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
            await services.Credentials.SaveOrUpdateProviderCredentialAsync(ProviderIds.MinerU, "MinerU API token", token);
            var presetId = await EnsureMinerUPresetAsync(services);
            var documentInstanceId = DocumentInstanceId.Parse(item.DocumentInstanceId);
            var coordinator = MinerUClientFactory is null
                ? services.Ocr
                : services.CreateOcrRunCoordinator(MinerUClientFactory);

            var run = await coordinator.RunPresetOnDocumentAsync(documentInstanceId, presetId);
            if (run.IsFailure)
            {
                item.OcrStatus = run.ErrorMessage ?? "OCR 运行失败。";
                _main.Report(item.OcrStatus);
                Raise(nameof(InspectorStatus));
                return;
            }

            var units = await services.SearchUnits.RebuildForDocumentInstanceAsync(documentInstanceId);
            var index = units.IsSuccess
                ? await services.SearchIndex.RebuildFtsForDocumentInstanceAsync(documentInstanceId)
                : units;

            item.OcrStatus = index.IsSuccess
                ? "OCR 完成，搜索索引已更新。"
                : index.ErrorMessage ?? "OCR 完成，但搜索索引更新失败。";
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

    private static async Task<OcrPresetId> EnsureMinerUPresetAsync(AppServices services)
    {
        await using var connection = services.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        var existing = await connection.ExecuteScalarAsync<string?>(
            """
            select p.preset_id
            from ocr_presets p
            join ocr_preset_versions v on v.preset_version_id = p.current_version_id
            where p.archived = 0
              and v.engine_id = @EngineId
            order by p.updated_at desc
            limit 1;
            """,
            new { EngineId = OcrEngineIds.MinerU });
        if (!string.IsNullOrWhiteSpace(existing))
            return OcrPresetId.Parse(existing);

        var created = await services.OcrPresets.CreatePresetAsync(
            "MinerU OCR",
            "MinerU document OCR preset",
            OcrEngineIds.MinerU,
            OcrModelIds.MinerUDefault,
            null,
            """{"isOcr":true,"enableTable":true,"enableFormula":true}""",
            true);
        if (created.IsFailure)
            throw new InvalidOperationException(created.ErrorMessage);

        return created.Value.PresetId;
    }

    public Task EditMetadataForItemAsync(LibraryItemViewModel item)
    {
        SelectedItem = item;
        return _main.EditSelectedItemCommand.ExecuteAsync();
    }

    public Task ViewPdfForItemAsync(LibraryItemViewModel item)
    {
        SelectedItem = item;
        return _main.ShowReadingCommand.ExecuteAsync();
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
        Func<LibraryItemViewModel, Task> editMetadata,
        Func<LibraryItemViewModel, Task>? viewPdf = null)
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
        ViewPdfCommand = new AsyncCommand(() => (viewPdf ?? editMetadata)(this));
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
    public AsyncCommand ViewPdfCommand { get; }

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
