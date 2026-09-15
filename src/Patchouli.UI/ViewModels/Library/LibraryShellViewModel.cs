using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
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
using Patchouli.Host.Composition;
using Patchouli.Host.Import;
using Patchouli.UI.Diagnostics;

namespace Patchouli.UI.ViewModels;

public sealed partial class LibraryShellViewModel : ViewModelBase
{
    private static readonly TimeSpan TagSelectionThrottle = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan RevisionBufferWindow = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan SidebarEventThrottle = TimeSpan.Zero;
    private readonly MainWindowViewModel _main;
    private readonly IScheduler _timingScheduler = TaskPoolScheduler.Default;
    private readonly IScheduler _uiScheduler;
    private readonly Subject<Unit> _selectionChangedRequests = new();
    private readonly Subject<Unit> _tagReconcileRequests = new();
    private readonly Subject<Unit> _collectionReconcileRequests = new();
    private readonly SerialDisposable _revisionSubscription = new();
    private ItemId? _pendingInspectorItemId;
    private ILibraryRevisionService? _observedLibraryRevisions;

    public LibraryShellViewModel(MainWindowViewModel main)
    {
        _main = main;
        _uiScheduler = SynchronizationContext.Current is { } synchronizationContext
            ? new SynchronizationContextScheduler(synchronizationContext)
            : CurrentThreadScheduler.Instance;
        Sidebar = new LibrarySidebarViewModel();
        Inspector = new ItemInspectorViewModel(
            async () => (await _main.ServicesAsync()).Items,
            async () => (await _main.ServicesAsync()).Tags,
            async () => (await _main.ServicesAsync()).ItemTypeProfiles);
        Register(Sidebar);
        Register(Inspector);
        Register(_revisionSubscription);

        // The sidebar replaces its collection catalog on every reload, so the shell must re-raise
        // the computed context-menu source and visibility flags or the item context menu keeps a
        // stale ItemsSource reference.
        Register(Observable.FromEventPattern<PropertyChangedEventHandler, PropertyChangedEventArgs>(
                handler => Sidebar.PropertyChanged += handler,
                handler => Sidebar.PropertyChanged -= handler)
            .Subscribe(
                pattern =>
                {
                    if (string.IsNullOrEmpty(pattern.EventArgs.PropertyName) ||
                        pattern.EventArgs.PropertyName == nameof(LibrarySidebarViewModel.Collections))
                    {
                        Raise(nameof(CollectionContextItems));
                        Raise(nameof(HasCollections));
                    }
                },
                exception => UnexpectedExceptions.Sink.Report(exception, "library-shell-sidebar-watch")));

        Register(ReactiveUiFlow.SubscribeLatest(
            Observable.FromEventPattern(handler => Sidebar.ScopeChanged += handler,
                    handler => Sidebar.ScopeChanged -= handler)
                .Select(_ => Unit.Default),
            SidebarEventThrottle,
            _timingScheduler,
            _uiScheduler,
            RefreshScopeChangedAsync,
            exception => UnexpectedExceptions.Sink.Report(exception, "library-shell-scope-refresh")));

        IObservable<Unit> tagSelectionChanges = Observable.FromEventPattern(
                handler => Sidebar.TagSelectionChanged += handler,
                handler => Sidebar.TagSelectionChanged -= handler)
            .Select(_ => Unit.Default);
        Register(ReactiveUiFlow.SubscribeLatest(
            tagSelectionChanges,
            TagSelectionThrottle,
            _timingScheduler,
            _uiScheduler,
            RefreshItemsCoreAsync,
            exception => UnexpectedExceptions.Sink.Report(exception, "library-shell-tag-refresh")));

        // Rapid selection changes must not stack inspector loads: only the latest selection
        // resolves, and the inspector pipeline cancels the superseded fetch.
        Register(ReactiveUiFlow.SubscribeLatest(
            _selectionChangedRequests,
            SidebarEventThrottle,
            _timingScheduler,
            _uiScheduler,
            _ => Inspector.LoadAsync(_pendingInspectorItemId),
            exception => UnexpectedExceptions.Sink.Report(exception, "library-shell-inspector-load")));

        RegisterSequentialSidebarEvent(
            Observable.FromEventPattern<TagListItemViewModel>(handler => Sidebar.PinToggled += handler,
                handler => Sidebar.PinToggled -= handler),
            (tag, _) => ToggleTagPinAsync(tag));
        RegisterSequentialSidebarEvent(
            Observable.FromEventPattern<TagListItemViewModel>(handler => Sidebar.RemoveRequested += handler,
                handler => Sidebar.RemoveRequested -= handler),
            (tag, _) => RemoveTagAsync(tag));
        RegisterSequentialSidebarEvent(
            Observable.FromEventPattern<TagListItemViewModel>(handler => Sidebar.RenameRequested += handler,
                handler => Sidebar.RenameRequested -= handler),
            (tag, _) => RenameTagAsync(tag));
        RegisterSequentialSidebarEvent(
            Observable.FromEventPattern<TagListItemViewModel>(handler => Sidebar.MergeIntoRequested += handler,
                handler => Sidebar.MergeIntoRequested -= handler),
            (tag, _) => MergeTagAsync(tag));
        RegisterSequentialSidebarEvent(
            Observable.FromEventPattern(handler => Sidebar.CollectionSelectionChanged += handler,
                handler => Sidebar.CollectionSelectionChanged -= handler),
            (_, _) => RefreshItemsFromCollectionSelectionAsync());
        RegisterSequentialSidebarEvent(
            Observable.FromEventPattern(handler => Sidebar.CreateCollectionRequested += handler,
                handler => Sidebar.CreateCollectionRequested -= handler),
            (_, _) => CreateCollectionAsync());
        RegisterSequentialSidebarEvent(
            Observable.FromEventPattern<CollectionListItemViewModel>(
                handler => Sidebar.RenameCollectionRequested += handler,
                handler => Sidebar.RenameCollectionRequested -= handler),
            (collection, _) => RenameCollectionAsync(collection));
        RegisterSequentialSidebarEvent(
            Observable.FromEventPattern<CollectionListItemViewModel>(
                handler => Sidebar.DissolveCollectionRequested += handler,
                handler => Sidebar.DissolveCollectionRequested -= handler),
            (collection, _) => DissolveCollectionAsync(collection));
        RegisterSequentialSidebarEvent(
            Observable.FromEventPattern<CollectionListItemViewModel>(
                handler => Sidebar.AddToSelectionRequested += handler,
                handler => Sidebar.AddToSelectionRequested -= handler),
            (collection, _) => AddSelectedItemsToCollectionAsync(collection.CollectionId));

        // The batch-selection derived state is collection-driven: project it from
        // CollectionChanged instead of raising it by hand at every mutation site.
        IObservable<Unit> selectedItemsChanged = Observable
            .FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                handler => SelectedItems.CollectionChanged += handler,
                handler => SelectedItems.CollectionChanged -= handler)
            .Select(_ => Unit.Default);
        selectedItemsChanged
            .Select(_ => SelectedItems.Count)
            .BindOutput(this, count => SelectedItemCount = count, ImmediateScheduler.Instance, null, true,
                SelectedItems.Count);
        selectedItemsChanged
            .Select(_ => SelectedItems.Count > 0)
            .BindOutput(this, hasItems => HasBatchSelection = hasItems, ImmediateScheduler.Instance, null, true,
                SelectedItems.Count > 0);
        selectedItemsChanged
            .Select(_ => SelectedItems.Count <= 1)
            .BindOutput(this, single => IsSingleSelectionOrNone = single, ImmediateScheduler.Instance, null, true,
                true);
        selectedItemsChanged
            .Select(_ => SelectedItems.Count == 2)
            .BindOutput(this, canMerge => CanMergeSelectedItems = canMerge, ImmediateScheduler.Instance, null, true,
                false);

        // Committed-change reconciles reload sidebar state from a fresh snapshot; a latest-wins
        // pipeline discards stale runs when commits arrive faster than the reload completes.
        Register(ReactiveUiFlow.SubscribeLatest(
            _tagReconcileRequests,
            SidebarEventThrottle,
            _timingScheduler,
            _uiScheduler,
            ReconcileTagsAfterCommittedChangeAsync,
            exception => UnexpectedExceptions.Sink.Report(exception, "library-shell-tag-reconcile")));
        Register(ReactiveUiFlow.SubscribeLatest(
            _collectionReconcileRequests,
            SidebarEventThrottle,
            _timingScheduler,
            _uiScheduler,
            ReconcileCollectionsAfterCommittedChangeAsync,
            exception => UnexpectedExceptions.Sink.Report(exception, "library-shell-collection-reconcile")));

        RemoveSelectedItemsFromCurrentCollectionCommand =
            new AsyncCommand(RemoveSelectedItemsFromCurrentCollectionAsync);
        CreateCollectionCommand = new AsyncCommand(CreateCollectionAsync);
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

    private void RegisterSequentialSidebarEvent<TEventArgs>(
        IObservable<EventPattern<TEventArgs>> source,
        Func<TEventArgs, CancellationToken, Task> operation)
    {
        // Command-style sidebar requests must all be handled in order (no latest-wins dropping),
        // so they concatenate instead of switching.
        Register(source
            .Select(pattern => Observable.FromAsync(cancellationToken =>
                RunSidebarEventSafelyAsync(pattern.EventArgs, operation, cancellationToken)))
            .Concat()
            .Subscribe());
    }

    private static async Task RunSidebarEventSafelyAsync<TEventArgs>(
        TEventArgs eventArgs,
        Func<TEventArgs, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await operation(eventArgs, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // The reporter is an error-boundary callback and must never escape into Rx OnError.
            try
            {
                UnexpectedExceptions.Sink.Report(exception, "library-shell-sidebar-event");
            }
            catch (Exception reportException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Sidebar event reportError callback failed: {reportException}");
            }
        }
    }

    private async Task RefreshScopeChangedAsync(CancellationToken cancellationToken)
    {
        Raise(nameof(CanModifyLibraryItems));
        await RefreshItemsCoreAsync(cancellationToken);
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

        _revisionSubscription.Disposable = null;
        if (revisions is null)
        {
            // The main window detaches before switching databases: cancel any metadata batch in
            // flight so its completion cannot refresh the grid or editors against the next Library.
            CancelMetadataBatchForLibrarySwitch();
            _observedLibraryRevisions = null;
            return;
        }

        ILibraryRevisionService observedRevisions = revisions;
        _observedLibraryRevisions = observedRevisions;
        IObservable<LibraryChangeSet> changes = Observable
            .FromEventPattern<LibraryRevisionCommittedEventArgs>(
                handler => observedRevisions.ChangeCommitted += handler,
                handler => observedRevisions.ChangeCommitted -= handler)
            .Select(eventPattern => eventPattern.EventArgs.ChangeSet);
        _revisionSubscription.Disposable = ReactiveUiFlow.SubscribeBufferedSequential(
            changes,
            RevisionBufferWindow,
            _timingScheduler,
            _uiScheduler,
            (batch, cancellationToken) =>
                ApplyBufferedChangeSetsAsync(observedRevisions, batch, cancellationToken),
            exception => UnexpectedExceptions.Sink.Report(exception, "library-shell-revision"));
    }

    private async Task ApplyBufferedChangeSetsAsync(
        ILibraryRevisionService revisions,
        IReadOnlyList<LibraryChangeSet> batch,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(revisions, _observedLibraryRevisions))
        {
            return;
        }

        await ApplyChangeSetAsync(MergeChangeSets(batch));
    }

    internal static LibraryChangeSet MergeChangeSets(IReadOnlyList<LibraryChangeSet> changeSets)
    {
        if (changeSets.Count == 0)
        {
            return LibraryChangeSet.Empty;
        }

        return new LibraryChangeSet(
            changeSets.Max(changeSet => changeSet.NewRevision),
            changeSets.SelectMany(changeSet => changeSet.ItemIds).Distinct().ToArray(),
            changeSets.SelectMany(changeSet => changeSet.DocumentInstanceIds).Distinct().ToArray(),
            changeSets.SelectMany(changeSet => changeSet.StyleIds).Distinct(StringComparer.Ordinal).ToArray(),
            changeSets.SelectMany(changeSet => changeSet.PageIds).Distinct().ToArray(),
            changeSets.SelectMany(changeSet => changeSet.OcrRunIds).Distinct().ToArray(),
            changeSets.SelectMany(changeSet => changeSet.CollectionIds).Distinct().ToArray());
    }

    [ObservableProperty] public partial string LibraryName { get; set; } = "我的书库";

    public LibrarySidebarViewModel Sidebar { get; }
    public ItemInspectorViewModel Inspector { get; }
    public ObservableCollection<string> RecentItems { get; private set; } = new();
    public ObservableCollection<string> RecentDocuments { get; private set; } = new();
    public ObservableCollection<LibraryItemViewModel> Items { get; private set; } = new();
    public ObservableCollection<LibraryItemViewModel> SelectedItems { get; } = new();

    [ExcludeFromDerivedGeneration] public string StatusText => _main.Status;

    [ObservableProperty] public partial string MinerUToken { get; set; } = "";

    [ObservableProperty] public partial bool IsBusy { get; set; }

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

    [ObservableProperty] public partial LibraryItemViewModel? SelectedItem { get; set; }

    partial void OnSelectedItemChanged(LibraryItemViewModel? value)
    {
        // The derived Inspector*/HasSelectedItem/NoSelectedItem notifications come from the
        // derived-property generator; the inspector load goes through the latest-wins pipeline
        // registered in the constructor so rapid selection changes cancel the superseded fetch.
        _pendingInspectorItemId = ParseItemIdOrNull(value?.ItemId);
        _selectionChangedRequests.OnNext(Unit.Default);
    }

    public bool HasSelectedItem => SelectedItem is not null;
    public bool NoSelectedItem => SelectedItem is null;

    [ExcludeFromDerivedGeneration] public bool IsLibraryLeftSidebarVisible => _main.IsLibraryLeftSidebarVisible;

    [ExcludeFromDerivedGeneration] public bool IsLibraryRightSidebarVisible => _main.IsLibraryRightSidebarVisible;

    [ExcludeFromDerivedGeneration] public bool IsLibraryVisible => _main.IsLibraryVisible;

    [ExcludeFromDerivedGeneration] public string RuntimeDatabasePath => _main.RuntimeDatabasePath;

    [ExcludeFromDerivedGeneration] public string DefaultSyncRootPath => _main.DefaultSyncRootPath;

    [ExcludeFromDerivedGeneration]
    public ObservableCollection<SidebarFileSearchRootViewModel> FileSearchRoots => _main.FileSearchRoots;

    [ExcludeFromDerivedGeneration] public bool HasFileSearchRoots => _main.HasFileSearchRoots;

    [ExcludeFromDerivedGeneration] public bool NoFileSearchRoots => _main.NoFileSearchRoots;

    [ExcludeFromDerivedGeneration]
    public AsyncCommand RescanFileSearchRootsCommand => _main.RescanFileSearchRootsCommand;

    [ExcludeFromDerivedGeneration] public AsyncCommand EditSelectedItemCommand => _main.EditSelectedItemCommand;

    [ExcludeFromDerivedGeneration] public AsyncCommand ShowReadingCommand => _main.ShowReadingCommand;

    [ExcludeFromDerivedGeneration] public AsyncCommand RunSelectedItemOcrCommand => _main.RunSelectedItemOcrCommand;

    [ExcludeFromDerivedGeneration]
    public UiCommandDescriptor CopyCslBibliographyDescriptor => _main.CopyCslBibliographyDescriptor;

    [ExcludeFromDerivedGeneration] public UiCommandDescriptor ExportItemDescriptor => _main.ExportItemDescriptor;

    [ObservableProperty] public partial bool IsReadingMode { get; set; }

    public bool ShowLibraryList => !IsReadingMode;
    public bool ShowPdfWorkspace => IsReadingMode;
    public string InspectorTitle => SelectedItem?.Title ?? "";

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
    public AsyncCommand RemoveSelectedItemsFromCurrentCollectionCommand { get; }
    public AsyncCommand CreateCollectionCommand { get; }

    /// <summary>Collections offered as context-menu targets for the current selection.</summary>
    [ExcludeFromDerivedGeneration]
    public ObservableCollection<CollectionListItemViewModel> CollectionContextItems => Sidebar.Collections;

    [ExcludeFromDerivedGeneration] public bool HasCollections => Sidebar.Collections.Count > 0;

    [ExcludeFromDerivedGeneration] public bool HasSelectedCollectionFilter => Sidebar.HasSelectedCollection;

    [ExcludeFromDerivedGeneration] public string SelectedCollectionFilterName => Sidebar.SelectedCollectionName;

    [ExcludeFromDerivedGeneration] public bool CanModifyLibraryItems => Sidebar.IsActiveSelected;

    private CancellationTokenSource? _metadataBatchCancellation;
    private bool _cancelMetadataBatchForLibrarySwitch;

    [ObservableProperty] public partial bool IsMetadataBatchBusy { get; private set; }

    [ObservableProperty] public partial double MetadataBatchProgress { get; private set; }

    [ObservableProperty] public partial string MetadataBatchStatus { get; private set; } = "";

    partial void OnMetadataBatchStatusChanged(string value)
    {
        Raise(nameof(HasMetadataBatchStatus));
    }

    [ExcludeFromDerivedGeneration]
    public bool HasMetadataBatchStatus => !string.IsNullOrWhiteSpace(MetadataBatchStatus);

    [ObservableProperty] public partial int SelectedItemCount { get; private set; }

    [ObservableProperty] public partial bool HasBatchSelection { get; private set; }

    [ObservableProperty] public partial bool IsSingleSelectionOrNone { get; private set; } = true;

    [ObservableProperty] public partial bool CanMergeSelectedItems { get; private set; }

    public void SetSelectedItems(IEnumerable<LibraryItemViewModel> items)
    {
        LibraryItemViewModel[] selected = items.Distinct().ToArray();
        SelectedItems.Clear();
        foreach (LibraryItemViewModel item in selected)
        {
            SelectedItems.Add(item);
        }

        // SelectedItemCount/HasBatchSelection/IsSingleSelectionOrNone/CanMergeSelectedItems are
        // projected from CollectionChanged by the pipeline registered in the constructor.
        Raise(nameof(SelectedItems));
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

    public Task RefreshItemsAsync()
    {
        return RefreshItemsCoreAsync(CancellationToken.None);
    }

    private async Task RefreshItemsCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? primaryItemId = SelectedItem?.ItemId;
        HashSet<string> selectedItemIds = SelectedItems.Select(item => item.ItemId).ToHashSet(StringComparer.Ordinal);
        HostServices services = await _main.ServicesAsync();
        // Microsoft.Data.Sqlite executes synchronously under the async facade, so the database
        // reads run on a thread-pool thread to keep scope switching responsive.
        Result<LibraryMetadata> library = await Task.Run(
            () => services.Library.GetCurrentLibraryAsync(cancellationToken),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (library.IsSuccess && LibraryName != library.Value.DisplayName)
        {
            LibraryName = library.Value.DisplayName;
            _main.RaiseLibraryTitleChanged();
        }

        bool isTrashScope = Sidebar.SelectedScope == LibrarySidebarScope.Trash;
        IReadOnlyList<string>? requiredTags = null;
        CollectionId? requiredCollectionId = null;
        if (!isTrashScope)
        {
            Result<IReadOnlyList<Collection>> collections =
                await Task.Run(() => services.Collections.ListCollectionsAsync(cancellationToken), cancellationToken);
            if (collections.IsFailure)
            {
                throw new InvalidOperationException(collections.ErrorMessage);
            }

            Sidebar.LoadCollections(collections.Value);
            Raise(nameof(HasCollections));
            requiredCollectionId = Sidebar.SelectedCollection?.CollectionId;

            IReadOnlyList<string> pinnedTags = await Task.Run(
                () => LoadPinnedTagsAsync(services, cancellationToken),
                cancellationToken);
            await Sidebar.LoadTagsAsync(services.LibraryItemCache, pinnedTags, cancellationToken);
            Sidebar.ApplyPinnedOrder(pinnedTags);
            requiredTags = Sidebar.GetSelectedTagNames();
        }

        bool noTagSelected = !isTrashScope && Sidebar.IsNoTagSelected;

        // The row-to-view-model mapping is pure CPU work proportional to library size; run it
        // together with the query on a thread-pool thread, then swap the collections in one
        // reset notification each instead of per-item add notifications. Non-trash scopes read
        // the in-memory LibraryItemCache snapshot; the trash scope still queries SQLite directly.
        (List<LibraryItemViewModel> Items, List<string> RecentItems, List<string> RecentDocuments) refreshed =
            await Task.Run(async () =>
            {
                IReadOnlyList<LibraryItemRow> rows;
                if (isTrashScope)
                {
                    Result<IReadOnlyList<LibraryItemRow>> rowsResult =
                        await services.LibraryItems.ListTrashedRowsAsync(cancellationToken);
                    if (rowsResult.IsFailure)
                    {
                        throw new InvalidOperationException(rowsResult.ErrorMessage);
                    }

                    rows = rowsResult.Value;
                }
                else
                {
                    // Reload the snapshot so a full refresh always reflects the latest committed
                    // state (same cost as the previous direct ListRowsAsync query); the cache
                    // still gives LoadTagsAsync and the tag queries a consistent in-memory view.
                    Result refreshResult = await services.LibraryItemCache.RefreshAsync(cancellationToken);
                    if (refreshResult.IsFailure)
                    {
                        throw new InvalidOperationException(refreshResult.ErrorMessage);
                    }

                    rows = noTagSelected
                        ? services.LibraryItemCache.QueryUntagged()
                        : services.LibraryItemCache.QueryByTags(requiredTags);

                    if (requiredCollectionId is { } collectionId)
                    {
                        Result<IReadOnlyList<ItemId>> members =
                            await services.Collections.GetCollectionItemIdsAsync(collectionId, cancellationToken);
                        if (members.IsFailure)
                        {
                            throw new InvalidOperationException(members.ErrorMessage);
                        }

                        HashSet<ItemId> memberIds = members.Value.ToHashSet();
                        rows = rows.Where(row => memberIds.Contains(row.ItemId)).ToArray();
                    }
                }

                List<LibraryItemViewModel> items = new();
                List<string> recentItems = new();
                List<string> recentDocuments = new();
                foreach (LibraryItemRow row in rows)
                {
                    items.Add(CreateItemViewModel(row));
                    if (!isTrashScope)
                    {
                        recentItems.Add(row.Title);
                        if (!string.IsNullOrWhiteSpace(row.LinkedFileName))
                        {
                            recentDocuments.Add(row.LinkedFileName);
                        }
                    }
                }

                return (items, recentItems, recentDocuments);
            }, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        Items = new ObservableCollection<LibraryItemViewModel>(refreshed.Items);
        RecentItems = new ObservableCollection<string>(refreshed.RecentItems);
        RecentDocuments = new ObservableCollection<string>(refreshed.RecentDocuments);

        Raise(nameof(Items));
        Raise(nameof(RecentItems));
        Raise(nameof(RecentDocuments));

        // Publish the new Items collection before restoring the selection. The DataGrid selection
        // model only accepts items that belong to its current ItemsSource, and the ItemsSource
        // binding still points at the previous collection until the notification above is processed.
        SelectedItem = Items.FirstOrDefault(item => item.ItemId == primaryItemId) ?? Items.FirstOrDefault();
        SetSelectedItems(Items.Where(item => selectedItemIds.Contains(item.ItemId)));
    }

    private async Task<IReadOnlyList<string>> LoadPinnedTagsAsync(
        HostServices services,
        CancellationToken cancellationToken = default)
    {
        Result<PinnedTagsAppSettings?> result =
            await services.LibrarySettingCoordinator.ReadAsync<PinnedTagsAppSettings>(
                LibrarySettingKeys.PinnedTags, true, cancellationToken);
        if (result.IsFailure || result.Value is null)
        {
            return Array.Empty<string>();
        }

        return TagNormalizer.NormalizeMany(result.Value.Tags);
    }

    private async Task SavePinnedTagsAsync(IReadOnlyList<string> pinnedTags)
    {
        HostServices services = await _main.ServicesAsync();
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
        HostServices services = await _main.ServicesAsync();
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

        HostServices services = await _main.ServicesAsync();
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

        HostServices services = await _main.ServicesAsync();
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

        HostServices services = await _main.ServicesAsync();
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

        HostServices services = await _main.ServicesAsync();
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

        HostServices services = await _main.ServicesAsync();
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

        HostServices services = await _main.ServicesAsync();
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

        HostServices services = await _main.ServicesAsync();
        Result mergeResult = await services.Tags.MergeTagsAsync(normalizedSource, normalizedTarget);
        if (!mergeResult.IsSuccess)
        {
            _main.ReportError($"合并标签失败：{mergeResult.ErrorMessage}");
        }
    }

    private async Task RefreshItemsFromCollectionSelectionAsync()
    {
        Raise(nameof(CanModifyLibraryItems));
        Raise(nameof(HasSelectedCollectionFilter));
        Raise(nameof(SelectedCollectionFilterName));
        await RefreshItemsAsync();
    }

    private async Task CreateCollectionAsync()
    {
        string? name = await _main.Dialogs.ShowDialogAsync<string?>(
            new TagNamePromptDialogViewModel(
                "新建集合",
                "集合名称：",
                "创建"));
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        HostServices services = await _main.ServicesAsync();
        Result<Collection> result = await services.Collections.CreateCollectionAsync(name);
        if (result.IsFailure)
        {
            _main.ReportError($"创建集合失败：{result.ErrorMessage}");
            return;
        }

        await RefreshItemsAsync();
    }

    private async Task RenameCollectionAsync(CollectionListItemViewModel collection)
    {
        string? name = await _main.Dialogs.ShowDialogAsync<string?>(
            new TagNamePromptDialogViewModel(
                "重命名集合",
                $"将集合“{collection.Name}”重命名为：",
                "重命名"));
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        HostServices services = await _main.ServicesAsync();
        Result<Collection> result = await services.Collections.RenameCollectionAsync(collection.CollectionId, name);
        if (result.IsFailure)
        {
            _main.ReportError($"重命名集合失败：{result.ErrorMessage}");
            return;
        }

        await RefreshItemsAsync();
    }

    private async Task DissolveCollectionAsync(CollectionListItemViewModel collection)
    {
        ConfirmDialogResult? confirm = await _main.Dialogs.ShowDialogAsync<ConfirmDialogResult>(
            new ConfirmDialogViewModel(
                "解散集合",
                $"将解散集合“{collection.Name}”。集合中的题录不会被删除。",
                "解散",
                confirmDanger: true));
        if (confirm != ConfirmDialogResult.Confirm)
        {
            return;
        }

        HostServices services = await _main.ServicesAsync();
        Result result = await services.Collections.DissolveCollectionAsync(collection.CollectionId);
        if (!result.IsSuccess)
        {
            _main.ReportError($"解散集合失败：{result.ErrorMessage}");
            return;
        }

        if (Sidebar.SelectedCollection?.CollectionId == collection.CollectionId)
        {
            Sidebar.ClearCollectionSelection();
        }

        await RefreshItemsAsync();
    }

    private Task AddSelectedItemsToCollectionAsync(CollectionId collectionId)
    {
        LibraryItemViewModel[] items = SelectedItems.Count > 0
            ? SelectedItems.ToArray()
            : SelectedItem is null
                ? []
                : [SelectedItem];
        return DropItemsOnCollectionAsync(items, collectionId);
    }

    private async Task RemoveSelectedItemsFromCurrentCollectionAsync()
    {
        if (Sidebar.SelectedCollection is not { } collection || SelectedItems.Count == 0)
        {
            return;
        }

        HostServices services = await _main.ServicesAsync();
        ItemId[] itemIds = SelectedItems.Select(item => ItemId.Parse(item.ItemId)).ToArray();
        Result result = await services.Collections.RemoveItemsAsync(collection.CollectionId, itemIds);
        if (!result.IsSuccess)
        {
            _main.ReportError($"从集合移除失败：{result.ErrorMessage}");
            return;
        }

        await RefreshItemsAsync();
    }

    /// <summary>Drops library items onto a collection, adding each item as a member.</summary>
    public async Task DropItemsOnCollectionAsync(IReadOnlyList<LibraryItemViewModel> items,
        CollectionId collectionId)
    {
        if (items.Count == 0)
        {
            return;
        }

        HostServices services = await _main.ServicesAsync();
        ItemId[] itemIds = items.Select(item => ItemId.Parse(item.ItemId)).ToArray();
        Result result = await services.Collections.AddItemsAsync(collectionId, itemIds);
        if (!result.IsSuccess)
        {
            _main.ReportError($"添加到集合失败：{result.ErrorMessage}");
            return;
        }

        await RefreshItemsAsync();
    }

    /// <summary>
    /// Detects duplicate library items and lets the user process or skip each pair.
    /// </summary>
    public async Task DetectDuplicatesAsync()
    {
        HostServices services = await _main.ServicesAsync();
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

    private async Task<bool> ProcessDuplicatePairAsync(HostServices services, DuplicateItemPair pair)
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

        bool hasActiveFilter = Sidebar.HasSelectedCollection || Sidebar.IsNoTagSelected ||
                               Sidebar.GetSelectedTagNames().Count > 0;
        if (Sidebar.SelectedScope == LibrarySidebarScope.Trash || hasActiveFilter)
        {
            // Incremental row lookup returns active items by ID without applying the current
            // sidebar relation filters. Re-run the filtered query so newly imported/scanned
            // items cannot leak into an empty collection or a tag-filtered view.
            await DispatcherTasks.RunAsync(RefreshItemsAsync);
            return;
        }

        HostServices services = await _main.ServicesAsync();
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
        bool selectedItemRemoved = false;
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
                    selectedItemRemoved = true;
                }

                Items.RemoveAt(index);
            }
        }

        if (selectedItemRemoved && SelectedItem is not null)
        {
            // The selected row was deleted or merged away: drop it from the batch selection and
            // clear the inspector instead of reloading it for an id that no longer resolves.
            // The batch-selection derived properties update through the CollectionChanged pipeline.
            SelectedItems.Remove(SelectedItem);
            SelectedItem = null;
        }
        else if (SelectedItem is not null &&
                 removedItemIds is not null &&
                 removedItemIds.Any(id => string.Equals(id.ToString(), SelectedItem.ItemId, StringComparison.Ordinal)))
        {
            _ = Inspector.LoadAsync(ParseItemIdOrNull(SelectedItem.ItemId));
        }

        if (selectedChanged)
        {
            Raise(nameof(InspectorTitle));
            Raise(nameof(InspectorStatus));
            Raise(nameof(InspectorPath));
        }
    }

    /// <summary>
    /// Routes the complete host notification to its affected item rows. Both item and document
    /// changes are projected by stable IDs; no full Library refresh is needed after a commit.
    /// </summary>
    public async Task ApplyChangeSetAsync(LibraryChangeSet changeSet)
    {
        bool hasTagFilter = Sidebar.IsNoTagSelected || Sidebar.GetSelectedTagNames().Count > 0;
        if (!hasTagFilter)
        {
            await ApplyChangeSetAsync(changeSet.ItemIds);
            await ApplyDocumentChangeSetAsync(changeSet.DocumentInstanceIds);
        }

        if (changeSet.ItemIds.Count > 0 || changeSet.DocumentInstanceIds.Count > 0)
        {
            // An active tag filter is reconciled from one refreshed cache snapshot below. Avoid
            // first projecting unfiltered rows by ID, which would expose a transient invalid grid
            // state between the incremental update and the tag-membership pass. The reconcile is
            // latest-wins: it runs on the pipeline registered in the constructor.
            _tagReconcileRequests.OnNext(Unit.Default);
        }

        // Collection counts follow item lifecycle and membership changes; the catalog itself
        // changes when a Collection is created, renamed, or dissolved. Reload it reactively so
        // the sidebar and an active collection filter never observe a stale catalog.
        if (changeSet.CollectionIds.Count > 0)
        {
            _collectionReconcileRequests.OnNext(Unit.Default);
        }
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

        HostServices services = await _main.ServicesAsync();
        Result<IReadOnlyList<ItemId>> itemIds = await Task.Run(() =>
            services.LibraryItems.GetItemIdsByDocumentInstanceIdsAsync(documentInstanceIds));
        if (itemIds.IsSuccess)
        {
            await ApplyChangeSetAsync(itemIds.Value);
        }
    }

    /// <summary>
    /// Reloads the sidebar tag list after a committed changeset and re-evaluates the active tag
    /// filter so the grid follows tag edits published by write services (add/remove/rename/merge).
    /// Pin state and selection survive the reload; a selected tag that no longer exists is dropped
    /// by <see cref="LibrarySidebarViewModel.LoadTagsAsync"/>, and the filter re-run then reflects
    /// the surviving selection. The pipeline discards stale runs when commits arrive faster than
    /// the reload completes by cancelling the superseded run's token.
    /// </summary>
    private async Task ReconcileTagsAfterCommittedChangeAsync(CancellationToken cancellationToken)
    {
        if (Sidebar.SelectedScope == LibrarySidebarScope.Trash)
        {
            // The trash path routes through RefreshItemsAsync, which already reloads tags.
            return;
        }

        HostServices services = await _main.ServicesAsync();
        // The commit is already visible in SQLite; refresh the in-memory snapshot here as well
        // (the revision monitor refreshes it independently) so tag counts and the tag filter
        // query observe this commit regardless of event-handler ordering.
        Result refreshResult = await Task.Run(() => services.LibraryItemCache.RefreshAsync(), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (refreshResult.IsFailure)
        {
            return;
        }

        IReadOnlyList<string> pinnedTags = await Task.Run(() => LoadPinnedTagsAsync(services), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        bool filterWasActive = Sidebar.IsNoTagSelected || Sidebar.GetSelectedTagNames().Count > 0;
        await Sidebar.LoadTagsAsync(services.LibraryItemCache, pinnedTags, cancellationToken);
        Sidebar.ApplyPinnedOrder(pinnedTags);
        cancellationToken.ThrowIfCancellationRequested();

        // Re-run the filter whenever it was or still is active: item tag membership may have
        // changed even though the sidebar selection did not.
        if (filterWasActive || Sidebar.IsNoTagSelected || Sidebar.GetSelectedTagNames().Count > 0)
        {
            await ApplyTagFilterMembershipAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Reloads the sidebar collection catalog after a committed change so names, membership
    /// counts, and the active collection filter follow writes published by any host surface. The
    /// pipeline discards stale runs by cancelling the superseded run's token; the active filter
    /// re-runs only when a collection is selected or the selected collection disappeared.
    /// </summary>
    private async Task ReconcileCollectionsAfterCommittedChangeAsync(CancellationToken cancellationToken)
    {
        if (Sidebar.SelectedScope == LibrarySidebarScope.Trash)
        {
            return;
        }

        HostServices services = await _main.ServicesAsync();
        Result<IReadOnlyList<Collection>> collections =
            await Task.Run(() => services.Collections.ListCollectionsAsync(), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (collections.IsFailure)
        {
            return;
        }

        CollectionId? previousSelection = Sidebar.SelectedCollection?.CollectionId;
        await DispatcherTasks.RunAsync(() =>
        {
            Sidebar.LoadCollections(collections.Value);
            return Task.CompletedTask;
        });
        cancellationToken.ThrowIfCancellationRequested();

        CollectionId? currentSelection = Sidebar.SelectedCollection?.CollectionId;
        if (previousSelection != currentSelection || currentSelection is not null)
        {
            // The filter selection changed or an active collection's membership may have changed.
            await RefreshItemsCoreAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Re-runs the active tag filter against the refreshed cache snapshot and patches the grid
    /// collection in place: rows that stopped matching are removed, rows that started matching
    /// are inserted in created_at order, and the current grid selection is preserved for items
    /// that survived the re-filter (the selected item is cleared when it no longer matches).
    /// </summary>
    private async Task ApplyTagFilterMembershipAsync(CancellationToken cancellationToken)
    {
        bool noTagSelected = Sidebar.IsNoTagSelected;
        IReadOnlyList<string> requiredTags = Sidebar.GetSelectedTagNames();
        HostServices services = await _main.ServicesAsync();
        IReadOnlyList<LibraryItemRow> matching = await Task.Run(() =>
            noTagSelected
                ? services.LibraryItemCache.QueryUntagged()
                : services.LibraryItemCache.QueryByTags(requiredTags), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        await DispatcherTasks.RunAsync(() =>
        {
            Dictionary<string, LibraryItemRow> matchingById = matching.ToDictionary(
                row => row.ItemId.ToString(),
                StringComparer.Ordinal);
            string? primaryItemId = SelectedItem?.ItemId;
            HashSet<string> selectedItemIds = SelectedItems.Select(item => item.ItemId)
                .ToHashSet(StringComparer.Ordinal);

            for (int index = Items.Count - 1; index >= 0; index--)
            {
                if (matchingById.TryGetValue(Items[index].ItemId, out LibraryItemRow? row))
                {
                    Items[index].ApplyRow(row);
                }
                else
                {
                    Items.RemoveAt(index);
                }
            }

            HashSet<string> presentIds = Items.Select(item => item.ItemId)
                .ToHashSet(StringComparer.Ordinal);
            foreach (LibraryItemRow row in matching)
            {
                if (presentIds.Add(row.ItemId.ToString()))
                {
                    InsertItemByCreatedAt(CreateItemViewModel(row));
                }
            }

            SelectedItem = primaryItemId is null
                ? null
                : Items.FirstOrDefault(item => item.ItemId == primaryItemId);
            SetSelectedItems(Items.Where(item => selectedItemIds.Contains(item.ItemId)));
            return Task.CompletedTask;
        });
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
        HostServices services = await _main.ServicesAsync();
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
        Raise(nameof(InspectorStatus));
        try
        {
            string documentEngine = _main.AppOptions.OcrEngines.EngineFor(OcrScope.Document);
            LibraryImportOrchestrator orchestrator = await _main.ImportOrchestratorAsync();
            OcrEnqueueItem[] enqueueItems = items
                .Select(item => new OcrEnqueueItem(item.Title, item.DocumentInstanceId, item.SourcePath))
                .ToArray();
            Result<OcrEnqueueSummary> result =
                await orchestrator.EnqueueOcrForItemsAsync(
                    enqueueItems,
                    documentEngine,
                    priority,
                    MinerUToken,
                    CancellationToken.None);
            if (result.IsFailure)
            {
                if (result.ErrorCode == LibraryImportOrchestrator.MinerUTokenRequiredErrorCode)
                {
                    await _main.OpenSettingsAsync("mineru", "运行 OCR 前需要 MinerU API token。请先在设置中完成配置。");
                    _main.Report("运行 OCR 前需要 MinerU API token。请先在设置中完成配置。");
                    return;
                }

                _main.ReportError(result.ErrorMessage ?? "未知错误");
                return;
            }

            OcrEnqueueSummary summary = result.Value;
            foreach (LibraryItemViewModel item in items)
            {
                OcrEnqueueFailure? failure = summary.Failures.FirstOrDefault(candidate =>
                    string.Equals(candidate.Title, item.Title, StringComparison.Ordinal));
                if (failure is not null)
                {
                    _main.ReportError($"{item.Title} OCR 入队失败：{failure.Message}");
                }
                else if (string.IsNullOrWhiteSpace(item.DocumentInstanceId) ||
                         string.IsNullOrWhiteSpace(item.SourcePath))
                {
                    item.ApplyPrimaryDocumentOcrIndexState(
                        PrimaryDocumentOcrIndexState.Resolve(false, null, null, false, false));
                }
                else
                {
                    item.ApplyPrimaryDocumentOcrIndexState(
                        PrimaryDocumentOcrIndexState.Resolve(true, "running", null, false, false));
                }
            }

            string text = $"{operation}：成功入队 {summary.Succeeded}，失败 {summary.Failed}，无可用文档源 {summary.Skipped}。";
            if (summary.Failed > 0)
            {
                _main.ReportError(text);
            }
            else
            {
                _main.Report(text);
            }

            Raise(nameof(InspectorStatus));
            if (summary.Queue is not null)
            {
                _main.OcrQueue.ObserveQueue(summary.Queue);
            }

            await _main.OcrQueue.RefreshAsync();
        }
        finally
        {
            IsBusy = false;
            Raise(nameof(InspectorStatus));
        }
    }

    private async Task LookupMetadataBatchAsync()
    {
        if (IsMetadataBatchBusy || SelectedItems.Count == 0)
        {
            return;
        }

        ItemId[] itemIds = SelectedItems.Select(item => ItemId.Parse(item.ItemId)).ToArray();
        CancellationTokenSource cancellation = new();
        _metadataBatchCancellation = cancellation;
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
                cancellation.Token);

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
            if (!_cancelMetadataBatchForLibrarySwitch)
            {
                await RefreshItemsOnUiThreadAsync();
                await _main.RefreshOpenItemEditorsAsync(itemIds);
                MetadataBatchStatus = $"批量获取已取消：已处理 {latest.Completed}/{itemIds.Length} 个。";
                _main.Report(MetadataBatchStatus);
            }
        }
        catch (Exception exception)
        {
            MetadataBatchStatus = $"批量获取失败：{exception.Message}";
            _main.ReportError(MetadataBatchStatus);
        }
        finally
        {
            if (ReferenceEquals(_metadataBatchCancellation, cancellation))
            {
                _metadataBatchCancellation = null;
                IsMetadataBatchBusy = false;
                _cancelMetadataBatchForLibrarySwitch = false;
            }

            cancellation.Dispose();
        }
    }

    private void CancelMetadataBatchForLibrarySwitch()
    {
        if (_metadataBatchCancellation is null)
        {
            return;
        }

        // The completion path must not refresh the grid or editors against the next Library.
        _cancelMetadataBatchForLibrarySwitch = true;
        _metadataBatchCancellation.Cancel();
    }

    private Task CancelMetadataBatchAsync()
    {
        _metadataBatchCancellation?.Cancel();
        return Task.CompletedTask;
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

        HostServices services = await _main.ServicesAsync();
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

        HostServices services = await _main.ServicesAsync();
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

        HostServices services = await _main.ServicesAsync();
        ItemId[] itemIds = SelectedItems.Select(item => ItemId.Parse(item.ItemId)).ToArray();
        Result result = await services.Items.RestoreItemsAsync(itemIds);
        if (!result.IsSuccess)
        {
            _main.ReportError($"还原题录失败：{result.ErrorMessage}");
        }
    }

    private static void ScheduleFileAssetGc(HostServices services)
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
        // ShowLibraryList/ShowPdfWorkspace follow IsReadingMode through the derived-property generator.
        IsReadingMode = true;
        await _main.ShowReadingAsync();
    }

    public void ExitReadingMode()
    {
        if (!IsReadingMode)
        {
            return;
        }

        IsReadingMode = false;
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
    public static bool CanLookup(HostServices services, string scheme)
    {
        return services.MetadataLookup.CanLookup(scheme);
    }

    public static async Task<MetadataLookupOutcome> LookupAsync(
        HostServices services,
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
        HostServices services,
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

public sealed partial class LibraryItemViewModel : ViewModelBase
{
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
        Title = title;
        ItemType = itemType;
        Authors = authors;
        Year = year;
        PublicationTitle = publicationTitle;
        Publisher = publisher;
        DocumentInstanceId = documentInstanceId;
        FileAssetId = fileAssetId;
        FileName = fileName;
        SourcePath = sourcePath;
        PageCount = pageCount;
        SearchUnitCount = searchUnitCount;
        IndexStatus = indexStatus;
        CreatedAt = createdAt ?? "";
        _primaryDocumentOcrIndexState = primaryDocumentOcrIndexState ??
                                        PrimaryDocumentOcrIndexState.Resolve(documentInstanceId is not null, null, null,
                                            false, false);
        HasOcrText = hasOcrText;
        OcrStatus = ocrStatus ?? _primaryDocumentOcrIndexState.Detail;
        RunOcrCommand = new AsyncCommand(() => runOcr(this));
        EditMetadataCommand = new AsyncCommand(() => editMetadata(this));
        ViewPdfCommand = new AsyncCommand(() => (viewPdf ?? editMetadata)(this));
    }

    public string ItemId { get; }
    public string CreatedAt { get; }

    [ObservableProperty] public partial string Title { get; set; }

    [ObservableProperty] public partial string ItemType { get; set; }

    [ObservableProperty] public partial string Authors { get; set; }

    [ObservableProperty] public partial string Year { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceText))]
    public partial string PublicationTitle { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceText))]
    public partial string? Publisher { get; set; }

    // The resolver is a method call, so the derived-property generator cannot classify this
    // getter; PublicationTitle/Publisher notify it through NotifyPropertyChangedFor instead.
    [ExcludeFromDerivedGeneration]
    public string SourceText => ItemSourceTextResolver.Resolve(ItemType, PublicationTitle, Publisher);

    [ObservableProperty] public partial string? DocumentInstanceId { get; set; }

    [ObservableProperty] public partial string? FileAssetId { get; set; }

    [ObservableProperty] public partial string FileName { get; set; }

    [ObservableProperty] public partial string SourcePath { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageCountDisplay))]
    public partial int PageCount { get; set; }

    [ObservableProperty] public partial int SearchUnitCount { get; set; }

    [ObservableProperty] public partial string IndexStatus { get; set; }

    // PageCountDisplay calls ToString(), which the derived-property generator classifies as an
    // unsafe method call; PageCount notifies it through NotifyPropertyChangedFor instead.
    [ExcludeFromDerivedGeneration] public string PageCountDisplay => PageCount <= 0 ? "-" : PageCount.ToString();

    public AsyncCommand RunOcrCommand { get; }
    public AsyncCommand EditMetadataCommand { get; }
    public AsyncCommand ViewPdfCommand { get; }

    [ObservableProperty] public partial string OcrStatus { get; set; }

    // These three read the mutable non-readonly backing field directly, so they stay manual and
    // are raised by ApplyPrimaryDocumentOcrIndexState.
    [ExcludeFromDerivedGeneration] public string OcrIndexState => _primaryDocumentOcrIndexState.Value;

    [ExcludeFromDerivedGeneration] public string OcrIndexStateLabel => _primaryDocumentOcrIndexState.ChineseLabel;

    [ExcludeFromDerivedGeneration] public string OcrIndexStateDetail => _primaryDocumentOcrIndexState.Detail;

    [ObservableProperty] public partial bool HasOcrText { get; private set; }

    public void ApplyPrimaryDocumentOcrIndexState(PrimaryDocumentOcrIndexState state)
    {
        _primaryDocumentOcrIndexState = state;
        // OcrStatus notifies through its Toolkit setter; the index-state projections are
        // excluded from derived generation and stay manual.
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
