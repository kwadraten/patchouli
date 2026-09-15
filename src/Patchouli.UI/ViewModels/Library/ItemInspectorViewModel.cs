using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
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
public sealed partial class ItemInspectorViewModel : ViewModelBase
{
    private static readonly TimeSpan LoadRequestThrottle = TimeSpan.Zero;
    private readonly Func<Task<IItemService>> _itemServiceFactory;
    private readonly Func<Task<IItemTagService>> _tagServiceFactory;
    private readonly Func<Task<ICslItemTypeProfileService>> _profileServiceFactory;
    private readonly Subject<Unit> _loadRequests = new();
    private ItemId? _currentItemId;
    private ItemId? _latestLoadId;
    private TaskCompletionSource? _latestLoadCompletion;
    private bool _isDisposed;

    public ItemInspectorViewModel(
        Func<Task<IItemService>> itemServiceFactory,
        Func<Task<IItemTagService>> tagServiceFactory,
        Func<Task<ICslItemTypeProfileService>> profileServiceFactory)
    {
        _itemServiceFactory = itemServiceFactory;
        _tagServiceFactory = tagServiceFactory;
        _profileServiceFactory = profileServiceFactory;
        Groups = new ObservableCollection<InspectorGroupViewModel>();
        Tags = new ObservableCollection<InspectorTagViewModel>();
        Sections = new ObservableCollection<object>();
        TagsSection = new InspectorTagsSectionViewModel(this);
        Register(TagsSection);
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
            exception => UnexpectedExceptions.Sink.Report(exception, "item-inspector-load")));
    }

    [ObservableProperty] public partial string Title { get; private set; } = "";

    [ObservableProperty] public partial string Subtitle { get; private set; } = "";

    [ObservableProperty] public partial bool IsEmpty { get; private set; } = true;

    public bool HasContent => !IsEmpty;
    public ObservableCollection<InspectorGroupViewModel> Groups { get; }
    public ObservableCollection<InspectorTagViewModel> Tags { get; }

    /// <summary>Display-ordered inspector cards: 基本信息, the tags section, then the remaining field groups.</summary>
    public ObservableCollection<object> Sections { get; }

    internal InspectorTagsSectionViewModel TagsSection { get; }

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

    /// <summary>
    /// Loads the inspector for the given item. Passing <see langword="null"/> clears the inspector.
    /// Requests are coalesced latest-wins: a newer request supersedes an in-flight load so a slow
    /// fetch that completes after a newer selection cannot repopulate the inspector with stale
    /// content. Superseded requests complete immediately; cancellation never surfaces as an error.
    /// </summary>
    public Task LoadAsync(ItemId? itemId)
    {
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
        return _latestLoadCompletion.Task;
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
            // Release any caller still awaiting an in-flight load.
            _latestLoadCompletion?.TrySetResult();
        }

        base.Dispose(disposing);
    }

    private void Clear()
    {
        Title = "";
        Subtitle = "";
        IsEmpty = true;
        IsTagEditorOpen = false;
        Groups.Clear();
        Tags.Clear();
        Sections.Clear();
        _currentItemId = null;
        Raise(nameof(Tags));
        Raise(nameof(Sections));
    }

    private async Task ProjectAsync(ItemMetadata metadata, CancellationToken cancellationToken)
    {
        Title = metadata.Title;
        Subtitle = metadata.ItemType;
        IsEmpty = false;
        _currentItemId = metadata.ItemId;

        Tags.Clear();
        foreach (string tag in ParseTags(metadata.TagsJson).OrderBy(static t => t, StringComparer.Ordinal))
        {
            Tags.Add(new InspectorTagViewModel(tag, RemoveTagAsync));
        }

        ICslItemTypeProfileService profileService = await Task.Run(_profileServiceFactory, cancellationToken);
        Result<CslItemTypeProfile> profileResult =
            await Task.Run(() => profileService.GetProfileAsync(metadata.ItemType), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyDictionary<string, string> fieldLabels = profileResult.IsSuccess
            ? profileResult.Value.FieldLabels
            : EmptyFieldLabels;

        Groups.Clear();

        InspectorGroupViewModel basic = new("基本信息");
        basic.Fields.Add(Field("条目类型", CslItemTypeDisplayNames.For(metadata.ItemType)));
        AddIfPresent(basic.Fields, "标题", metadata.Title);
        AddIfPresent(basic.Fields, "作者", FormatCreators(metadata.Creators));
        AddIfPresent(basic.Fields, "年份", metadata.Date);
        AddIfPresent(basic.Fields, "语言", metadata.Language);
        AddIfPresent(basic.Fields, "状态", metadata.Status);
        AddIfPresent(basic.Fields, LabelFor(fieldLabels, "container-title", "期刊/出处"), metadata.PublicationTitle);
        AddIfPresent(basic.Fields, LabelFor(fieldLabels, "publisher", "出版社/机构"), metadata.Publisher);
        AddIfPresent(basic.Fields, "会议名", metadata.CollectionTitle);
        AddIfPresent(basic.Fields, "版本", metadata.Edition);
        AddIfPresent(basic.Fields, "卷", metadata.Volume);
        AddIfPresent(basic.Fields, "期", metadata.Issue);
        AddIfPresent(basic.Fields, "页码", metadata.Pages);
        if (metadata.ItemType is not "manuscript" and not "collection")
        {
            AddIfPresent(basic.Fields, "出版地", metadata.Place);
        }

        Dictionary<string, string> customFields = ParseCustomFields(metadata.CustomFieldsJson);
        if (customFields.TryGetValue("archive", out string? archive))
        {
            AddIfPresent(basic.Fields, LabelFor(fieldLabels, "archive", "档案馆"), archive);
        }

        if (customFields.TryGetValue("archive_location", out string? archiveLoc))
        {
            AddIfPresent(basic.Fields, LabelFor(fieldLabels, "archive_location", "馆藏位置"), archiveLoc);
        }

        if (customFields.TryGetValue("archive-place", out string? archivePlace))
        {
            AddIfPresent(basic.Fields, LabelFor(fieldLabels, "archive-place", "档案所在地"), archivePlace);
        }

        if (customFields.TryGetValue("archive_collection", out string? archiveCol))
        {
            AddIfPresent(basic.Fields, LabelFor(fieldLabels, "archive_collection", "档案集合"), archiveCol);
        }

        if (customFields.TryGetValue("event-title", out string? eventTitle))
        {
            AddIfPresent(basic.Fields, LabelFor(fieldLabels, "event-title", "会议名称"), eventTitle);
        }

        if (customFields.TryGetValue("event-place", out string? eventPlace))
        {
            AddIfPresent(basic.Fields, LabelFor(fieldLabels, "event-place", "会议地点"), eventPlace);
        }

        if (basic.Fields.Count > 0)
        {
            Groups.Add(basic);
        }

        if (metadata.Identifiers.Count > 0)
        {
            InspectorGroupViewModel identifiers = new("标识符");
            foreach (ItemIdentifier identifier in metadata.Identifiers.OrderBy(static i => i.Scheme,
                         StringComparer.Ordinal))
            {
                string schemeLabel;
                if (string.Equals(identifier.Scheme, BuiltInIdentifierSchemes.CallNumber,
                        StringComparison.OrdinalIgnoreCase))
                {
                    schemeLabel = fieldLabels.TryGetValue("call-number", out string? customCallNum)
                        ? customCallNum
                        : metadata.ItemType is "manuscript" or "collection"
                            ? "档案号"
                            : "索书号";
                }
                else if (string.Equals(identifier.Scheme, BuiltInIdentifierSchemes.ArXiv,
                             StringComparison.OrdinalIgnoreCase))
                {
                    schemeLabel = "arXiv";
                }
                else
                {
                    schemeLabel = identifier.Scheme.ToUpperInvariant();
                }

                identifiers.Fields.Add(Field(schemeLabel, identifier.Value));
            }

            Groups.Add(identifiers);
        }

        InspectorGroupViewModel extended = new("扩展信息");
        HashSet<string> basicCustomKeys = new(StringComparer.Ordinal)
        {
            "archive", "archive_location", "archive-place", "archive_collection", "event-title", "event-place"
        };
        foreach ((string key, string value) in customFields.OrderBy(static kvp => kvp.Key, StringComparer.Ordinal))
        {
            if (basicCustomKeys.Contains(key))
            {
                continue;
            }

            ExtraCslVariableOption? option = ExtraCslVariableCatalog.Find(key);
            string label = fieldLabels.TryGetValue(key, out string? customLabel)
                ? customLabel
                : option?.Label ?? (key == "original_biblatex_entry_type" ? "原始 BibLaTeX 类型" : key);
            bool wrap = option?.IsMultiline ?? false;
            AddIfPresent(extended.Fields, label, value, wrap);
        }

        if (extended.Fields.Count > 0)
        {
            Groups.Add(extended);
        }

        InspectorGroupViewModel other = new("其他");
        AddIfPresent(other.Fields, "副标题", metadata.Subtitle);
        AddIfPresent(other.Fields, "短标题", metadata.TitleShort);
        AddIfPresent(other.Fields, "体裁", metadata.Genre);
        AddIfPresent(other.Fields, "编号", metadata.Number);
        AddIfPresent(other.Fields, "章节号", metadata.ChapterNumber);
        AddIfPresent(other.Fields, "版本号", metadata.Version);
        AddIfPresent(other.Fields, "引文键", metadata.CitationKey);
        AddIfPresent(other.Fields, "备注", metadata.Note, true);
        AddIfPresent(other.Fields, "摘要", metadata.Abstract, true);
        if (other.Fields.Count > 0)
        {
            Groups.Add(other);
        }

        Raise(nameof(Groups));

        // Card order in the view: 基本信息 first, then the tags section, then the remaining groups.
        Sections.Clear();
        if (Groups.Count > 0)
        {
            Sections.Add(Groups[0]);
        }

        Sections.Add(TagsSection);
        foreach (InspectorGroupViewModel group in Groups.Skip(1))
        {
            Sections.Add(group);
        }

        Raise(nameof(Sections));
    }

    private static string LabelFor(IReadOnlyDictionary<string, string> fieldLabels, string fieldKey, string fallback)
    {
        return fieldLabels.TryGetValue(fieldKey, out string? label) ? label : fallback;
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyFieldLabels =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static void AddIfPresent(Collection<InspectorFieldViewModel> fields, string label, string? value,
        bool wrap = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        fields.Add(new InspectorFieldViewModel(label, value.Trim(), wrap));
    }

    private static InspectorFieldViewModel Field(string label, string value, bool wrap = false)
    {
        return new InspectorFieldViewModel(label, value, wrap);
    }

    private static string FormatCreators(IReadOnlyList<ItemCreator> creators)
    {
        if (creators.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(", ", creators
            .OrderBy(static creator => creator.SequenceIndex)
            .Select(static creator => creator.DisplayName)
            .Where(static name => !string.IsNullOrWhiteSpace(name)));
    }

    private static IReadOnlyList<string> ParseTags(string tagsJson)
    {
        if (string.IsNullOrWhiteSpace(tagsJson))
        {
            return Array.Empty<string>();
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(tagsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            return document.RootElement.EnumerateArray()
                .Select(element => element.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static Dictionary<string, string> ParseCustomFields(string? json)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
                {
                    string val = prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString() ?? ""
                        : prop.Value.ToString();
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        result[prop.Name] = val.Trim();
                    }
                }
            }
        }
        catch (JsonException)
        {
            return result;
        }

        return result;
    }
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
