using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.UI.ViewModels.Core;
using Patchouli.UI.ViewModels.Editor;

namespace Patchouli.UI.ViewModels;

/// <summary>
/// A read-only projection of <see cref="ItemMetadata"/> into collapsible inspector groups.
/// The view model formats fields for display and exposes lightweight tag editing that routes
/// writes through <see cref="IItemTagService"/>.
/// </summary>
public sealed partial class ItemInspectorViewModel : ViewModelBase, IAsyncDisposable
{
    private static readonly TimeSpan LoadRequestThrottle = TimeSpan.Zero;
    private readonly Func<Task<IItemService>> _itemServiceFactory;
    private readonly Func<Task<IItemTagService>> _tagServiceFactory;
    private readonly Func<Task<ICslItemTypeProfileService>> _profileServiceFactory;
    private readonly Func<Task<ICollectionService>> _collectionServiceFactory;
    private readonly Subject<Unit> _loadRequests = new();
    private readonly AsyncOperationLifetime _loadLifetime = new();
    private ItemId? _currentItemId;
    private ItemId? _latestLoadId;
    private TaskCompletionSource? _latestLoadCompletion;
    private bool _isDisposed;

    public ItemInspectorViewModel(
        Func<Task<IItemService>> itemServiceFactory,
        Func<Task<IItemTagService>> tagServiceFactory,
        Func<Task<ICslItemTypeProfileService>> profileServiceFactory,
        Func<Task<ICollectionService>> collectionServiceFactory)
    {
        _itemServiceFactory = itemServiceFactory;
        _tagServiceFactory = tagServiceFactory;
        _profileServiceFactory = profileServiceFactory;
        _collectionServiceFactory = collectionServiceFactory;
        Groups = new ObservableCollection<InspectorGroupViewModel>();
        Tags = new ObservableCollection<InspectorTagViewModel>();
        Collections = new ObservableCollection<InspectorCollectionViewModel>();
        Sections = new ObservableCollection<object>();
        TagsSection = new InspectorTagsSectionViewModel(this);
        CollectionsSection = new InspectorCollectionsSectionViewModel(this);
        Register(TagsSection);
        Register(CollectionsSection);
        AddTagCommand = new AsyncCommand(AddTagAsync);
        ToggleTagEditorCommand = new RelayCommand(_ => IsTagEditorOpen = !IsTagEditorOpen);
        IScheduler uiScheduler = SynchronizationContext.Current is { } synchronizationContext
            ? new SynchronizationContextScheduler(synchronizationContext)
            : CurrentThreadScheduler.Instance;
        Register(ReactiveUiFlow.SubscribeLatest(
            _loadRequests,
            LoadRequestThrottle,
            TaskPoolScheduler.Default,
            uiScheduler,
            LoadLatestAsync,
            exception => UnexpectedExceptions.Sink.Report(exception, "item-inspector-load"), _loadLifetime));
    }

    [ObservableProperty] public partial string Title { get; private set; } = "";

    [ObservableProperty] public partial string Subtitle { get; private set; } = "";

    [ObservableProperty] public partial bool IsEmpty { get; private set; } = true;

    public bool HasContent => !IsEmpty;
    public ObservableCollection<InspectorGroupViewModel> Groups { get; }
    public ObservableCollection<InspectorTagViewModel> Tags { get; }

    /// <summary>The collections holding the currently loaded item, ordered by collection name.</summary>
    public ObservableCollection<InspectorCollectionViewModel> Collections { get; }

    /// <summary>Display-ordered inspector cards: 基本信息, the tags section, the collections section,
    /// then the remaining field groups.</summary>
    public ObservableCollection<object> Sections { get; }

    internal InspectorTagsSectionViewModel TagsSection { get; }

    internal InspectorCollectionsSectionViewModel CollectionsSection { get; }

    [ObservableProperty] public partial string NewTagName { get; set; } = "";

    [ObservableProperty] public partial bool IsTagEditorOpen { get; private set; }

    public AsyncCommand AddTagCommand { get; }
    public RelayCommand ToggleTagEditorCommand { get; }

    private async Task AddTagAsync()
    {
        if (_currentItemId is null)
        {
            return;
        }

        string? normalized = TagNormalizer.Normalize(NewTagName);
        if (normalized is null)
        {
            return;
        }

        IItemTagService tagService = await _tagServiceFactory();
        Result result = await tagService.AddTagsToItemsAsync([_currentItemId.Value], [normalized]);
        if (result.IsSuccess)
        {
            NewTagName = "";
            await LoadAsync(_currentItemId.Value);
        }
    }

    private async Task RemoveTagAsync(InspectorTagViewModel tag)
    {
        if (_currentItemId is null)
        {
            return;
        }

        IItemTagService tagService = await _tagServiceFactory();
        Result result = await tagService.RemoveTagFromItemsAsync([_currentItemId.Value], tag.Name);
        if (result.IsSuccess)
        {
            await LoadAsync(_currentItemId.Value);
        }
    }

    private async Task LoadCollectionsAsync(ItemId itemId, CancellationToken cancellationToken)
    {
        Collections.Clear();
        ICollectionService collectionService = await Task.Run(_collectionServiceFactory, cancellationToken);
        Result<IReadOnlyList<CollectionId>> membership =
            await Task.Run(() => collectionService.GetItemCollectionIdsAsync(itemId, cancellationToken),
                cancellationToken);
        if (membership.IsFailure || membership.Value.Count == 0)
        {
            return;
        }

        Result<IReadOnlyList<Collection>> catalog =
            await Task.Run(() => collectionService.ListCollectionsAsync(cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (catalog.IsFailure)
        {
            return;
        }

        HashSet<CollectionId> memberOf = membership.Value.ToHashSet();
        foreach (Collection collection in catalog.Value.Where(c => memberOf.Contains(c.CollectionId)))
        {
            Collections.Add(new InspectorCollectionViewModel(collection.Name));
        }
    }

    /// <summary>
    /// Loads the inspector for the given item. Passing <see langword="null"/> clears the inspector.
    /// Requests are coalesced latest-wins: a newer request supersedes an in-flight load so a slow
    /// fetch that completes after a newer selection cannot repopulate the inspector with stale
    /// content. Superseded requests complete immediately; cancellation never surfaces as an error.
    /// </summary>
    public Task LoadAsync(ItemId? itemId)
    {
        return LoadAsync(itemId, CancellationToken.None);
    }

    public Task LoadAsync(ItemId? itemId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_isDisposed)
        {
            return Task.CompletedTask;
        }

        // Completing the previous completion hands its caller a finished task while only the
        // newest request drives the actual projection.
        _latestLoadCompletion?.TrySetResult();
        _latestLoadCompletion =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _latestLoadId = itemId;
        _loadRequests.OnNext(Unit.Default);
        // This is a publication/coalescing signal, not the database task. Cancelling
        // a caller's wait is safe: DisposeAsync still drains the actual load lifetime.
        return _latestLoadCompletion.Task.WaitAsync(cancellationToken);
    }

    private async Task LoadLatestAsync(CancellationToken cancellationToken)
    {
        if (_latestLoadCompletion is not { } completion)
        {
            return;
        }

        try
        {
            await LoadCoreAsync(_latestLoadId, cancellationToken);
        }
        finally
        {
            completion.TrySetResult();
        }
    }

    private async Task LoadCoreAsync(ItemId? itemId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (itemId is null)
        {
            Clear();
            return;
        }

        try
        {
            // Microsoft.Data.Sqlite executes synchronously under the async facade; keep the
            // reads off the UI thread so selection changes do not stall the library page.
            IItemService itemService = await Task.Run(_itemServiceFactory, cancellationToken);
            Result<ItemMetadata> result =
                await Task.Run(() => itemService.GetItemAsync(itemId.Value), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (result.IsFailure || result.Value is null)
            {
                Clear();
                return;
            }

            await ProjectAsync(result.Value, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer load superseded this one; leave the current state unchanged.
        }
    }

    protected override void Dispose(bool disposing)
    {
        _isDisposed = true;
        if (disposing)
        {
            _loadLifetime.Dispose();
            // Release any caller still awaiting an in-flight load.
            _latestLoadCompletion?.TrySetResult();
        }

        base.Dispose(disposing);
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await _loadLifetime.DisposeAsync();
    }

    private void Clear()
    {
        Title = "";
        Subtitle = "";
        IsEmpty = true;
        IsTagEditorOpen = false;
        Groups.Clear();
        Tags.Clear();
        Collections.Clear();
        Sections.Clear();
        _currentItemId = null;
        Raise(nameof(Tags));
        Raise(nameof(Collections));
        Raise(nameof(Sections));
    }

    private async Task ProjectAsync(ItemMetadata metadata, CancellationToken cancellationToken)
    {
        Title = metadata.Title;
        Subtitle = metadata.ItemType;
        IsEmpty = false;
        _currentItemId = metadata.ItemId;

        await LoadCollectionsAsync(metadata.ItemId, cancellationToken);

        ICslItemTypeProfileService profileService = await Task.Run(_profileServiceFactory, cancellationToken);
        Result<CslItemTypeProfile> profileResult =
            await Task.Run(() => profileService.GetProfileAsync(metadata.ItemType), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyDictionary<string, string> fieldLabels = profileResult.IsSuccess
            ? profileResult.Value.FieldLabels
            : EmptyFieldLabels;

        ItemInspectorProjectionModel projection = ItemInspectorProjection.Project(metadata, fieldLabels);

        Tags.Clear();
        foreach (string tag in projection.Tags)
        {
            Tags.Add(new InspectorTagViewModel(tag, RemoveTagAsync));
        }

        Groups.Clear();
        foreach (InspectorGroupProjection group in projection.Groups)
        {
            InspectorGroupViewModel groupViewModel = new(group.Title);
            foreach (InspectorFieldProjection field in group.Fields)
            {
                groupViewModel.Fields.Add(new InspectorFieldViewModel(field.Label, field.Value, field.WrapText));
            }

            Groups.Add(groupViewModel);
        }

        Raise(nameof(Groups));

        // Card order in the view: 基本信息 first, then the tags and collections sections, then
        // the remaining groups.
        Sections.Clear();
        if (Groups.Count > 0)
        {
            Sections.Add(Groups[0]);
        }

        Sections.Add(TagsSection);
        Sections.Add(CollectionsSection);
        foreach (InspectorGroupViewModel group in Groups.Skip(1))
        {
            Sections.Add(group);
        }

        Raise(nameof(Sections));
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyFieldLabels =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// The tags card in the inspector's section flow; exposes the parent's tag state to its data template
/// and forwards the parent's change notifications for the bound properties.
/// </summary>
public sealed class InspectorTagsSectionViewModel : ViewModelBase
{
    private readonly ItemInspectorViewModel _parent;

    public InspectorTagsSectionViewModel(ItemInspectorViewModel parent)
    {
        _parent = parent;
        // Forwarding subscriptions are owned by this section and cleaned up when it is disposed.
        IObservable<EventPattern<PropertyChangedEventArgs>> parentPropertyChanged = Observable
            .FromEventPattern<PropertyChangedEventHandler, PropertyChangedEventArgs>(
                handler => _parent.PropertyChanged += handler,
                handler => _parent.PropertyChanged -= handler);
        Register(parentPropertyChanged
            .Where(pattern => pattern.EventArgs.PropertyName is nameof(ItemInspectorViewModel.IsTagEditorOpen)
                or nameof(ItemInspectorViewModel.NewTagName))
            .Subscribe(pattern => Raise(pattern.EventArgs.PropertyName)));
        Register(Observable
            .FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                handler => _parent.Tags.CollectionChanged += handler,
                handler => _parent.Tags.CollectionChanged -= handler)
            .Subscribe(_ => Raise(nameof(HasNoTags))));
    }

    public string Title => "标签";
    public ObservableCollection<InspectorTagViewModel> Tags => _parent.Tags;
    public bool HasNoTags => Tags.Count == 0;

    public string NewTagName
    {
        get => _parent.NewTagName;
        set => _parent.NewTagName = value;
    }

    public bool IsTagEditorOpen => _parent.IsTagEditorOpen;
    public AsyncCommand AddTagCommand => _parent.AddTagCommand;
    public RelayCommand ToggleTagEditorCommand => _parent.ToggleTagEditorCommand;
}

/// <summary>
/// The collections card in the inspector's section flow; exposes the parent's collection state to
/// its data template and forwards the parent's change notifications for the bound properties.
/// </summary>
public sealed class InspectorCollectionsSectionViewModel : ViewModelBase
{
    private readonly ItemInspectorViewModel _parent;

    public InspectorCollectionsSectionViewModel(ItemInspectorViewModel parent)
    {
        _parent = parent;
        // Forwarding subscription is owned by this section and cleaned up when it is disposed.
        Register(Observable
            .FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                handler => _parent.Collections.CollectionChanged += handler,
                handler => _parent.Collections.CollectionChanged -= handler)
            .Subscribe(_ => Raise(nameof(HasNoCollections))));
    }

    public string Title => "集合";
    public ObservableCollection<InspectorCollectionViewModel> Collections => _parent.Collections;
    public bool HasNoCollections => Collections.Count == 0;
}

/// <summary>
/// A single editable tag chip shown in the item inspector.
/// </summary>
public sealed class InspectorTagViewModel : ViewModelBase
{
    public InspectorTagViewModel(string name, Func<InspectorTagViewModel, Task> remove)
    {
        Name = name;
        RemoveCommand = new AsyncCommand(() => remove(this));
    }

    public string Name { get; }
    public AsyncCommand RemoveCommand { get; }
}

/// <summary>
/// A single read-only collection chip shown in the item inspector.
/// </summary>
public sealed class InspectorCollectionViewModel : ViewModelBase
{
    public InspectorCollectionViewModel(string name)
    {
        Name = name;
    }

    public string Name { get; }
}

/// <summary>
/// A collapsible group of inspector fields.
/// </summary>
public sealed class InspectorGroupViewModel : ViewModelBase
{
    public InspectorGroupViewModel(string title)
    {
        Title = title;
        Fields = new ObservableCollection<InspectorFieldViewModel>();
    }

    public string Title { get; }
    public ObservableCollection<InspectorFieldViewModel> Fields { get; }
}

/// <summary>
/// A single label/value pair in the item inspector.
/// </summary>
public sealed class InspectorFieldViewModel : ViewModelBase
{
    public InspectorFieldViewModel(string label, string value, bool wrapText)
    {
        Label = label;
        Value = value;
        WrapText = wrapText;
    }

    public string Label { get; }
    public string Value { get; }
    public bool WrapText { get; }
}
