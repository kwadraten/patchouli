using System.Collections.ObjectModel;
using System.Text.Json;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Credentials;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Import;
using Patchouli.Core.Results;
using Patchouli.Core.Settings;
using Patchouli.Ocr;
using Avalonia.Threading;
using Patchouli.Core.Bibliography.MetadataLookup;
using Patchouli.Core.Library;
using Patchouli.UI.ViewModels.Core;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.ViewModels;

public sealed class LibraryShellViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private readonly SemaphoreSlim _committedChangeGate = new(1, 1);
    private ILibraryRevisionService? _observedLibraryRevisions;

    public LibraryShellViewModel(MainWindowViewModel main)
    {
        _main = main;
        Sidebar = new LibrarySidebarViewModel();
        Inspector = new ItemInspectorViewModel(
            async () => (await _main.ServicesAsync()).Items,
            async () => (await _main.ServicesAsync()).Tags,
            async () => (await _main.ServicesAsync()).ItemTypeProfiles);
        Sidebar.ScopeChanged += async (_, _) =>
        {
            Raise(nameof(CanModifyLibraryItems));
            await RefreshItemsAsync();
        };
        Sidebar.TagSelectionChanged += async (_, _) => await RefreshItemsAsync();
        Sidebar.PinToggled += async (_, tag) => await ToggleTagPinAsync(tag);
        Sidebar.RemoveRequested += async (_, tag) => await RemoveTagAsync(tag);
        Sidebar.RenameRequested += async (_, tag) => await RenameTagAsync(tag);
        Sidebar.MergeIntoRequested += async (_, tag) => await MergeTagAsync(tag);
        RefreshCommand = new AsyncCommand(RefreshItemsAsync);
        ShowRecentItemsCommand = new AsyncCommand(ShowRecentItemsAsync);
        SwitchToReadingModeCommand = new AsyncCommand(SwitchToReadingModeAsync);
        LookupMetadataBatchCommand = new AsyncCommand(LookupMetadataBatchAsync);
        CancelMetadataBatchCommand = new AsyncCommand(CancelMetadataBatchAsync);
        DetectDuplicatesCommand = new AsyncCommand(DetectDuplicatesAsync);
        MergeSelectedItemsCommand = new AsyncCommand(MergeSelectedItemsAsync);
        DeleteSelectedItemsCommand = new AsyncCommand(DeleteSelectedItemsAsync);
        RestoreSelectedItemsCommand = new AsyncCommand(RestoreSelectedItemsAsync);
        PurgeSelectedItemsCommand = new AsyncCommand(PurgeSelectedItemsAsync);
        QuickFillOcrCommand = new AsyncCommand(RunQuickFillOcrAsync);
    }

    /// <summary>
    /// Binds this shell to the revision stream for the currently open Library. The main window
    /// owns Library lifetime and calls this again with <see langword="null"/> before switching
    /// databases, so a notification from a previous Library cannot update the new shell.
    /// </summary>
    internal void ObserveLibraryRevisions(ILibraryRevisionService? revisions)
    {
        if (ReferenceEquals(_observedLibraryRevisions, revisions))
        {
            return;
        }

        if (_observedLibraryRevisions is not null)
        {
            _observedLibraryRevisions.ChangeCommitted -= OnLibraryChangeCommitted;
        }

        _observedLibraryRevisions = revisions;
        if (_observedLibraryRevisions is not null)
        {
            _observedLibraryRevisions.ChangeCommitted += OnLibraryChangeCommitted;
        }
    }

    private void OnLibraryChangeCommitted(object? sender, LibraryRevisionCommittedEventArgs eventArgs)
    {
        if (_observedLibraryRevisions is null || !ReferenceEquals(sender, _observedLibraryRevisions))
        {
            return;
        }

        ILibraryRevisionService revisions = _observedLibraryRevisions;
        ApplyCommittedChangeSetAsync(revisions, eventArgs.ChangeSet)
            .Observe("library-shell-revision", $"apply-{eventArgs.ChangeSet.NewRevision}");
    }

    private async Task ApplyCommittedChangeSetAsync(ILibraryRevisionService revisions, LibraryChangeSet changeSet)
    {
        await _committedChangeGate.WaitAsync();
        try
        {
            if (!ReferenceEquals(revisions, _observedLibraryRevisions))
            {
                return;
            }

            if (ReferenceEquals(revisions, _observedLibraryRevisions))
            {
                await ApplyChangeSetAsync(changeSet);
            }
        }
        finally
        {
            _committedChangeGate.Release();
        }
    }

    public string LibraryName { get; set; } = "我的书库";
    public LibrarySidebarViewModel Sidebar { get; }
    public ItemInspectorViewModel Inspector { get; }
    public ObservableCollection<string> RecentItems { get; } = new();
    public ObservableCollection<string> RecentDocuments { get; } = new();
    public ObservableCollection<LibraryItemViewModel> Items { get; } = new();
    public ObservableCollection<LibraryItemViewModel> SelectedItems { get; } = new();
    public string StatusText => _main.Status;
    public string MinerUToken { get; set; } = "";
    public bool IsBusy { get; set; }
    private LibraryItemViewModel? _selectedItem;

    private static ItemId? ParseItemIdOrNull(string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return null;
        }

        try
        {
            return ItemId.Parse(itemId);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public LibraryItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (_selectedItem == value)
            {
                return;
            }

            _selectedItem = value;
            Raise();
            Raise(nameof(InspectorTitle));
            Raise(nameof(InspectorSubtitle));
            Raise(nameof(InspectorStatus));
            Raise(nameof(InspectorPath));
            Raise(nameof(HasSelectedItem));
            Raise(nameof(NoSelectedItem));
            _ = Inspector.LoadAsync(ParseItemIdOrNull(value?.ItemId));
            _main.RaiseShellSelectionChanged();
        }
    }

    public bool HasSelectedItem => SelectedItem is not null;
    public bool NoSelectedItem => SelectedItem is null;
    public bool IsLibraryLeftSidebarVisible => _main.IsLibraryLeftSidebarVisible;
    public bool IsLibraryRightSidebarVisible => _main.IsLibraryRightSidebarVisible;
    public bool IsLibraryVisible => _main.IsLibraryVisible;
    public string RuntimeDatabasePath => _main.RuntimeDatabasePath;
    public string DefaultSyncRootPath => _main.DefaultSyncRootPath;
    public ObservableCollection<SidebarFileSearchRootViewModel> FileSearchRoots => _main.FileSearchRoots;
    public bool HasFileSearchRoots => _main.HasFileSearchRoots;
    public bool NoFileSearchRoots => _main.NoFileSearchRoots;
    public AsyncCommand RescanFileSearchRootsCommand => _main.RescanFileSearchRootsCommand;
    public AsyncCommand EditSelectedItemCommand => _main.EditSelectedItemCommand;
    public AsyncCommand ShowReadingCommand => _main.ShowReadingCommand;
    public AsyncCommand RunSelectedItemOcrCommand => _main.RunSelectedItemOcrCommand;
    public UiCommandDescriptor CopyCslBibliographyDescriptor => _main.CopyCslBibliographyDescriptor;
    public UiCommandDescriptor ExportItemDescriptor => _main.ExportItemDescriptor;
    public bool IsReadingMode { get; set; }
    public bool ShowLibraryList => !IsReadingMode;
    public bool ShowPdfWorkspace => IsReadingMode;
    public string InspectorTitle => SelectedItem?.Title ?? "";

    public string InspectorSubtitle => SelectedItem is null
        ? ""
        : $"{CslItemTypeDisplayNames.For(SelectedItem.ItemType)} / {SelectedItem.FileName}";

    public string InspectorStatus => SelectedItem?.OcrStatus ?? "未选择文档";
    public string InspectorPath => SelectedItem?.SourcePath ?? "";
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand ShowRecentItemsCommand { get; }
    public AsyncCommand SwitchToReadingModeCommand { get; }
    public AsyncCommand LookupMetadataBatchCommand { get; }
    public AsyncCommand CancelMetadataBatchCommand { get; }
    public AsyncCommand DetectDuplicatesCommand { get; }
    public AsyncCommand MergeSelectedItemsCommand { get; }
    public AsyncCommand DeleteSelectedItemsCommand { get; }
    public AsyncCommand RestoreSelectedItemsCommand { get; }
    public AsyncCommand PurgeSelectedItemsCommand { get; }
    public AsyncCommand QuickFillOcrCommand { get; }

    public bool CanModifyLibraryItems => Sidebar.IsActiveSelected;

    private CancellationTokenSource? _metadataBatchCancellation;
    private bool _isMetadataBatchBusy;
    private double _metadataBatchProgress;
    private string _metadataBatchStatus = "";

    public bool IsMetadataBatchBusy
    {
        get => _isMetadataBatchBusy;
        private set
        {
            if (_isMetadataBatchBusy == value)
            {
                return;
            }

            _isMetadataBatchBusy = value;
            Raise();
        }
    }

    public double MetadataBatchProgress
    {
        get => _metadataBatchProgress;
        private set
        {
            if (_metadataBatchProgress == value)
            {
                return;
            }

            _metadataBatchProgress = value;
            Raise();
        }
    }

    public string MetadataBatchStatus
    {
        get => _metadataBatchStatus;
        private set
        {
            if (_metadataBatchStatus == value)
            {
                return;
            }

            _metadataBatchStatus = value;
            Raise();
            Raise(nameof(HasMetadataBatchStatus));
        }
    }

    public bool HasMetadataBatchStatus => !string.IsNullOrWhiteSpace(MetadataBatchStatus);
    public int SelectedItemCount => SelectedItems.Count;
    public bool HasBatchSelection => SelectedItems.Count > 0;
    public bool IsSingleSelectionOrNone => SelectedItems.Count <= 1;
    public bool CanMergeSelectedItems => SelectedItems.Count == 2;

    public void SetSelectedItems(IEnumerable<LibraryItemViewModel> items)
    {
        LibraryItemViewModel[] selected = items.Distinct().ToArray();
        SelectedItems.Clear();
        foreach (LibraryItemViewModel item in selected)
        {
            SelectedItems.Add(item);
        }

        Raise(nameof(SelectedItems));
        Raise(nameof(SelectedItemCount));
        Raise(nameof(HasBatchSelection));
        Raise(nameof(IsSingleSelectionOrNone));
        Raise(nameof(CanMergeSelectedItems));
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
        if (_main.AppOptions.Ui.LibraryGridVisibleColumns.TryGetValue(key, out bool visible))
        {
            return visible;
        }

        return defaultValue;
    }

    private void SetColumnVisibility(string key, bool value)
    {
        Dictionary<string, bool> columns = new(_main.AppOptions.Ui.LibraryGridVisibleColumns,
            StringComparer.Ordinal);
        columns[key] = value;
        SettingsSaveResult saved = _main.UpdateAppOptions(_main.AppOptions with
        {
            Ui = _main.AppOptions.Ui with { LibraryGridVisibleColumns = columns }
        });
        if (saved.IsSuccess)
        {
            Raise($"Show{key}Column");
        }
    }

    public bool TryGetColumnWidth(string key, out double width)
    {
        return _main.AppOptions.Ui.LibraryGridColumnWidths.TryGetValue(key, out width);
    }

    public bool TryGetColumnOrder(string key, out int order)
    {
        return _main.AppOptions.Ui.LibraryGridColumnOrder.TryGetValue(key, out order);
    }

    public void SetColumnWidth(string key, double width)
    {
        if (width <= 0)
        {
            return;
        }

        Dictionary<string, double> widths = new(_main.AppOptions.Ui.LibraryGridColumnWidths,
            StringComparer.Ordinal);
        widths[key] = width;
        _main.UpdateAppOptions(_main.AppOptions with
        {
            Ui = _main.AppOptions.Ui with { LibraryGridColumnWidths = widths }
        });
    }

    public void SetColumnOrder(string key, int order)
    {
        if (order < 0)
        {
            return;
        }

        Dictionary<string, int> orders = new(_main.AppOptions.Ui.LibraryGridColumnOrder,
            StringComparer.Ordinal);
        orders[key] = order;
        _main.UpdateAppOptions(_main.AppOptions with
        {
            Ui = _main.AppOptions.Ui with { LibraryGridColumnOrder = orders }
        });
    }

    public void NotifyMinerUTokenChanged()
    {
        Raise(nameof(MinerUToken));
    }

    public async Task RefreshItemsAsync()
    {
        string? primaryItemId = SelectedItem?.ItemId;
        HashSet<string> selectedItemIds = SelectedItems.Select(item => item.ItemId).ToHashSet(StringComparer.Ordinal);
        AppServices services = await _main.ServicesAsync();
        Result<LibraryMetadata> library = await services.Library.GetCurrentLibraryAsync();
        if (library.IsSuccess && LibraryName != library.Value.DisplayName)
        {
            LibraryName = library.Value.DisplayName;
            Raise(nameof(LibraryName));
            _main.RaiseLibraryTitleChanged();
        }

        bool isTrashScope = Sidebar.SelectedScope == LibrarySidebarScope.Trash;
        IReadOnlyList<string>? requiredTags = null;
        if (!isTrashScope)
        {
            IReadOnlyList<string> pinnedTags = await LoadPinnedTagsAsync(services);
            await Sidebar.LoadTagsAsync(services.Tags, services.LibraryItems, pinnedTags);
            Sidebar.ApplyPinnedOrder(pinnedTags);
            requiredTags = Sidebar.GetSelectedTagNames();
        }

        Result<IReadOnlyList<LibraryItemRow>> rowsResult = isTrashScope
            ? await services.LibraryItems.ListTrashedRowsAsync()
            : await services.LibraryItems.ListRowsAsync(requiredTags);
        if (rowsResult.IsFailure)
        {
            throw new InvalidOperationException(rowsResult.ErrorMessage);
        }

        if (!isTrashScope && Sidebar.IsNoTagSelected)
        {
            rowsResult = Result<IReadOnlyList<LibraryItemRow>>.Success(
                rowsResult.Value.Where(row => row.Tags is null || row.Tags.Count == 0).ToArray());
        }

        List<LibraryItemViewModel> refreshedItems = new();
        List<string> refreshedRecentItems = new();
        List<string> refreshedRecentDocuments = new();
        foreach (LibraryItemRow row in rowsResult.Value)
        {
            refreshedItems.Add(CreateItemViewModel(row));
            if (!isTrashScope)
            {
                refreshedRecentItems.Add(row.Title);
                if (!string.IsNullOrWhiteSpace(row.LinkedFileName))
                {
                    refreshedRecentDocuments.Add(row.LinkedFileName);
                }
            }
        }

        Items.Clear();
        foreach (LibraryItemViewModel item in refreshedItems)
        {
            Items.Add(item);
        }

        RecentItems.Clear();
        RecentDocuments.Clear();
        if (!isTrashScope)
        {
            foreach (string item in refreshedRecentItems)
            {
                RecentItems.Add(item);
            }

            foreach (string document in refreshedRecentDocuments)
            {
                RecentDocuments.Add(document);
            }
        }

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

    private async Task<IReadOnlyList<string>> LoadPinnedTagsAsync(AppServices services)
    {
        Result<PinnedTagsAppSettings?> result =
            await services.LibrarySettingCoordinator.ReadAsync<PinnedTagsAppSettings>(
                LibrarySettingKeys.PinnedTags, true);
        if (result.IsFailure || result.Value is null)
        {
            return Array.Empty<string>();
        }

        return TagNormalizer.NormalizeMany(result.Value.Tags);
    }

    private async Task SavePinnedTagsAsync(IReadOnlyList<string> pinnedTags)
    {
        AppServices services = await _main.ServicesAsync();
        Result<LibraryMetadata> library = await services.Library.GetCurrentLibraryAsync();
        if (library.IsFailure)
        {
            return;
        }

        PinnedTagsAppSettings settings = new(pinnedTags.ToArray());
        await services.LibrarySettingCoordinator.SaveEnabledAsync(
            LibrarySettingKeys.PinnedTags,
            settings,
            _main.AppOptions.Sync.DeviceId,
            _ => Task.FromResult(SettingsSaveResult.Success));
    }

    private async Task ToggleTagPinAsync(TagListItemViewModel tag)
    {
        AppServices services = await _main.ServicesAsync();
        IReadOnlyList<string> pinnedTags = await LoadPinnedTagsAsync(services);
        List<string> next = new(pinnedTags);
        if (tag.IsPinned)
        {
            next.RemoveAll(name => string.Equals(name, tag.Name, StringComparison.Ordinal));
            tag.IsPinned = false;
        }
        else
        {
            if (!next.Contains(tag.Name, StringComparer.Ordinal))
            {
                next.Add(tag.Name);
            }

            tag.IsPinned = true;
        }

        await SavePinnedTagsAsync(next);
        Sidebar.ApplyPinnedOrder(next);
    }

    private async Task RemoveTagAsync(TagListItemViewModel tag)
    {
        if (tag.IsNoTagEntry)
        {
            return;
        }

        ConfirmDialogResult? result = await _main.Dialogs.ShowDialogAsync<ConfirmDialogResult>(
            new ConfirmDialogViewModel(
                "移除标签",
                $"将从所有活动题录中移除标签“{tag.Name}”。",
                "移除",
                confirmDanger: true));
        if (result != ConfirmDialogResult.Confirm)
        {
            return;
        }

        AppServices services = await _main.ServicesAsync();
        Result deleteResult = await services.Tags.RemoveTagAsync(tag.Name);
        if (!deleteResult.IsSuccess)
        {
            _main.ReportError($"移除标签失败：{deleteResult.ErrorMessage}");
        }
    }

    private async Task RenameTagAsync(TagListItemViewModel tag)
    {
        if (tag.IsNoTagEntry)
        {
            return;
        }

        string? newName = await _main.Dialogs.ShowDialogAsync<string?>(
            new TagNamePromptDialogViewModel(
                "重命名标签",
                $"将标签“{tag.Name}”重命名为：",
                "重命名"));
        if (string.IsNullOrWhiteSpace(newName))
        {
            return;
        }

        newName = TagNormalizer.Normalize(newName)!;
        if (string.Equals(newName, tag.Name, StringComparison.Ordinal))
        {
            return;
        }

        bool targetExists = Sidebar.Tags.Any(t => !t.IsNoTagEntry &&
                                                  string.Equals(t.Name, newName, StringComparison.Ordinal));
        if (targetExists)
        {
            ConfirmDialogResult? confirm = await _main.Dialogs.ShowDialogAsync<ConfirmDialogResult>(
                new ConfirmDialogViewModel(
                    "合并标签",
                    $"标签“{newName}”已存在。是否将“{tag.Name}”合并到“{newName}”？",
                    "合并",
                    confirmDanger: true));
            if (confirm != ConfirmDialogResult.Confirm)
            {
                return;
            }
        }

        AppServices services = await _main.ServicesAsync();
        Result result = await services.Tags.RenameTagAsync(tag.Name, newName);
        if (!result.IsSuccess)
        {
            _main.ReportError($"重命名标签失败：{result.ErrorMessage}");
        }
    }

    private async Task MergeTagAsync(TagListItemViewModel tag)
    {
        if (tag.IsNoTagEntry)
        {
            return;
        }

        string? targetName = await _main.Dialogs.ShowDialogAsync<string?>(
            new TagNamePromptDialogViewModel(
                "合并标签",
                $"将标签“{tag.Name}”合并到：",
                "合并"));
        if (string.IsNullOrWhiteSpace(targetName))
        {
            return;
        }

        targetName = TagNormalizer.Normalize(targetName)!;
        if (string.Equals(targetName, tag.Name, StringComparison.Ordinal))
        {
            return;
        }

        ConfirmDialogResult? confirm = await _main.Dialogs.ShowDialogAsync<ConfirmDialogResult>(
            new ConfirmDialogViewModel(
                "合并标签",
                $"是否将标签“{tag.Name}”合并到“{targetName}”？",
                "合并",
                confirmDanger: true));
        if (confirm != ConfirmDialogResult.Confirm)
        {
            return;
        }

        AppServices services = await _main.ServicesAsync();
        Result result = await services.Tags.MergeTagsAsync(tag.Name, targetName);
        if (!result.IsSuccess)
        {
            _main.ReportError($"合并标签失败：{result.ErrorMessage}");
        }
    }

    /// <summary>
    /// Drops the selected library items onto a tag, adding that tag to each item.
    /// </summary>
    public async Task DropItemsOnTagAsync(IReadOnlyList<LibraryItemViewModel> items, string tagName)
    {
        if (items.Count == 0)
        {
            return;
        }

        string? normalized = TagNormalizer.Normalize(tagName);
        if (normalized is null)
        {
            return;
        }

        AppServices services = await _main.ServicesAsync();
        ItemId[] itemIds = items.Select(item => ItemId.Parse(item.ItemId)).ToArray();
        Result result = await services.Tags.AddTagsToItemsAsync(itemIds, [normalized]);
        if (!result.IsSuccess)
        {
            _main.ReportError($"添加标签失败：{result.ErrorMessage}");
        }
    }

    /// <summary>
    /// Merges the two selected library items after previewing the field choices. The dialog can
    /// swap which item is the source and which is the target.
    /// </summary>
    private async Task MergeSelectedItemsAsync()
    {
        if (SelectedItems.Count != 2)
        {
            return;
        }

        LibraryItemViewModel source = SelectedItems[0];
        LibraryItemViewModel target = SelectedItems[1];
        if (string.Equals(source.ItemId, target.ItemId, StringComparison.Ordinal))
        {
            return;
        }

        AppServices services = await _main.ServicesAsync();
        ItemId sourceId = ItemId.Parse(source.ItemId);
        ItemId targetId = ItemId.Parse(target.ItemId);

        Result<ItemMergePreview> previewResult =
            await services.MergeItems.BuildMergePreviewAsync(sourceId, targetId);
        if (previewResult.IsFailure)
        {
            _main.ReportError($"无法预览合并：{previewResult.ErrorMessage}");
            return;
        }

        ItemMergePreviewDialogViewModel dialog = new(
            previewResult.Value,
            async (swapSource, swapTarget, cancellationToken) =>
                await services.MergeItems.BuildMergePreviewAsync(swapSource, swapTarget, cancellationToken));

        ItemMergeDialogResult? result = await _main.Dialogs.ShowDialogAsync<ItemMergeDialogResult?>(dialog);
        if (result != ItemMergeDialogResult.Merge)
        {
            return;
        }

        Result mergeResult = await services.MergeItems.MergeAsync(
            dialog.CurrentSourceItemId,
            dialog.CurrentTargetItemId,
            dialog.GetChoices(),
            _main.ItemHasUnsavedEdits);

        if (!mergeResult.IsSuccess)
        {
            _main.ReportError($"合并失败：{mergeResult.ErrorMessage}");
        }
    }

    /// <summary>
    /// Drops the selected library items onto the "no tag" entry after confirmation,
    /// clearing every tag from those items.
    /// </summary>
    public async Task DropItemsOnNoTagAsync(IReadOnlyList<LibraryItemViewModel> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        ConfirmDialogResult? result = await _main.Dialogs.ShowDialogAsync<ConfirmDialogResult>(
            new ConfirmDialogViewModel(
                "清空标签",
                $"将清空 {items.Count} 个题录的全部标签。",
                "清空",
                confirmDanger: true));
        if (result != ConfirmDialogResult.Confirm)
        {
            return;
        }

        AppServices services = await _main.ServicesAsync();
        ItemId[] itemIds = items.Select(item => ItemId.Parse(item.ItemId)).ToArray();
        Result clearResult = await services.Tags.SetTagsAsync(itemIds, Array.Empty<string>());
        if (!clearResult.IsSuccess)
        {
            _main.ReportError($"清空标签失败：{clearResult.ErrorMessage}");
        }
    }

    /// <summary>
    /// Drops one tag onto another, merging the source tag into the target tag after confirmation.
    /// </summary>
    public async Task DropTagOnTagAsync(string sourceTag, string targetTag)
    {
        string? normalizedSource = TagNormalizer.Normalize(sourceTag);
        string? normalizedTarget = TagNormalizer.Normalize(targetTag);
        if (normalizedSource is null || normalizedTarget is null ||
            string.Equals(normalizedSource, normalizedTarget, StringComparison.Ordinal))
        {
            return;
        }

        ConfirmDialogResult? result = await _main.Dialogs.ShowDialogAsync<ConfirmDialogResult>(
            new ConfirmDialogViewModel(
                "合并标签",
                $"是否将标签“{normalizedSource}”合并到“{normalizedTarget}”？",
                "合并",
                confirmDanger: true));
        if (result != ConfirmDialogResult.Confirm)
        {
            return;
        }

        AppServices services = await _main.ServicesAsync();
        Result mergeResult = await services.Tags.MergeTagsAsync(normalizedSource, normalizedTarget);
        if (!mergeResult.IsSuccess)
        {
            _main.ReportError($"合并标签失败：{mergeResult.ErrorMessage}");
        }
    }

    /// <summary>
    /// Detects duplicate library items and lets the user process or skip each pair.
    /// </summary>
    public async Task DetectDuplicatesAsync()
    {
        AppServices services = await _main.ServicesAsync();
        IReadOnlyList<DuplicateItemPair> pairs = await services.DuplicateItemDetection.FindDuplicatesAsync();

        if (pairs.Count == 0)
        {
            await _main.Dialogs.ShowDialogAsync<ConfirmDialogResult>(
                new ConfirmDialogViewModel(
                    "检测重复题录",
                    "未检测到重复题录。",
                    "确定"));
            return;
        }

        Dictionary<ItemId, string> titles = new();
        foreach (DuplicateItemPair pair in pairs)
        {
            await EnsureTitleAsync(pair.ItemIdA);
            await EnsureTitleAsync(pair.ItemIdB);
        }

        DuplicateItemsDialogViewModel dialog = new(
            pairs,
            titles,
            async pair => await ProcessDuplicatePairAsync(services, pair));

        await _main.Dialogs.ShowDialogAsync<DuplicateItemsDialogResult>(dialog);

        async Task EnsureTitleAsync(ItemId itemId)
        {
            if (titles.ContainsKey(itemId))
            {
                return;
            }

            Result<ItemMetadata> item = await services.Items.GetItemAsync(itemId);
            titles[itemId] = item.IsSuccess ? item.Value.Title : itemId.ToString();
        }
    }

    private async Task<bool> ProcessDuplicatePairAsync(AppServices services, DuplicateItemPair pair)
    {
        ItemId sourceId = pair.ItemIdA == pair.DefaultTargetItemId ? pair.ItemIdB : pair.ItemIdA;
        ItemId targetId = pair.DefaultTargetItemId;

        Result<ItemMergePreview> preview = await services.MergeItems.BuildMergePreviewAsync(sourceId, targetId);
        if (preview.IsFailure)
        {
            _main.ReportError($"无法预览合并：{preview.ErrorMessage}");
            return false;
        }

        ItemMergePreviewDialogViewModel dialog = new(
            preview.Value,
            async (swapSource, swapTarget, cancellationToken) =>
                await services.MergeItems.BuildMergePreviewAsync(swapSource, swapTarget, cancellationToken));

        ItemMergeDialogResult? result = await _main.Dialogs.ShowDialogAsync<ItemMergeDialogResult?>(dialog);
        if (result != ItemMergeDialogResult.Merge)
        {
            return false;
        }

        Result mergeResult = await services.MergeItems.MergeAsync(
            dialog.CurrentSourceItemId,
            dialog.CurrentTargetItemId,
            dialog.GetChoices(),
            _main.ItemHasUnsavedEdits);

        if (!mergeResult.IsSuccess)
        {
            _main.ReportError($"合并失败：{mergeResult.ErrorMessage}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Applies a host commit notification by fetching only the changed rows and updating the
    /// collection by stable primary key. Never clears and reloads the whole Library, and never
    /// runs a long database query synchronously on the UI dispatcher.
    /// </summary>
    public async Task ApplyChangeSetAsync(IReadOnlyCollection<ItemId> itemIds)
    {
        if (itemIds.Count == 0)
        {
            return;
        }

        if (Sidebar.SelectedScope == LibrarySidebarScope.Trash)
        {
            await DispatcherTasks.RunAsync(RefreshItemsAsync);
            return;
        }

        AppServices services = await _main.ServicesAsync();
        Result<IReadOnlyList<LibraryItemRow>> rowsResult =
            await Task.Run(() => services.LibraryItems.GetRowsByIdsAsync(itemIds));
        if (rowsResult.IsFailure)
        {
            await DispatcherTasks.RunAsync(RefreshItemsAsync);
            return;
        }

        await DispatcherTasks.RunAsync(() =>
        {
            ApplyRows(rowsResult.Value, itemIds);
            return Task.CompletedTask;
        });
    }

    private void ApplyRows(IReadOnlyList<LibraryItemRow> rows, IReadOnlyCollection<ItemId>? removedItemIds = null)
    {
        Dictionary<string, int> indexByItemId = new(StringComparer.Ordinal);
        for (int index = 0; index < Items.Count; index++)
        {
            indexByItemId[Items[index].ItemId] = index;
        }

        bool selectedChanged = false;
        string? selectedItemId = SelectedItem?.ItemId;
        foreach (LibraryItemRow row in rows)
        {
            string key = row.ItemId.ToString();
            if (indexByItemId.TryGetValue(key, out int existingIndex))
            {
                Items[existingIndex].ApplyRow(row);
            }
            else
            {
                InsertItemByCreatedAt(CreateItemViewModel(row));
            }

            if (selectedItemId is not null &&
                string.Equals(selectedItemId, key, StringComparison.OrdinalIgnoreCase))
            {
                selectedChanged = true;
            }
        }

        if (removedItemIds is not null)
        {
            HashSet<string> returnedIds = rows.Select(row => row.ItemId.ToString())
                .ToHashSet(StringComparer.Ordinal);
            HashSet<string> removedIds = removedItemIds.Select(id => id.ToString())
                .Where(id => !returnedIds.Contains(id))
                .ToHashSet(StringComparer.Ordinal);
            for (int index = Items.Count - 1; index >= 0; index--)
            {
                if (!removedIds.Contains(Items[index].ItemId))
                {
                    continue;
                }

                if (selectedItemId is not null &&
                    string.Equals(selectedItemId, Items[index].ItemId, StringComparison.OrdinalIgnoreCase))
                {
                    selectedChanged = true;
                }

                Items.RemoveAt(index);
            }
        }

        if (selectedChanged)
        {
            Raise(nameof(InspectorTitle));
            Raise(nameof(InspectorSubtitle));
            Raise(nameof(InspectorStatus));
            Raise(nameof(InspectorPath));
        }

        if (SelectedItem is not null &&
            removedItemIds is not null &&
            removedItemIds.Any(id => string.Equals(id.ToString(), SelectedItem.ItemId, StringComparison.Ordinal)))
        {
            _ = Inspector.LoadAsync(ParseItemIdOrNull(SelectedItem.ItemId));
        }
    }

    /// <summary>
    /// Routes the complete host notification to its affected item rows. Both item and document
    /// changes are projected by stable IDs; no full Library refresh is needed after a commit.
    /// </summary>
    public async Task ApplyChangeSetAsync(LibraryChangeSet changeSet)
    {
        await ApplyChangeSetAsync(changeSet.ItemIds);
        await ApplyDocumentChangeSetAsync(changeSet.DocumentInstanceIds);
    }

    /// <summary>
    /// Resolves the owning items of the given document instances and refreshes only those rows by
    /// stable primary key, so a terminal OCR event updates just the affected row instead of
    /// clearing and rebuilding the whole Library.
    /// </summary>
    public async Task ApplyDocumentChangeSetAsync(IReadOnlyCollection<DocumentInstanceId> documentInstanceIds)
    {
        if (documentInstanceIds.Count == 0)
        {
            return;
        }

        AppServices services = await _main.ServicesAsync();
        Result<IReadOnlyList<ItemId>> itemIds = await Task.Run(() =>
            services.LibraryItems.GetItemIdsByDocumentInstanceIdsAsync(documentInstanceIds));
        if (itemIds.IsSuccess)
        {
            await ApplyChangeSetAsync(itemIds.Value);
        }
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
            EditMetadataForItemAsync,
            ViewPdfForItemAsync,
            createdAt: row.CreatedAt,
            primaryDocumentOcrIndexState: row.PrimaryDocumentOcrIndexState,
            hasOcrText: row.HasOcrText);
    }

    private void InsertItemByCreatedAt(LibraryItemViewModel item)
    {
        int insertAt = Items.Count;
        for (int index = 0; index < Items.Count; index++)
        {
            // created_at is stored as ISO-8601 UTC ("O"), so ordinal comparison matches time order.
            if (string.CompareOrdinal(Items[index].CreatedAt, item.CreatedAt) < 0)
            {
                insertAt = index;
                break;
            }
        }

        Items.Insert(insertAt, item);
    }

    public async Task<LibraryItemViewModel?> ResolveDocumentItemAsync(string documentInstanceId)
    {
        AppServices services = await _main.ServicesAsync();
        Result<DocumentNavigationRow?> result =
            await services.LibraryItems.GetDocumentNavigationAsync(DocumentInstanceId.Parse(documentInstanceId));
        if (result.IsFailure || result.Value is null)
        {
            return null;
        }

        DocumentNavigationRow row = result.Value;

        LibraryItemViewModel? item = Items.FirstOrDefault(candidate =>
            string.Equals(candidate.ItemId, row.ItemId.ToString(), StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return null;
        }

        if (string.Equals(item.DocumentInstanceId, row.DocumentInstanceId.ToString(),
                StringComparison.OrdinalIgnoreCase))
        {
            return item;
        }

        return new LibraryItemViewModel(
            item.ItemId,
            item.Title,
            item.ItemType,
            item.Authors,
            item.Year,
            item.PublicationTitle,
            item.Publisher,
            row.DocumentInstanceId.ToString(),
            row.FileAssetId,
            row.FileName,
            row.SourcePath,
            row.PageCount,
            row.SearchUnitCount,
            row.IndexStatus,
            RunOcrForItemAsync,
            EditMetadataForItemAsync,
            ViewPdfForItemAsync);
    }

    private Task RefreshItemsOnUiThreadAsync()
    {
        return DispatcherTasks.RunAsync(RefreshItemsAsync);
    }

    public Task RunOcrForItemAsync(LibraryItemViewModel item)
    {
        SelectedItem = item;
        return RunOcrForItemsAsync([item], OcrQueuePriority.UserStartedDocument, "OCR");
    }

    public Task RunOcrBatchAsync(IReadOnlyList<LibraryItemViewModel> items)
    {
        return RunOcrForItemsAsync(items, OcrQueuePriority.BatchCollection, "所选题录 OCR");
    }

    private Task RunQuickFillOcrAsync()
    {
        IReadOnlyList<LibraryItemViewModel> candidates = SelectQuickFillOcrCandidates(Items);
        if (candidates.Count == 0)
        {
            _main.Report("当前筛选结果中没有可快速补全 OCR 的文献。");
            return Task.CompletedTask;
        }

        return RunOcrForItemsAsync(candidates, OcrQueuePriority.BatchCollection, "快速补全 OCR");
    }

    internal static IReadOnlyList<LibraryItemViewModel> SelectQuickFillOcrCandidates(
        IEnumerable<LibraryItemViewModel> items)
    {
        return items.Where(static item =>
                !item.HasOcrText &&
                item.OcrIndexState != PrimaryDocumentOcrIndexState.OcrRunning &&
                !string.IsNullOrWhiteSpace(item.DocumentInstanceId) &&
                !string.IsNullOrWhiteSpace(item.SourcePath))
            .ToArray();
    }

    private async Task RunOcrForItemsAsync(IReadOnlyList<LibraryItemViewModel> items, string priority, string operation)
    {
        if (items.Count == 0)
        {
            _main.Report("请先选择题录。");
            return;
        }

        IsBusy = true;
        Raise(nameof(IsBusy));
        Raise(nameof(InspectorStatus));
        try
        {
            AppServices services = await _main.ServicesAsync();
            string documentEngine = _main.AppOptions.OcrEngines.EngineFor(OcrScope.Document);
            IRealOcrAdapter? adapter = services.OcrAdapters.GetAdapter(documentEngine);
            if (adapter is null)
            {
                _main.ReportError($"未注册 OCR 引擎：{documentEngine}");
                return;
            }

            if (RequiresMinerUToken(documentEngine, adapter.GetCapability()))
            {
                string token = await ResolveMinerUTokenAsync();
                if (string.IsNullOrWhiteSpace(token))
                {
                    await _main.OpenSettingsAsync("mineru", "运行 OCR 前需要 MinerU API token。请先在设置中完成配置。");
                    _main.Report("运行 OCR 前需要 MinerU API token。请先在设置中完成配置。");
                    return;
                }
            }

            OcrPresetId presetId;
            try
            {
                presetId = await EnsurePresetForEngineAsync(services, documentEngine);
            }
            catch (Exception exception)
            {
                _main.ReportError($"OCR preset 不可用：{exception.Message}");
                return;
            }

            int succeeded = 0;
            int failed = 0;
            int skipped = 0;
            foreach (LibraryItemViewModel item in items)
            {
                if (string.IsNullOrWhiteSpace(item.DocumentInstanceId) || string.IsNullOrWhiteSpace(item.SourcePath))
                {
                    skipped++;
                    item.ApplyPrimaryDocumentOcrIndexState(
                        PrimaryDocumentOcrIndexState.Resolve(false, null, null, false, false));
                    continue;
                }

                DocumentInstanceId documentInstanceId;
                try
                {
                    documentInstanceId = DocumentInstanceId.Parse(item.DocumentInstanceId);
                }
                catch (FormatException)
                {
                    failed++;
                    _main.ReportError($"{item.Title} OCR 入队失败：文档标识无效。");
                    continue;
                }

                Result<OcrQueueTask> queued = await QueueOcrForItemAsync(
                    services, documentInstanceId, presetId, priority);
                if (queued.IsSuccess)
                {
                    succeeded++;
                    item.ApplyPrimaryDocumentOcrIndexState(
                        PrimaryDocumentOcrIndexState.Resolve(true, "running", null, false, false));
                }
                else
                {
                    failed++;
                    _main.ReportError($"{item.Title} OCR 入队失败：{queued.ErrorMessage ?? "未知错误"}");
                }
            }

            string summary = $"{operation}：成功入队 {succeeded}，失败 {failed}，无可用文档源 {skipped}。";
            if (failed > 0)
            {
                _main.ReportError(summary);
            }
            else
            {
                _main.Report(summary);
            }

            Raise(nameof(InspectorStatus));
            await _main.OcrQueue.RefreshAsync();
        }
        finally
        {
            IsBusy = false;
            Raise(nameof(IsBusy));
            Raise(nameof(InspectorStatus));
        }
    }

    internal static bool RequiresMinerUToken(string engineId, OcrEngineCapability capability)
    {
        return engineId == OcrEngineIds.MinerU && capability.RequiresCredential;
    }

    private async Task LookupMetadataBatchAsync()
    {
        if (IsMetadataBatchBusy || SelectedItems.Count == 0)
        {
            return;
        }

        ItemId[] itemIds = SelectedItems.Select(item => ItemId.Parse(item.ItemId)).ToArray();
        _metadataBatchCancellation = new CancellationTokenSource();
        IsMetadataBatchBusy = true;
        MetadataBatchProgress = 0;
        MetadataBatchStatus = $"正在获取 0/{itemIds.Length} 个题录的元数据...";
        MetadataLookupProgressInfo latest = new(0, itemIds.Length, 0, 0, null);
        try
        {
            MetadataLookupOutcome outcome = await MetadataLookupUiBridge.LookupBatchAsync(
                await _main.ServicesAsync(),
                itemIds,
                progress =>
                {
                    latest = progress;
                    MetadataBatchProgress = progress.Total <= 0 ? 0 : 100d * progress.Completed / progress.Total;
                    MetadataBatchStatus =
                        $"正在获取 {progress.Completed}/{Math.Max(progress.Total, itemIds.Length)} 个题录的元数据...";
                },
                _metadataBatchCancellation.Token);

            await RefreshItemsOnUiThreadAsync();
            await _main.RefreshOpenItemEditorsAsync(itemIds);
            int failed = Math.Max(latest.Failed, outcome.FailedCount);
            int succeeded = Math.Max(latest.Succeeded, outcome.SucceededCount);
            if (!outcome.IsSuccess && failed == 0)
            {
                failed = itemIds.Length - succeeded;
            }

            MetadataBatchProgress = 100;
            MetadataBatchStatus = failed > 0
                ? $"批量获取完成：成功 {succeeded} 个，失败 {failed} 个。{outcome.Message}"
                : $"批量获取完成：成功 {Math.Max(succeeded, itemIds.Length)} 个。";
            if (failed > 0)
            {
                _main.ReportError(MetadataBatchStatus);
            }
            else
            {
                _main.Report(MetadataBatchStatus);
            }
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

    private async Task<Result<OcrQueueTask>> QueueOcrForItemAsync(AppServices services,
        DocumentInstanceId documentInstanceId, OcrPresetId presetId, string priority)
    {
        Result<IReadOnlyList<Patchouli.Core.Layout.Page>> pages =
            await services.Pages.ListPagesAsync(documentInstanceId);
        if (pages.IsFailure)
        {
            return Result<OcrQueueTask>.Failure(pages.ErrorCode!, pages.ErrorMessage!);
        }

        PageId[] pageIds = pages.Value.Select(static page => page.PageId).ToArray();

        if (pageIds.Length == 0)
        {
            return Result<OcrQueueTask>.Failure(AppErrorCodes.ValidationFailed,
                "Document instance has no pages to OCR.");
        }

        Result<OcrPresetVersion> version = await services.OcrPresets.GetCurrentVersionAsync(presetId);
        if (version.IsFailure)
        {
            return Result<OcrQueueTask>.Failure(version.ErrorCode!, version.ErrorMessage!);
        }

        Result<IOcrQueueScheduler> queue = await services.GetOcrQueueAsync();
        if (queue.IsFailure)
        {
            return Result<OcrQueueTask>.Failure(queue.ErrorCode!, queue.ErrorMessage!);
        }

        _main.OcrQueue.ObserveQueue(queue.Value);

        string adapterKind = version.Value.EngineId == OcrEngineIds.MinerU
            ? OcrAdapterKind.CloudApi
            : OcrAdapterKind.LocalLibrary;
        string? providerId = version.Value.EngineId == OcrEngineIds.MinerU ? ProviderIds.MinerU : null;
        return await services.Ocr.QueueDocumentOcrAsync(documentInstanceId, presetId, pageIds, version.Value.EngineId,
            adapterKind,
            providerId, priority);
    }

    public void ApplyOcrQueueRunningState(OcrQueueTask task)
    {
        LibraryItemViewModel? item = Items.FirstOrDefault(candidate =>
            string.Equals(candidate.DocumentInstanceId, task.DocumentInstanceId.ToString(),
                StringComparison.Ordinal));
        if (item is null)
        {
            return;
        }

        item.ApplyPrimaryDocumentOcrIndexState(
            PrimaryDocumentOcrIndexState.Resolve(true, "running", null, false, false));
        Raise(nameof(InspectorStatus));
    }

    public void ApplyOcrQueueTerminalState(OcrQueueTask task)
    {
        LibraryItemViewModel? item = Items.FirstOrDefault(candidate =>
            string.Equals(candidate.DocumentInstanceId, task.DocumentInstanceId.ToString(),
                StringComparison.Ordinal));
        if (item is null)
        {
            return;
        }

        ApplyDocumentChangeSetAsync([task.DocumentInstanceId])
            .Observe("library-shell-ocr", "refresh-terminal-ocr-state");
    }

    private async Task<string> ResolveMinerUTokenAsync()
    {
        if (!string.IsNullOrWhiteSpace(MinerUToken))
        {
            return MinerUToken.Trim();
        }

        if (!_main.HasOpenRuntimeDatabase)
        {
            return "";
        }

        string persisted = await _main.GetPersistedMinerUTokenAsync();
        MinerUToken = persisted;
        NotifyMinerUTokenChanged();
        return persisted;
    }

    internal static async Task<OcrPresetId> EnsureMinerUPresetAsync(AppServices services)
    {
        Result<OcrPreset?> existing = await services.OcrPresets.FindActivePresetByEngineIdAsync(OcrEngineIds.MinerU);
        if (existing.IsFailure)
        {
            throw new InvalidOperationException(existing.ErrorMessage);
        }

        if (existing.Value is not null)
        {
            return existing.Value.PresetId;
        }

        Result<OcrPreset> created = await services.OcrPresets.CreatePresetAsync(
            "MinerU OCR",
            "MinerU document OCR preset",
            OcrEngineIds.MinerU,
            OcrModelIds.MinerUDefault,
            null,
            """{"isOcr":true,"enableTable":true,"enableFormula":true}""",
            true);
        if (created.IsFailure)
        {
            throw new InvalidOperationException(created.ErrorMessage);
        }

        return created.Value.PresetId;
    }

    internal static async Task<OcrPresetId> EnsureNdlKotenPresetAsync(AppServices services)
    {
        Result<OcrPreset?> existing = await services.OcrPresets.FindActivePresetByEngineIdAsync(OcrEngineIds.NdlKoten);
        if (existing.IsFailure)
        {
            throw new InvalidOperationException(existing.ErrorMessage);
        }

        if (existing.Value is not null)
        {
            return existing.Value.PresetId;
        }

        Result<OcrPreset> created = await services.OcrPresets.CreatePresetAsync(
            "NDL Koten OCR Lite",
            "Local classical Japanese OCR preset",
            OcrEngineIds.NdlKoten,
            OcrModelIds.NdlKotenDefault,
            services.OcrStorage.NdlKotenModelsDirectory,
            "{}",
            true);
        if (created.IsFailure)
        {
            throw new InvalidOperationException(created.ErrorMessage);
        }

        return created.Value.PresetId;
    }

    internal static async Task<OcrPresetId> EnsurePresetForEngineAsync(AppServices services, string engineId)
    {
        return engineId switch
        {
            OcrEngineIds.MinerU => await EnsureMinerUPresetAsync(services),
            OcrEngineIds.NdlKoten => await EnsureNdlKotenPresetAsync(services),
            _ => throw new InvalidOperationException($"未实现默认 OCR preset 的引擎：{engineId}")
        };
    }

    public Task EditMetadataForItemAsync(LibraryItemViewModel item)
    {
        SelectedItem = item;
        return _main.EditSelectedItemCommand.ExecuteAsync();
    }

    public Task ViewPdfForItemAsync(LibraryItemViewModel item)
    {
        return _main.ShowReadingAsync(item);
    }

    private async Task DeleteSelectedItemsAsync()
    {
        if (SelectedItems.Count == 0 || Sidebar.SelectedScope != LibrarySidebarScope.Active)
        {
            return;
        }

        AppServices services = await _main.ServicesAsync();
        ItemId[] itemIds = SelectedItems.Select(item => ItemId.Parse(item.ItemId)).ToArray();
        Result result = await services.Items.DeleteItemsAsync(itemIds);
        if (!result.IsSuccess)
        {
            _main.ReportError($"删除题录失败：{result.ErrorMessage}");
        }
    }

    private async Task PurgeSelectedItemsAsync()
    {
        if (SelectedItems.Count == 0 || Sidebar.SelectedScope != LibrarySidebarScope.Trash)
        {
            return;
        }

        AppServices services = await _main.ServicesAsync();
        List<ItemPurgeDependencyReport> reports = new();
        List<string> reportFailures = new();
        foreach (LibraryItemViewModel item in SelectedItems.ToArray())
        {
            Result<ItemPurgeDependencyReport> report =
                await services.PurgeItems.BuildPurgeReportAsync(ItemId.Parse(item.ItemId));
            if (report.IsFailure)
            {
                reportFailures.Add($"{item.Title}：{report.ErrorMessage}");
            }
            else
            {
                reports.Add(report.Value);
            }
        }

        if (reportFailures.Count > 0)
        {
            _main.ReportError($"无法生成删除报告：{string.Join("；", reportFailures)}");
            return;
        }

        if (reports.Any(report => report.HasActiveOcr))
        {
            _main.ReportError("无法永久删除：选中题录存在活动 OCR 任务。");
            return;
        }

        PurgeConfirmDialogViewModel dialog = new(
            reports.Select(report => SelectedItems.Single(item => item.ItemId == report.ItemId.ToString()).Title)
                .ToArray(),
            reports);
        bool? confirmed = await _main.Dialogs.ShowDialogAsync<bool?>(dialog);
        if (confirmed != true)
        {
            return;
        }

        Result result = await services.PurgeItems.PurgeItemsAsync(reports.Select(report => report.ItemId).ToArray());
        if (!result.IsSuccess)
        {
            _main.ReportError($"永久删除失败：{result.ErrorMessage}");
            return;
        }

        ScheduleFileAssetGc(services);
    }

    private async Task RestoreSelectedItemsAsync()
    {
        if (SelectedItems.Count == 0 || Sidebar.SelectedScope != LibrarySidebarScope.Trash)
        {
            return;
        }

        AppServices services = await _main.ServicesAsync();
        ItemId[] itemIds = SelectedItems.Select(item => ItemId.Parse(item.ItemId)).ToArray();
        Result result = await services.Items.RestoreItemsAsync(itemIds);
        if (!result.IsSuccess)
        {
            _main.ReportError($"还原题录失败：{result.ErrorMessage}");
        }
    }

    private static void ScheduleFileAssetGc(AppServices services)
    {
#pragma warning disable CS4014
        Task.Run(async () =>
        {
            try
            {
                await services.FileAssetGc.RunAsync(new FileAssetGcOptions(TimeSpan.FromSeconds(2)));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _ = exception;
            }
        });
#pragma warning restore CS4014
    }

    public void RaisePageStateChanged()
    {
        Raise(nameof(IsLibraryLeftSidebarVisible));
        Raise(nameof(IsLibraryRightSidebarVisible));
        Raise(nameof(IsLibraryVisible));
        Raise(nameof(RuntimeDatabasePath));
        Raise(nameof(DefaultSyncRootPath));
        Raise(nameof(FileSearchRoots));
        Raise(nameof(HasFileSearchRoots));
        Raise(nameof(NoFileSearchRoots));
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
        if (!IsReadingMode)
        {
            return;
        }

        IsReadingMode = false;
        Raise(nameof(IsReadingMode));
        Raise(nameof(ShowLibraryList));
        Raise(nameof(ShowPdfWorkspace));
        _main.RaiseShellSelectionChanged();
    }

    public async Task RefreshAsync()
    {
        await UnexpectedExceptionBoundary.RunAsync(RefreshItemsAsync, "refresh-library-shell");
        Raise(nameof(StatusText));
        Raise(nameof(LibraryName));
    }
}

internal sealed record MetadataLookupOutcome(
    bool IsSuccess,
    string Message,
    int SucceededCount = 0,
    int FailedCount = 0);

internal sealed record MetadataLookupProgressInfo(int Completed, int Total, int Succeeded, int Failed, string? Message);

internal static class MetadataLookupUiBridge
{
    public static bool CanLookup(AppServices services, string scheme)
    {
        return services.MetadataLookup.CanLookup(scheme);
    }

    public static async Task<MetadataLookupOutcome> LookupAsync(
        AppServices services,
        ItemId itemId,
        ItemIdentifier identifier,
        CancellationToken cancellationToken)
    {
        Result<Patchouli.Core.Bibliography.MetadataLookup.MetadataLookupOutcome> result =
            await services.MetadataLookup.LookupAndApplyAsync(itemId, identifier, cancellationToken);
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
        Progress<MetadataBatchProgress> progress = new(value =>
            onProgress(new MetadataLookupProgressInfo(value.Completed, value.Total, value.Succeeded, value.Failed,
                value.Message)));
        Result<MetadataBatchResult> result =
            await services.MetadataLookup.LookupAndApplyBatchAsync(itemIds, progress, cancellationToken);
        return result.IsFailure
            ? new MetadataLookupOutcome(false, result.ErrorMessage ?? "批量元数据获取失败。")
            : new MetadataLookupOutcome(true, "", result.Value.SucceededCount, result.Value.FailedCount);
    }
}

public sealed class LibraryItemViewModel : ViewModelBase
{
    private string _ocrStatus;
    private PrimaryDocumentOcrIndexState _primaryDocumentOcrIndexState;

    public LibraryItemViewModel(
        string itemId,
        string title,
        string itemType,
        string authors,
        string year,
        string publicationTitle,
        string? publisher,
        string? documentInstanceId,
        string? fileAssetId,
        string fileName,
        string sourcePath,
        int pageCount,
        int searchUnitCount,
        string indexStatus,
        Func<LibraryItemViewModel, Task> runOcr,
        Func<LibraryItemViewModel, Task> editMetadata,
        Func<LibraryItemViewModel, Task>? viewPdf = null,
        string? ocrStatus = null,
        string? createdAt = null,
        PrimaryDocumentOcrIndexState? primaryDocumentOcrIndexState = null,
        bool hasOcrText = false)
    {
        ItemId = itemId;
        _title = title;
        _itemType = itemType;
        _authors = authors;
        _year = year;
        _publicationTitle = publicationTitle;
        _publisher = publisher;
        _documentInstanceId = documentInstanceId;
        _fileAssetId = fileAssetId;
        _fileName = fileName;
        _sourcePath = sourcePath;
        _pageCount = pageCount;
        _searchUnitCount = searchUnitCount;
        _indexStatus = indexStatus;
        CreatedAt = createdAt ?? "";
        _primaryDocumentOcrIndexState = primaryDocumentOcrIndexState ??
                                        PrimaryDocumentOcrIndexState.Resolve(documentInstanceId is not null, null, null,
                                            false, false);
        HasOcrText = hasOcrText;
        _ocrStatus = ocrStatus ?? _primaryDocumentOcrIndexState.Detail;
        RunOcrCommand = new AsyncCommand(() => runOcr(this));
        EditMetadataCommand = new AsyncCommand(() => editMetadata(this));
        ViewPdfCommand = new AsyncCommand(() => (viewPdf ?? editMetadata)(this));
    }

    public string ItemId { get; }
    public string CreatedAt { get; }

    private string _title = "";
    private string _itemType = "";
    private string _authors = "";
    private string _year = "";
    private string _publicationTitle = "";
    private string? _publisher;
    private string? _documentInstanceId;
    private string? _fileAssetId;
    private string _fileName = "";
    private string _sourcePath = "";
    private int _pageCount;
    private int _searchUnitCount;
    private string _indexStatus = "";

    public string Title
    {
        get => _title;
        set
        {
            if (_title == value)
            {
                return;
            }

            _title = value;
            Raise();
        }
    }

    public string ItemType
    {
        get => _itemType;
        set
        {
            if (_itemType == value)
            {
                return;
            }

            _itemType = value;
            Raise();
        }
    }

    public string Authors
    {
        get => _authors;
        set
        {
            if (_authors == value)
            {
                return;
            }

            _authors = value;
            Raise();
        }
    }

    public string Year
    {
        get => _year;
        set
        {
            if (_year == value)
            {
                return;
            }

            _year = value;
            Raise();
        }
    }

    public string PublicationTitle
    {
        get => _publicationTitle;
        set
        {
            if (_publicationTitle == value)
            {
                return;
            }

            _publicationTitle = value;
            Raise();
            Raise(nameof(SourceText));
        }
    }

    public string? Publisher
    {
        get => _publisher;
        set
        {
            if (_publisher == value)
            {
                return;
            }

            _publisher = value;
            Raise();
            Raise(nameof(SourceText));
        }
    }

    public string SourceText => ItemSourceTextResolver.Resolve(ItemType, PublicationTitle, Publisher);

    public string? DocumentInstanceId
    {
        get => _documentInstanceId;
        set
        {
            if (_documentInstanceId == value)
            {
                return;
            }

            _documentInstanceId = value;
            Raise();
        }
    }

    public string? FileAssetId
    {
        get => _fileAssetId;
        set
        {
            if (_fileAssetId == value)
            {
                return;
            }

            _fileAssetId = value;
            Raise();
        }
    }

    public string FileName
    {
        get => _fileName;
        set
        {
            if (_fileName == value)
            {
                return;
            }

            _fileName = value;
            Raise();
        }
    }

    public string SourcePath
    {
        get => _sourcePath;
        set
        {
            if (_sourcePath == value)
            {
                return;
            }

            _sourcePath = value;
            Raise();
        }
    }

    public int PageCount
    {
        get => _pageCount;
        set
        {
            if (_pageCount == value)
            {
                return;
            }

            _pageCount = value;
            Raise();
            Raise(nameof(PageCountDisplay));
        }
    }

    public int SearchUnitCount
    {
        get => _searchUnitCount;
        set
        {
            if (_searchUnitCount == value)
            {
                return;
            }

            _searchUnitCount = value;
            Raise();
        }
    }

    public string IndexStatus
    {
        get => _indexStatus;
        set
        {
            if (_indexStatus == value)
            {
                return;
            }

            _indexStatus = value;
            Raise();
        }
    }

    public string PageCountDisplay => PageCount <= 0 ? "-" : PageCount.ToString();
    public AsyncCommand RunOcrCommand { get; }
    public AsyncCommand EditMetadataCommand { get; }
    public AsyncCommand ViewPdfCommand { get; }

    public string OcrStatus
    {
        get => _ocrStatus;
        set
        {
            if (_ocrStatus == value)
            {
                return;
            }

            _ocrStatus = value;
            Raise();
        }
    }

    public string OcrIndexState => _primaryDocumentOcrIndexState.Value;
    public string OcrIndexStateLabel => _primaryDocumentOcrIndexState.ChineseLabel;
    public string OcrIndexStateDetail => _primaryDocumentOcrIndexState.Detail;
    public bool HasOcrText { get; private set; }

    public void ApplyPrimaryDocumentOcrIndexState(PrimaryDocumentOcrIndexState state)
    {
        _primaryDocumentOcrIndexState = state;
        OcrStatus = state.Detail;
        Raise(nameof(OcrIndexState));
        Raise(nameof(OcrIndexStateLabel));
        Raise(nameof(OcrIndexStateDetail));
    }

    /// <summary>
    /// Replaces every mutable display field from a fresh read-model row while keeping the
    /// stable ItemId and the collection position, so selection and virtualization survive.
    /// </summary>
    public void ApplyRow(LibraryItemRow row)
    {
        Title = row.Title;
        ItemType = row.ItemType;
        Authors = row.Authors;
        Year = row.Year ?? "";
        PublicationTitle = row.PublicationTitle ?? "";
        Publisher = row.Publisher;
        DocumentInstanceId = row.DocumentInstanceId?.ToString();
        FileAssetId = row.FileAssetId;
        FileName = row.LinkedFileName ?? "";
        SourcePath = row.SourcePath;
        PageCount = row.PageCount;
        SearchUnitCount = row.SearchUnitCount;
        IndexStatus = row.IndexStatus;
        HasOcrText = row.HasOcrText;
        ApplyPrimaryDocumentOcrIndexState(row.PrimaryDocumentOcrIndexState);
    }
}
