using System.Collections.ObjectModel;
using System.Text.Json;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.UI.ViewModels.Core;

namespace Patchouli.UI.ViewModels;

/// <summary>
/// A read-only projection of <see cref="ItemMetadata"/> into collapsible inspector groups.
/// The view model formats fields for display and exposes lightweight tag editing that routes
/// writes through <see cref="IItemTagService"/>.
/// </summary>
public sealed class ItemInspectorViewModel : ViewModelBase
{
    private readonly Func<Task<IItemService>> _itemServiceFactory;
    private readonly Func<Task<IItemTagService>> _tagServiceFactory;
    private readonly Func<Task<ICslItemTypeProfileService>> _profileServiceFactory;
    private ItemId? _currentItemId;

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
        AddTagCommand = new AsyncCommand(AddTagAsync);
    }

    public string Title { get; private set; } = "";
    public string Subtitle { get; private set; } = "";
    public bool IsEmpty { get; private set; } = true;
    public bool HasContent => !IsEmpty;
    public ObservableCollection<InspectorGroupViewModel> Groups { get; }
    public ObservableCollection<InspectorTagViewModel> Tags { get; }
    public string NewTagName { get; set; } = "";
    public AsyncCommand AddTagCommand { get; }

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
            Raise(nameof(NewTagName));
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
    /// Cancellation is ignored so that transient selection changes do not surface errors.
    /// </summary>
    public async Task LoadAsync(ItemId? itemId)
    {
        if (itemId is null)
        {
            Clear();
            return;
        }

        try
        {
            IItemService itemService = await _itemServiceFactory();
            Result<ItemMetadata> result = await itemService.GetItemAsync(itemId.Value);
            if (result.IsFailure || result.Value is null)
            {
                Clear();
                return;
            }

            await ProjectAsync(result.Value);
        }
        catch (OperationCanceledException)
        {
            // Selection changed while loading; leave the current state unchanged.
        }
    }

    private void Clear()
    {
        Title = "";
        Subtitle = "";
        IsEmpty = true;
        Groups.Clear();
        Tags.Clear();
        _currentItemId = null;
        Raise(nameof(Title));
        Raise(nameof(Subtitle));
        Raise(nameof(IsEmpty));
        Raise(nameof(HasContent));
        Raise(nameof(Tags));
    }

    private async Task ProjectAsync(ItemMetadata metadata)
    {
        Title = metadata.Title;
        Subtitle = metadata.ItemType;
        IsEmpty = false;
        _currentItemId = metadata.ItemId;
        Raise(nameof(HasContent));

        Tags.Clear();
        foreach (string tag in ParseTags(metadata.TagsJson).OrderBy(static t => t, StringComparer.Ordinal))
        {
            Tags.Add(new InspectorTagViewModel(tag, RemoveTagAsync));
        }

        ICslItemTypeProfileService profileService = await _profileServiceFactory();
        Result<CslItemTypeProfile> profileResult =
            await profileService.GetProfileAsync(metadata.ItemType);
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
        if (basic.Fields.Count > 0)
        {
            Groups.Add(basic);
        }

        InspectorGroupViewModel provenance = new("出处与出版");
        AddIfPresent(provenance.Fields, LabelFor(fieldLabels, "container-title", "期刊/出处"), metadata.PublicationTitle);
        AddIfPresent(provenance.Fields, LabelFor(fieldLabels, "publisher", "出版社/机构"), metadata.Publisher);
        AddIfPresent(provenance.Fields, "会议名", metadata.CollectionTitle);
        AddIfPresent(provenance.Fields, "版本", metadata.Edition);
        AddIfPresent(provenance.Fields, "卷", metadata.Volume);
        AddIfPresent(provenance.Fields, "期", metadata.Issue);
        AddIfPresent(provenance.Fields, "页码", metadata.Pages);
        AddIfPresent(provenance.Fields, "出版地", metadata.Place);
        if (provenance.Fields.Count > 0)
        {
            Groups.Add(provenance);
        }

        if (metadata.Identifiers.Count > 0)
        {
            InspectorGroupViewModel identifiers = new("标识符");
            foreach (ItemIdentifier identifier in metadata.Identifiers.OrderBy(static i => i.Scheme,
                         StringComparer.Ordinal))
            {
                identifiers.Fields.Add(Field(identifier.Scheme.ToUpperInvariant(), identifier.Value));
            }

            Groups.Add(identifiers);
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

        Raise(nameof(Title));
        Raise(nameof(Subtitle));
        Raise(nameof(IsEmpty));
        Raise(nameof(Groups));
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
