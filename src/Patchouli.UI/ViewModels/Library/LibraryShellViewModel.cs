using System.Collections.ObjectModel;
using System.Text.Json;
using Dapper;
using Patchouli.Core.Credentials;
using Patchouli.Core.Ids;
using Patchouli.Core.Import;
using Patchouli.Core.Results;
using Patchouli.Ocr;
using Patchouli.Ocr.MinerU;
using Avalonia.Threading;

namespace Patchouli.UI.ViewModels;

public sealed class LibraryShellViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;

    public LibraryShellViewModel(MainWindowViewModel main)
    {
        _main = main;
        RefreshCommand = new AsyncCommand(RefreshItemsAsync);
        ShowRecentItemsCommand = new AsyncCommand(ShowRecentItemsAsync);
        SwitchToReadingModeCommand = new AsyncCommand(SwitchToReadingModeAsync);
        LookupMetadataBatchCommand = new AsyncCommand(LookupMetadataBatchAsync);
        CancelMetadataBatchCommand = new AsyncCommand(CancelMetadataBatchAsync);
    }

    public string LibraryName { get; set; } = "我的书库";
    public ObservableCollection<string> RecentItems { get; } = new();
    public ObservableCollection<string> RecentDocuments { get; } = new();
    public ObservableCollection<LibraryItemViewModel> Items { get; } = new();
    public ObservableCollection<LibraryItemViewModel> SelectedItems { get; } = new();
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
    public bool IsReadingMode { get; set; }
    public bool ShowLibraryList => !IsReadingMode;
    public bool ShowPdfWorkspace => IsReadingMode;
    public string InspectorTitle => SelectedItem?.Title ?? "";
    public string InspectorSubtitle => SelectedItem is null ? "" : $"{SelectedItem.ItemType} / {SelectedItem.FileName}";
    public string InspectorStatus => SelectedItem?.OcrStatus ?? "未选择文档";
    public string InspectorPath => SelectedItem?.SourcePath ?? "";
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand ShowRecentItemsCommand { get; }
    public AsyncCommand SwitchToReadingModeCommand { get; }
    public AsyncCommand LookupMetadataBatchCommand { get; }
    public AsyncCommand CancelMetadataBatchCommand { get; }

    private CancellationTokenSource? _metadataBatchCancellation;
    private bool _isMetadataBatchBusy;
    private double _metadataBatchProgress;
    private string _metadataBatchStatus = "";
    public bool IsMetadataBatchBusy
    {
        get => _isMetadataBatchBusy;
        private set { if (_isMetadataBatchBusy == value) return; _isMetadataBatchBusy = value; Raise(); }
    }
    public double MetadataBatchProgress
    {
        get => _metadataBatchProgress;
        private set { if (_metadataBatchProgress == value) return; _metadataBatchProgress = value; Raise(); }
    }
    public string MetadataBatchStatus
    {
        get => _metadataBatchStatus;
        private set { if (_metadataBatchStatus == value) return; _metadataBatchStatus = value; Raise(); Raise(nameof(HasMetadataBatchStatus)); }
    }
    public bool HasMetadataBatchStatus => !string.IsNullOrWhiteSpace(MetadataBatchStatus);
    public int SelectedItemCount => SelectedItems.Count;
    public bool HasBatchSelection => SelectedItems.Count > 0;

    public void SetSelectedItems(IEnumerable<LibraryItemViewModel> items)
    {
        var selected = items.Distinct().ToArray();
        SelectedItems.Clear();
        foreach (var item in selected) SelectedItems.Add(item);
        Raise(nameof(SelectedItems));
        Raise(nameof(SelectedItemCount));
        Raise(nameof(HasBatchSelection));
    }

    public bool ShowItemTypeColumn
    {
        get => GetColumnVisibility("ItemType", true);
        set => SetColumnVisibility("ItemType", value);
    }
    public bool ShowYearColumn
    {
        get => GetColumnVisibility("Year", true);
        set => SetColumnVisibility("Year", value);
    }
    public bool ShowAuthorColumn
    {
        get => GetColumnVisibility("Author", true);
        set => SetColumnVisibility("Author", value);
    }
    public bool ShowTitleColumn
    {
        get => GetColumnVisibility("Title", true);
        set => SetColumnVisibility("Title", value);
    }
    public bool ShowSourceColumn
    {
        get => GetColumnVisibility("Source", true);
        set => SetColumnVisibility("Source", value);
    }
    public bool ShowStatusColumn
    {
        get => GetColumnVisibility("Status", true);
        set => SetColumnVisibility("Status", value);
    }
    public bool ShowPagesColumn
    {
        get => GetColumnVisibility("Pages", true);
        set => SetColumnVisibility("Pages", value);
    }
    public bool ShowFileColumn
    {
        get => GetColumnVisibility("File", true);
        set => SetColumnVisibility("File", value);
    }

    private bool GetColumnVisibility(string key, bool defaultValue)
    {
        if (_main.AppOptions.Ui.LibraryGridVisibleColumns.TryGetValue(key, out var visible))
            return visible;
        return defaultValue;
    }

    private void SetColumnVisibility(string key, bool value)
    {
        _main.AppOptions.Ui.LibraryGridVisibleColumns[key] = value;
        _main.AppOptions.Save(_main.SettingsFilePath);
        Raise($"Show{key}Column");
    }

    public bool TryGetColumnWidth(string key, out double width) => _main.AppOptions.Ui.LibraryGridColumnWidths.TryGetValue(key, out width);

    public bool TryGetColumnOrder(string key, out int order) => _main.AppOptions.Ui.LibraryGridColumnOrder.TryGetValue(key, out order);

    public void SetColumnWidth(string key, double width)
    {
        if (width <= 0) return;
        _main.AppOptions.Ui.LibraryGridColumnWidths[key] = width;
        _main.AppOptions.Save(_main.SettingsFilePath);
    }

    public void SetColumnOrder(string key, int order)
    {
        if (order < 0) return;
        _main.AppOptions.Ui.LibraryGridColumnOrder[key] = order;
        _main.AppOptions.Save(_main.SettingsFilePath);
    }

    public void NotifyMinerUTokenChanged()
    {
        Raise(nameof(MinerUToken));
    }

    public async Task RefreshItemsAsync()
    {
        var primaryItemId = SelectedItem?.ItemId;
        var selectedItemIds = SelectedItems.Select(item => item.ItemId).ToHashSet(StringComparer.Ordinal);
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

        var refreshedItems = new List<LibraryItemViewModel>();
        var refreshedRecentItems = new List<string>();
        var refreshedRecentDocuments = new List<string>();
        foreach (var row in rowsResult.Value)
        {
            var documentInstanceKey = row.DocumentInstanceId?.ToString();
            var source = documentInstanceKey is not null && sourcePaths.TryGetValue(documentInstanceKey, out var value)
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
            refreshedItems.Add(item);
            refreshedRecentItems.Add(row.Title);
            if (!string.IsNullOrWhiteSpace(row.LinkedFileName))
                refreshedRecentDocuments.Add(row.LinkedFileName);
        }

        Items.Clear();
        foreach (var item in refreshedItems) Items.Add(item);
        RecentItems.Clear();
        foreach (var item in refreshedRecentItems) RecentItems.Add(item);
        RecentDocuments.Clear();
        foreach (var document in refreshedRecentDocuments) RecentDocuments.Add(document);

        SelectedItem = Items.FirstOrDefault(item => item.ItemId == primaryItemId) ?? Items.FirstOrDefault();
        SetSelectedItems(Items.Where(item => selectedItemIds.Contains(item.ItemId)));
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

    private Task RefreshItemsOnUiThreadAsync()
        => DispatcherTasks.RunAsync(RefreshItemsAsync);

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
            if (MinerUClientFactory is null)
            {
                var queued = await QueueOcrForItemAsync(services, documentInstanceId, presetId);
                item.OcrStatus = queued.IsSuccess
                    ? $"OCR 已加入后台队列：{queued.Value.TaskId}"
                    : queued.ErrorMessage ?? "OCR 入队失败。";
                _main.Report(item.OcrStatus);
                Raise(nameof(InspectorStatus));
                await _main.OcrQueue.RefreshAsync();
                return;
            }

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

    private async Task LookupMetadataBatchAsync()
    {
        if (IsMetadataBatchBusy || SelectedItems.Count == 0) return;

        var itemIds = SelectedItems.Select(item => ItemId.Parse(item.ItemId)).ToArray();
        _metadataBatchCancellation = new CancellationTokenSource();
        IsMetadataBatchBusy = true;
        MetadataBatchProgress = 0;
        MetadataBatchStatus = $"正在获取 0/{itemIds.Length} 个题录的元数据...";
        var latest = new MetadataLookupProgressInfo(0, itemIds.Length, 0, 0, null);
        try
        {
            var outcome = await MetadataLookupUiBridge.LookupBatchAsync(
                await _main.ServicesAsync(),
                itemIds,
                progress =>
                {
                    latest = progress;
                    MetadataBatchProgress = progress.Total <= 0 ? 0 : 100d * progress.Completed / progress.Total;
                    MetadataBatchStatus = $"正在获取 {progress.Completed}/{Math.Max(progress.Total, itemIds.Length)} 个题录的元数据...";
                },
                _metadataBatchCancellation.Token);

            await RefreshItemsOnUiThreadAsync();
            await _main.RefreshOpenItemEditorsAsync(itemIds);
            var failed = Math.Max(latest.Failed, outcome.FailedCount);
            var succeeded = Math.Max(latest.Succeeded, outcome.SucceededCount);
            if (!outcome.IsSuccess && failed == 0) failed = itemIds.Length - succeeded;
            MetadataBatchProgress = 100;
            MetadataBatchStatus = failed > 0
                ? $"批量获取完成：成功 {succeeded} 个，失败 {failed} 个。{outcome.Message}"
                : $"批量获取完成：成功 {Math.Max(succeeded, itemIds.Length)} 个。";
            if (failed > 0) _main.ReportError(MetadataBatchStatus); else _main.Report(MetadataBatchStatus);
        }
        catch (OperationCanceledException)
        {
            await RefreshItemsOnUiThreadAsync();
            await _main.RefreshOpenItemEditorsAsync(itemIds);
            MetadataBatchStatus = $"批量获取已取消：已处理 {latest.Completed}/{itemIds.Length} 个。";
            _main.Report(MetadataBatchStatus);
        }
        catch (Exception exception)
        {
            MetadataBatchStatus = $"批量获取失败：{exception.Message}";
            _main.ReportError(MetadataBatchStatus);
        }
        finally
        {
            IsMetadataBatchBusy = false;
            _metadataBatchCancellation.Dispose();
            _metadataBatchCancellation = null;
        }
    }

    private Task CancelMetadataBatchAsync()
    {
        _metadataBatchCancellation?.Cancel();
        return Task.CompletedTask;
    }

    private async Task<Result<OcrQueueTask>> QueueOcrForItemAsync(AppServices services, DocumentInstanceId documentInstanceId, OcrPresetId presetId)
    {
        await using var connection = services.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();

        var pageIds = (await connection.QueryAsync<string>(
            """
            select page_id
            from pages
            where document_instance_id = @DocumentInstanceId
            order by page_index, page_id;
            """,
            new { DocumentInstanceId = documentInstanceId.ToString() }))
            .Select(PageId.Parse)
            .ToArray();

        if (pageIds.Length == 0)
        {
            return Result<OcrQueueTask>.Failure(AppErrorCodes.ValidationFailed, "Document instance has no pages to OCR.");
        }

        var engineId = await connection.ExecuteScalarAsync<string?>(
            """
            select v.engine_id
            from ocr_presets p
            join ocr_preset_versions v on v.preset_version_id = p.current_version_id
            where p.preset_id = @PresetId
            limit 1;
            """,
            new { PresetId = presetId.ToString() });
        if (string.IsNullOrWhiteSpace(engineId))
        {
            return Result<OcrQueueTask>.Failure(AppErrorCodes.InvalidState, "Active OCR preset/version was not found.");
        }

        var queue = await services.GetOcrQueueAsync();
        if (queue.IsFailure)
        {
            return Result<OcrQueueTask>.Failure(queue.ErrorCode!, queue.ErrorMessage!);
        }

        var adapterKind = engineId == OcrEngineIds.MinerU ? OcrAdapterKind.CloudApi : OcrAdapterKind.LocalLibrary;
        var providerId = engineId == OcrEngineIds.MinerU ? ProviderIds.MinerU : null;
        var enqueued = await queue.Value.EnqueueDocumentAsync(documentInstanceId, presetId, pageIds, engineId, adapterKind, providerId, OcrQueuePriority.UserStartedDocument);
        if (enqueued.IsSuccess)
        {
            await queue.Value.StartAsync();
        }

        return enqueued;
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

    private async Task SwitchToReadingModeAsync()
    {
        IsReadingMode = true;
        Raise(nameof(IsReadingMode));
        Raise(nameof(ShowLibraryList));
        Raise(nameof(ShowPdfWorkspace));
        await _main.ShowReadingAsync();
    }

    public void ExitReadingMode()
    {
        if (!IsReadingMode) return;
        IsReadingMode = false;
        Raise(nameof(IsReadingMode));
        Raise(nameof(ShowLibraryList));
        Raise(nameof(ShowPdfWorkspace));
        _main.RaiseShellSelectionChanged();
    }

    public async void Refresh()
    {
        await UnexpectedExceptionBoundary.RunAsync(RefreshItemsAsync, "refresh-library-shell");
        Raise(nameof(StatusText));
        Raise(nameof(LibraryName));
    }
}

internal sealed record MetadataLookupOutcome(bool IsSuccess, string Message, int SucceededCount = 0, int FailedCount = 0);
internal sealed record MetadataLookupProgressInfo(int Completed, int Total, int Succeeded, int Failed, string? Message);

internal static class MetadataLookupUiBridge
{
    public static bool CanLookup(AppServices services, string scheme) => services.MetadataLookup.CanLookup(scheme);

    public static async Task<MetadataLookupOutcome> LookupAsync(
        AppServices services,
        ItemId itemId,
        Patchouli.Core.Bibliography.ItemIdentifier identifier,
        CancellationToken cancellationToken)
    {
        var result = await services.MetadataLookup.LookupAndApplyAsync(itemId, identifier, cancellationToken);
        return result.IsFailure
            ? new MetadataLookupOutcome(false, result.ErrorMessage ?? "元数据获取失败。")
            : new MetadataLookupOutcome(true, $"已从 {result.Value.Candidate.SourceId} 获取元数据。");
    }

    public static async Task<MetadataLookupOutcome> LookupBatchAsync(
        AppServices services,
        IReadOnlyList<ItemId> itemIds,
        Action<MetadataLookupProgressInfo> onProgress,
        CancellationToken cancellationToken)
    {
        var progress = new Progress<Patchouli.Core.Bibliography.MetadataLookup.MetadataBatchProgress>(value =>
            onProgress(new MetadataLookupProgressInfo(value.Completed, value.Total, value.Succeeded, value.Failed, value.Message)));
        var result = await services.MetadataLookup.LookupAndApplyBatchAsync(itemIds, progress, cancellationToken);
        return result.IsFailure
            ? new MetadataLookupOutcome(false, result.ErrorMessage ?? "批量元数据获取失败。")
            : new MetadataLookupOutcome(true, "", result.Value.SucceededCount, result.Value.FailedCount);
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
