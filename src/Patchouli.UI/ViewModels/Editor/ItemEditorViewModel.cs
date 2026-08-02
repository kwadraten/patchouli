using System.Collections.ObjectModel;
using System.Text.Json;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.MetadataLookup;
using Patchouli.Core.Csl;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.UI.ViewModels;

namespace Patchouli.UI.ViewModels.Editor;

public sealed record CreatorRoleOption(string Key, string Label);

public sealed class CreatorItemViewModel : ViewModelBase
{
    private string _role = ItemCreatorRoles.Author;
    private string _family = "";
    private string _given = "";
    private string _literal = "";
    private string _suffix = "";
    private string _particles = "";
    private string _name = "";
    private bool _isLiteral;
    private bool _isApplyingName;

    public string Role
    {
        get => _role;
        set
        {
            if (_role == value)
            {
                return;
            }

            _role = value;
            Raise();
            Raise(nameof(SelectedRole));
        }
    }

    public string Literal
    {
        get => _literal;
        set
        {
            if (_literal == value)
            {
                return;
            }

            _literal = value;
            Raise();
            if (_isApplyingName)
            {
                return;
            }

            _isLiteral = !string.IsNullOrWhiteSpace(value);
            _name = _isLiteral ? value : _name;
            if (_isLiteral)
            {
                _family = "";
                _given = "";
                Raise(nameof(Name));
                Raise(nameof(IsLiteral));
                Raise(nameof(IsPersonalName));
                Raise(nameof(Family));
                Raise(nameof(Given));
            }
        }
    }

    public string Family
    {
        get => _family;
        set
        {
            if (_family == value)
            {
                return;
            }

            _family = value;
            Raise();
        }
    }

    public string Given
    {
        get => _given;
        set
        {
            if (_given == value)
            {
                return;
            }

            _given = value;
            Raise();
        }
    }

    public string Suffix
    {
        get => _suffix;
        set
        {
            if (_suffix == value)
            {
                return;
            }

            _suffix = value;
            Raise();
        }
    }

    public string Particles
    {
        get => _particles;
        set
        {
            if (_particles == value)
            {
                return;
            }

            _particles = value;
            Raise();
        }
    }

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value)
            {
                return;
            }

            _name = value;
            Raise();
            ApplyNameParts();
        }
    }

    public bool IsLiteral
    {
        get => _isLiteral;
        set
        {
            if (_isLiteral == value)
            {
                return;
            }

            _isLiteral = value;
            Raise();
            Raise(nameof(IsPersonalName));
            ApplyNameParts();
        }
    }

    public bool IsPersonalName => !_isLiteral;

    private bool _isExpanded;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            Raise();
        }
    }

    private IReadOnlyList<CreatorRoleOption> _availableRoles = DefaultRoleOptions();

    /// <summary>Role choices for the dropdown, driven by the active item-type profile.</summary>
    public IReadOnlyList<CreatorRoleOption> AvailableRoles
    {
        get => _availableRoles;
        set
        {
            _availableRoles = value;
            Raise();
            Raise(nameof(SelectedRole));
        }
    }

    /// <summary>ComboBox selection wrapper around the English <see cref="Role" /> key.</summary>
    public CreatorRoleOption SelectedRole
    {
        get => AvailableRoles.FirstOrDefault(option => option.Key == Role)
               ?? new CreatorRoleOption(Role, ItemCreatorRoles.DisplayLabelFor(Role));
        set
        {
            if (value is not null)
            {
                Role = value.Key;
            }
        }
    }

    public static IReadOnlyList<CreatorRoleOption> DefaultRoleOptions()
    {
        return ItemCreatorRoles.DisplayLabels
            .Select(pair => new CreatorRoleOption(pair.Key, pair.Value))
            .ToArray();
    }

    public AsyncCommand RemoveCommand { get; }
    public RelayCommand ToggleDetailsCommand { get; }

    public CreatorItemViewModel(Action<CreatorItemViewModel> onRemove)
    {
        RemoveCommand = new AsyncCommand(() =>
        {
            onRemove(this);
            return Task.CompletedTask;
        });
        ToggleDetailsCommand = new RelayCommand(_ => IsExpanded = !IsExpanded);
    }

    public void LoadFrom(ItemCreator creator)
    {
        _isApplyingName = true;
        _role = creator.Role;
        _family = creator.Family ?? "";
        _given = creator.Given ?? "";
        _literal = creator.Literal ?? "";
        _suffix = creator.Suffix ?? "";
        _particles = creator.Particles ?? "";
        _isLiteral = !string.IsNullOrWhiteSpace(_literal);
        _name = _isLiteral
            ? _literal
            : FormatPersonalName(_family, _given, _particles, _suffix);
        _isApplyingName = false;

        Raise(nameof(Role));
        Raise(nameof(Name));
        Raise(nameof(IsLiteral));
        Raise(nameof(IsPersonalName));
        Raise(nameof(Family));
        Raise(nameof(Given));
        Raise(nameof(Literal));
        Raise(nameof(Suffix));
        Raise(nameof(Particles));
    }

    private void ApplyNameParts()
    {
        ItemCreatorNameParts parts = ItemCreatorNameParser.Parse(
            _name,
            _isLiteral ? ItemCreatorNameMode.Literal : ItemCreatorNameMode.Personal);
        _isApplyingName = true;
        _family = parts.Family ?? "";
        _given = parts.Given ?? "";
        _literal = parts.Literal ?? "";
        _suffix = parts.Suffix ?? "";
        _particles = parts.Particles ?? "";
        _isApplyingName = false;

        Raise(nameof(Family));
        Raise(nameof(Given));
        Raise(nameof(Literal));
        Raise(nameof(Suffix));
        Raise(nameof(Particles));
    }

    private static string FormatPersonalName(string family, string given, string particles, string suffix)
    {
        return string.Join(" ", new[] { given, particles, family, suffix }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
    }
}

public sealed class LinkedDocumentInstanceItemViewModel : ViewModelBase
{
    public LinkedDocumentInstanceItemViewModel(
        DocumentInstance document,
        string displayName,
        Func<DocumentInstanceId, Task> setPrimary,
        Func<DocumentInstanceId, Task> remove,
        bool isPrimaryStaged = false)
    {
        DocumentInstanceId = document.DocumentInstanceId;
        DisplayName = displayName;
        InstanceType = document.InstanceType;
        IsPrimaryStaged = isPrimaryStaged;
        Status = isPrimaryStaged ? "将设为主要文件（保存题录后生效）" : document.Status;
        IsPrimary = document.IsPrimary;
        SetPrimaryCommand = new AsyncCommand(() => setPrimary(DocumentInstanceId));
        RemoveCommand = new AsyncCommand(() => remove(DocumentInstanceId));
        RemoveLabel = "移除";
    }

    /// <summary>A staged file registration shown in the linked-files card until the item is saved.</summary>
    private LinkedDocumentInstanceItemViewModel(string pendingPath, Func<string, Task> cancelPending)
    {
        DisplayName = Path.GetFileName(pendingPath);
        InstanceType = "";
        Status = "保存题录时注册";
        IsPendingRegistration = true;
        SetPrimaryCommand = new AsyncCommand(() => Task.CompletedTask);
        RemoveCommand = new AsyncCommand(() => cancelPending(pendingPath));
        RemoveLabel = "取消";
    }

    public static LinkedDocumentInstanceItemViewModel PendingRegistration(string path, Func<string, Task> cancelPending)
    {
        return new LinkedDocumentInstanceItemViewModel(path, cancelPending);
    }

    public DocumentInstanceId DocumentInstanceId { get; }
    public string DisplayName { get; }
    public string InstanceType { get; }
    public string Status { get; }
    public bool IsPrimary { get; }
    public bool IsPrimaryStaged { get; }
    public bool IsPendingRegistration { get; }
    public bool CanSetPrimary => !IsPrimary && !IsPrimaryStaged && !IsPendingRegistration;
    public AsyncCommand SetPrimaryCommand { get; }
    public AsyncCommand RemoveCommand { get; }
    public string RemoveLabel { get; }
}

public sealed class IdentifierItemViewModel : ViewModelBase
{
    private readonly string _pendingScheme = "";
    private bool _isBusy;
    private string _status = "";

    public IdentifierItemViewModel(
        ItemIdentifier identifier,
        bool canLookup,
        Func<IdentifierItemViewModel, Task> lookup,
        Func<IdentifierItemViewModel, Task> remove)
    {
        ItemIdentifier = identifier;
        DisplayText = Format(identifier.Scheme, identifier.Value, identifier.Note);
        CanLookup = canLookup;
        LookupCommand = new AsyncCommand(() => lookup(this));
        RemoveCommand = new AsyncCommand(() => remove(this));
    }

    public IdentifierItemViewModel(ItemIdentifierInput identifier)
    {
        _pendingScheme = identifier.Scheme;
        DisplayText = $"{Format(identifier.Scheme, identifier.Value, identifier.Note)}（保存题录时写入）";
        CanLookup = false;
        LookupCommand = new AsyncCommand(() => Task.CompletedTask);
        RemoveCommand = new AsyncCommand(() => Task.CompletedTask);
    }

    public ItemIdentifier? ItemIdentifier { get; }
    public string DisplayText { get; }
    public string Scheme => ItemIdentifier?.Scheme ?? _pendingScheme;
    public bool IsPending => ItemIdentifier is null;
    public bool CanLookup { get; }
    public bool ShowLookup => CanLookup && !_isBusy;
    public bool ShowRemove => ItemIdentifier is not null && !_isBusy;

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy == value)
            {
                return;
            }

            _isBusy = value;
            Raise();
            Raise(nameof(ShowLookup));
            Raise(nameof(ShowRemove));
        }
    }

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;
            Raise();
            Raise(nameof(HasStatus));
        }
    }

    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);
    public AsyncCommand LookupCommand { get; }
    public AsyncCommand RemoveCommand { get; }

    private static string Format(string scheme, string value, string? note)
    {
        return string.IsNullOrWhiteSpace(note)
            ? $"{scheme}: {value}"
            : $"{scheme}: {value} ({note})";
    }
}

public sealed class IdentifierSchemeShortcutViewModel
{
    public IdentifierSchemeShortcutViewModel(string scheme, string displayName, Action<string> select)
    {
        Scheme = scheme;
        DisplayName = displayName;
        SelectCommand = new RelayCommand(_ => select(scheme));
    }

    public string Scheme { get; }
    public string DisplayName { get; }
    public RelayCommand SelectCommand { get; }
}

public sealed record ExtraCslVariableOption(string Key, string Label, bool IsMultiline);

public sealed record ItemTypeOption(string Key, string DisplayName);

public enum ItemEditorSection
{
    BasicInformation,
    ExtendedInformation,
    Identifiers,
    Files
}

/// <summary>Low-frequency standard CSL variables offered by the structured extra-CSL editor. All values
/// are plain text; variables with dedicated storage (URL, call-number, DOI/ISBN/ISSN, title, …) are excluded.</summary>
public static class ExtraCslVariableCatalog
{
    public static readonly IReadOnlyList<ExtraCslVariableOption> Options =
    [
        new("archive", "档案馆", false),
        new("archive_location", "档案位置", false),
        new("archive-place", "档案地点", false),
        new("archive_collection", "档案集合", false),
        new("authority", "发布机构", false),
        new("jurisdiction", "司法辖区", false),
        new("division", "部门/分部", false),
        new("event-title", "会议名称", false),
        new("event-place", "会议地点", false),
        new("medium", "介质", false),
        new("dimensions", "尺寸", false),
        new("scale", "比例尺", false),
        new("license", "许可", false),
        new("section", "章节", false),
        new("number-of-volumes", "卷数", false),
        new("references", "参考文献", true),
        new("reviewed-title", "被评作品标题", false),
        new("reviewed-genre", "被评作品体裁", false)
    ];

    public static ExtraCslVariableOption? Find(string key)
    {
        return Options.FirstOrDefault(option => string.Equals(option.Key, key, StringComparison.Ordinal));
    }
}

public sealed class ExtraCslRowViewModel : ViewModelBase
{
    private string _value = "";
    private bool _isProjection;

    public ExtraCslRowViewModel(string key, string label, bool isMultiline, Action<ExtraCslRowViewModel> remove)
    {
        Key = key;
        Label = label;
        IsMultiline = isMultiline;
        RemoveCommand = new AsyncCommand(() =>
        {
            remove(this);
            return Task.CompletedTask;
        });
    }

    /// <summary>The raw CSL variable name; unknown keys loaded from existing data keep their raw name as label.</summary>
    public string Key { get; }

    public string Label { get; }
    public bool IsMultiline { get; }
    public bool CanRemove => !_isProjection;

    /// <summary>True when the active type projects this row into its basic-information form.</summary>
    public bool IsProjection
    {
        get => _isProjection;
        set
        {
            if (_isProjection == value)
            {
                return;
            }

            _isProjection = value;
            Raise();
            Raise(nameof(CanRemove));
        }
    }

    /// <summary>Invoked after <see cref="Value" /> changes; syncs extra-CSL-backed form fields.</summary>
    public Action<ExtraCslRowViewModel>? ValueChanged { get; set; }

    public string Value
    {
        get => _value;
        set
        {
            if (_value == value)
            {
                return;
            }

            _value = value;
            Raise();
            ValueChanged?.Invoke(this);
        }
    }

    public AsyncCommand RemoveCommand { get; }
}

public sealed class ItemEditorViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private ItemId? _itemId;
    private ItemMetadata? _loadedItem;
    private readonly List<ItemIdentifierInput> _pendingIdentifiers = new();
    private readonly List<IdentifierId> _pendingIdentifierRemovals = new();
    private readonly List<string> _pendingFileRegistrations = new();
    private readonly List<DocumentInstanceId> _pendingDocumentRemovals = new();
    private DocumentInstanceId? _pendingPrimaryDocumentId;
    private readonly Dictionary<string, ItemIdentifierInput?> _projectionStaged = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _fieldValueCache = new(StringComparer.Ordinal);
    private readonly List<CreatorItemViewModel> _creatorCache = new();
    private readonly ObservableCollection<CreatorItemViewModel> _emptyCreators = new();
    private IReadOnlyList<CreatorRoleOption> _creatorRoleOptions = CreatorItemViewModel.DefaultRoleOptions();
    private string _cslPreviewText = "保存题录后可使用默认 CSL 样式预览。";
    private bool _hasCslPreviewWarning;
    private bool _suppressProjectionSync;
    private ExtraCslVariableOption? _selectedExtraCslVariable;
    private NavCategoryViewModel _activeNavSection = null!;
    private bool _availableItemTypesLoaded;

    /// <summary>Test seam over <see cref="MetadataLookupUiBridge" />; production code never overrides it.</summary>
    internal Func<AppServices, ItemId, ItemIdentifier, CancellationToken, Task<MetadataLookupOutcome>> LookupRunner =
        MetadataLookupUiBridge.LookupAsync;

    public ItemEditorViewModel(MainWindowViewModel main)
    {
        _main = main;
        NewCommand = new AsyncCommand(NewAsync);
        SaveCommand = new AsyncCommand(SaveAsync);
        DiscardCommand = new AsyncCommand(DiscardAsync);
        AddCreatorCommand = new AsyncCommand(AddCreatorAsync);
        AddIdentifierCommand = new AsyncCommand(AddIdentifierAsync);
        RegisterFileCommand = new AsyncCommand(RegisterFileAsync);
        ImportBiblatexFromClipboardCommand = new AsyncCommand(ImportBiblatexFromClipboardAsync);
        ImportBiblatexFromFileCommand = new AsyncCommand(ImportBiblatexFromFileAsync);
        AddExtraCslRowCommand = new AsyncCommand(AddExtraCslRow);

        _activeNavSection = NavSections[0];
        RefreshExtraCslVariableChoices();
        BuildFields(null);
    }

    /// <summary>Left-navigation sections; the page keeps a single view model so saving stays atomic.</summary>
    public ObservableCollection<NavCategoryViewModel> NavSections { get; } =
    [
        new("基本信息", "Pencil", ItemEditorSection.BasicInformation),
        new("扩展信息", "List", ItemEditorSection.ExtendedInformation),
        new("唯一标识符", "Database", ItemEditorSection.Identifiers),
        new("文件关联", "FolderOpen", ItemEditorSection.Files)
    ];

    public NavCategoryViewModel ActiveNavSection
    {
        get => _activeNavSection;
        set
        {
            if (ReferenceEquals(_activeNavSection, value))
            {
                return;
            }

            _activeNavSection = value;
            Raise();
            Raise(nameof(IsBasicSectionActive));
            Raise(nameof(IsExtendedSectionActive));
            Raise(nameof(IsIdentifiersSectionActive));
            Raise(nameof(IsFilesSectionActive));
        }
    }

    public bool IsBasicSectionActive => Equals(_activeNavSection.Content, ItemEditorSection.BasicInformation);
    public bool IsExtendedSectionActive => Equals(_activeNavSection.Content, ItemEditorSection.ExtendedInformation);
    public bool IsIdentifiersSectionActive => Equals(_activeNavSection.Content, ItemEditorSection.Identifiers);
    public bool IsFilesSectionActive => Equals(_activeNavSection.Content, ItemEditorSection.Files);

    public string Header => _itemId is null ? "新建题录" : "编辑题录";
    public string ItemIdText => _itemId?.ToString() ?? "";
    public bool HasItem => _itemId is not null;

    private string _itemType = "book";

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
            Raise(nameof(SelectedItemTypeOption));
            Raise(nameof(IsGeneralTypeWarningVisible));
            Raise(nameof(IsExtraCslCardVisible));
            Raise(nameof(IsExtendedSectionEmpty));
            BuildFieldsAsync().Observe(nameof(ItemEditorViewModel), nameof(BuildFieldsAsync));
            UpdateUnsavedCslPreviewState();
        }
    }

    public bool IsGeneralTypeWarningVisible => _itemType == "general";

    /// <summary>The structured extra-CSL editor is only offered for concrete (non-general) types.</summary>
    public bool IsExtraCslCardVisible => _itemType != "general";

    /// <summary>The 扩展信息 section has no content for the general type.</summary>
    public bool IsExtendedSectionEmpty => !HasMoreFields && !IsExtraCslCardVisible;

    public ObservableCollection<ItemTypeOption> AvailableItemTypes { get; } = new();

    /// <summary>ComboBox selection wrapper; <see cref="ItemType" /> stays the English CSL key everywhere else.</summary>
    public ItemTypeOption? SelectedItemTypeOption
    {
        get => AvailableItemTypes.FirstOrDefault(option => option.Key == _itemType);
        set
        {
            if (value is not null)
            {
                ItemType = value.Key;
            }
        }
    }

    public ObservableCollection<ItemFieldDescriptor> Fields { get; } = new();

    /// <summary>Overflow fields rendered inside the collapsed "更多字段" section of the metadata card.</summary>
    public ObservableCollection<ItemFieldDescriptor> MoreFields { get; } = new();

    public bool HasMoreFields => MoreFields.Count > 0;

    public ObservableCollection<ExtraCslRowViewModel> ExtraCslRows { get; } = new();
    public ObservableCollection<ExtraCslVariableOption> AvailableExtraCslVariables { get; } = new();

    public ExtraCslVariableOption? SelectedExtraCslVariable
    {
        get => _selectedExtraCslVariable;
        set
        {
            if (_selectedExtraCslVariable == value)
            {
                return;
            }

            _selectedExtraCslVariable = value;
            Raise();
            Raise(nameof(CanAddExtraCslRow));
        }
    }

    public bool CanAddExtraCslRow => SelectedExtraCslVariable is not null;
    public AsyncCommand AddExtraCslRowCommand { get; }

    public string Title
    {
        get => GetFieldValue("Title");
        set => SetFieldValue("Title", value);
    }

    public string PublicationTitle
    {
        get => GetFieldValue("PublicationTitle");
        set => SetFieldValue("PublicationTitle", value);
    }

    public string IssuedDate
    {
        get => GetFieldValue("IssuedDate");
        set => SetFieldValue("IssuedDate", value);
    }

    public ObservableCollection<CreatorItemViewModel> Creators => GetCreatorField()?.Creators ?? _emptyCreators;

    public string CslPreviewText
    {
        get => _cslPreviewText;
        private set
        {
            if (_cslPreviewText == value)
            {
                return;
            }

            _cslPreviewText = value;
            Raise();
        }
    }

    public bool HasCslPreviewWarning
    {
        get => _hasCslPreviewWarning;
        private set
        {
            if (_hasCslPreviewWarning == value)
            {
                return;
            }

            _hasCslPreviewWarning = value;
            Raise();
        }
    }

    private void BuildFields(CslItemTypeProfile? itemTypeProfile)
    {
        CacheCurrentFields();
        UpdateCreatorRoles(itemTypeProfile);

        Fields.Clear();
        MoreFields.Clear();
        UpdateIdentifierSchemeShortcuts(itemTypeProfile);
        ItemEditorFieldSet profile = CslItemTypeProfileService.GetProfile(itemTypeProfile);

        foreach (ItemFieldDefinition def in profile.VisibleFields)
        {
            Fields.Add(CreateField(def));
        }

        foreach (ItemFieldDefinition def in profile.MoreFields)
        {
            MoreFields.Add(CreateField(def));
        }

        SynchronizeExtraCslProjectionRows();
        SyncProjectionFields();
        Raise(nameof(HasMoreFields));
        Raise(nameof(IsExtendedSectionEmpty));
        RaiseEditorFieldProxies();
    }

    private ItemFieldDescriptor CreateField(ItemFieldDefinition def)
    {
        ItemFieldDescriptor field = new(def.Key, def.Label, def.Type, def.IdentifierScheme, def.ExtraCslVariable);
        if (field.IsIdentifierBacked)
        {
            // The value of an identifier-backed projection field lives in the Identifiers
            // collection, not in the field-value cache; SyncProjectionFields populates it.
            field.ValueChanged = OnProjectionFieldValueChanged;
            field.LookupFromUrlCommand = new AsyncCommand(FetchMetadataFromUrlAsync);
            return field;
        }

        if (field.IsExtraCslBacked)
        {
            // The value of an extra-CSL-backed projection field lives in the ExtraCslRows
            // collection, not in the field-value cache; SyncProjectionFields populates it.
            field.ValueChanged = OnExtraCslFieldValueChanged;
            return field;
        }

        if (_fieldValueCache.TryGetValue(def.Key, out string? val))
        {
            field.Value = val;
        }

        if (def.Type == "CreatorList")
        {
            if (_creatorCache.Count > 0)
            {
                foreach (CreatorItemViewModel c in _creatorCache)
                {
                    field.Creators.Add(c);
                }
            }
            else if (field.Creators.Count == 0)
            {
                field.Creators.Add(CreateCreatorItem());
            }

            field.AddCreatorCommand = new AsyncCommand(() =>
            {
                field.Creators.Add(CreateCreatorItem());
                return Task.CompletedTask;
            });
        }

        return field;
    }

    private async Task BuildFieldsAsync()
    {
        await EnsureAvailableItemTypesAsync();
        Result<CslItemTypeProfile> profileResult =
            await (await _main.ServicesAsync()).ItemTypeProfiles.GetProfileAsync(_itemType);
        BuildFields(profileResult.IsSuccess ? profileResult.Value : null);
    }

    private void UpdateIdentifierSchemeShortcuts(CslItemTypeProfile? profile)
    {
        IdentifierSchemeShortcuts.Clear();
        foreach (string scheme in profile?.IdentifierSchemes
                                      .Where(static scheme => !string.IsNullOrWhiteSpace(scheme))
                                      .Select(static scheme => scheme.Trim().ToLowerInvariant())
                                      .Distinct(StringComparer.Ordinal)
                                  ?? [])
        {
            IdentifierSchemeShortcuts.Add(new IdentifierSchemeShortcutViewModel(
                scheme,
                CslItemTypeProfileService.GetIdentifierSchemeLabel(profile, scheme),
                SelectIdentifierScheme));
        }

        if (IdentifierSchemeShortcuts.Count > 0)
        {
            IdentifierScheme = IdentifierSchemeShortcuts[0].Scheme;
        }

        Raise(nameof(HasIdentifierSchemeShortcuts));
    }

    private void SelectIdentifierScheme(string scheme)
    {
        IdentifierScheme = scheme;
    }

    private void CacheCurrentFields()
    {
        foreach (ItemFieldDescriptor field in Fields.Concat(MoreFields))
        {
            if (field.IsIdentifierBacked || field.IsExtraCslBacked)
            {
                continue;
            }

            _fieldValueCache[field.Key] = field.Value;
            if (field.Type == "CreatorList")
            {
                _creatorCache.Clear();
                _creatorCache.AddRange(field.Creators);
            }
        }
    }

    private string _status = "就绪";

    public string Status
    {
        get => _status;
        private set
        {
            _status = value;
            Raise();
            if (!string.IsNullOrWhiteSpace(value))
            {
                if (value.Contains("失败", StringComparison.Ordinal) || value.Contains("不能", StringComparison.Ordinal) ||
                    value.Contains("无法", StringComparison.Ordinal))
                {
                    _main.ReportError(value);
                }
                else
                {
                    _main.Report(value);
                }
            }
        }
    }

    public ObservableCollection<IdentifierItemViewModel> Identifiers { get; } = new();
    public ObservableCollection<IdentifierSchemeShortcutViewModel> IdentifierSchemeShortcuts { get; } = new();
    public ObservableCollection<LinkedDocumentInstanceItemViewModel> LinkedFiles { get; } = new();
    public bool HasIdentifierSchemeShortcuts => IdentifierSchemeShortcuts.Count > 0;

    public AsyncCommand NewCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand DiscardCommand { get; }
    public AsyncCommand AddCreatorCommand { get; }
    public AsyncCommand AddIdentifierCommand { get; }
    public AsyncCommand RegisterFileCommand { get; }
    public AsyncCommand ImportBiblatexFromClipboardCommand { get; }
    public AsyncCommand ImportBiblatexFromFileCommand { get; }

    public string FilePath { get; set; } = "";

    // Identifier specific bindings
    private string _identifierScheme = BuiltInIdentifierSchemes.DOI;

    public string IdentifierScheme
    {
        get => _identifierScheme;
        set
        {
            if (_identifierScheme == value)
            {
                return;
            }

            _identifierScheme = value;
            Raise();
        }
    }

    private string _identifierValue = "";

    public string IdentifierValue
    {
        get => _identifierValue;
        set
        {
            if (_identifierValue == value)
            {
                return;
            }

            _identifierValue = value;
            Raise();
        }
    }

    private string _identifierNote = "";

    public string IdentifierNote
    {
        get => _identifierNote;
        set
        {
            if (_identifierNote == value)
            {
                return;
            }

            _identifierNote = value;
            Raise();
        }
    }

    public async Task NewAsync()
    {
        await EnsureAvailableItemTypesAsync();
        _itemId = null;
        _loadedItem = null;
        _fieldValueCache.Clear();
        _creatorCache.Clear();
        _projectionStaged.Clear();
        ItemType = "general";

        foreach (ItemFieldDescriptor f in Fields.Concat(MoreFields))
        {
            f.Value = "";
            if (f.Type == "CreatorList")
            {
                f.Creators.Clear();
                f.Creators.Add(CreateCreatorItem());
            }
        }

        Status = "就绪";
        Identifiers.Clear();
        _pendingIdentifiers.Clear();
        _pendingIdentifierRemovals.Clear();
        _pendingFileRegistrations.Clear();
        _pendingDocumentRemovals.Clear();
        _pendingPrimaryDocumentId = null;
        LinkedFiles.Clear();
        ExtraCslRows.Clear();
        RefreshExtraCslVariableChoices();
        UpdateUnsavedCslPreviewState();
        RaiseAll();
    }

    private async Task DiscardAsync()
    {
        if (_itemId is null)
        {
            await NewAsync();
        }
        else
        {
            await LoadAsync(_itemId.Value.ToString());
        }

        Status = "已放弃未保存的更改";
        Raise(nameof(Status));
    }

    public async Task LoadAsync(string itemId)
    {
        await EnsureAvailableItemTypesAsync();
        AppServices services = await _main.ServicesAsync();
        ItemId parsed = ItemId.Parse(itemId);
        Result<ItemMetadata> item = await services.Items.GetItemAsync(parsed);
        if (item.IsFailure)
        {
            Status = item.ErrorMessage ?? "无法加载题录。";
            Raise(nameof(Status));
            _main.Report(Status);
            return;
        }

        _itemId = parsed;
        _loadedItem = item.Value;
        _itemType = item.Value.ItemType;
        Raise(nameof(ItemType));
        Raise(nameof(SelectedItemTypeOption));
        Raise(nameof(IsGeneralTypeWarningVisible));

        _fieldValueCache.Clear();
        _creatorCache.Clear();
        _fieldValueCache["Title"] = item.Value.Title;
        _fieldValueCache["Subtitle"] = item.Value.Subtitle ?? "";
        _fieldValueCache["TitleShort"] = item.Value.TitleShort ?? "";
        _fieldValueCache["IssuedDate"] = FormatDate(item.Value.Dates, ItemDateRoles.Issued, item.Value.Date);
        _fieldValueCache["AccessedDate"] = FormatDate(item.Value.Dates, ItemDateRoles.Accessed, null);
        _fieldValueCache["OriginalDate"] = FormatDate(item.Value.Dates, ItemDateRoles.OriginalDate, null);
        _fieldValueCache["EventDate"] = FormatDate(item.Value.Dates, ItemDateRoles.EventDate, null);
        _fieldValueCache["SubmittedDate"] = FormatDate(item.Value.Dates, ItemDateRoles.Submitted, null);
        _fieldValueCache["PublicationTitle"] = item.Value.PublicationTitle ?? "";
        _fieldValueCache["ContainerTitleShort"] = item.Value.ContainerTitleShort ?? "";
        _fieldValueCache["CollectionTitle"] = item.Value.CollectionTitle ?? "";
        _fieldValueCache["Publisher"] = item.Value.Publisher ?? "";
        _fieldValueCache["Place"] = item.Value.Place ?? "";
        _fieldValueCache["Edition"] = item.Value.Edition ?? "";
        _fieldValueCache["Genre"] = item.Value.Genre ?? "";
        _fieldValueCache["Number"] = item.Value.Number ?? "";
        _fieldValueCache["ChapterNumber"] = item.Value.ChapterNumber ?? "";
        _fieldValueCache["Volume"] = item.Value.Volume ?? "";
        _fieldValueCache["Version"] = item.Value.Version ?? "";
        _fieldValueCache["Issue"] = item.Value.Issue ?? "";
        _fieldValueCache["Pages"] = item.Value.Pages ?? "";
        _fieldValueCache["Language"] = item.Value.Language ?? "";
        _fieldValueCache["Status"] = item.Value.Status ?? "";
        _fieldValueCache["Note"] = item.Value.Note ?? "";
        _fieldValueCache["AbstractText"] = item.Value.Abstract ?? "";
        _fieldValueCache["TagsText"] = FormatTags(item.Value.TagsJson);
        LoadExtraCslRows(item.Value.CustomFieldsJson);
        foreach (ItemCreator creator in item.Value.Creators)
        {
            CreatorItemViewModel editableCreator = CreateCreatorItem();
            editableCreator.LoadFrom(creator);
            _creatorCache.Add(editableCreator);
        }

        Result<CslItemTypeProfile> profileResult = await services.ItemTypeProfiles.GetProfileAsync(_itemType);
        Fields.Clear();
        BuildFields(profileResult.IsSuccess ? profileResult.Value : null);

        Status = $"正在编辑：{item.Value.Title}";
        _pendingIdentifiers.Clear();
        _pendingIdentifierRemovals.Clear();
        _pendingFileRegistrations.Clear();
        _pendingDocumentRemovals.Clear();
        _pendingPrimaryDocumentId = null;
        _projectionStaged.Clear();

        await RefreshIdentifiersAsync();
        await RefreshLinkedFilesAsync();
        await RefreshCslPreviewAsync();
        RaiseAll();
    }

    private async Task EnsureAvailableItemTypesAsync()
    {
        if (_availableItemTypesLoaded)
        {
            return;
        }

        Result<IReadOnlyList<CslItemTypeProfile>> profiles =
            await (await _main.ServicesAsync()).ItemTypeProfiles.ListProfilesAsync();
        if (profiles.IsFailure)
        {
            Status = profiles.ErrorMessage ?? "无法加载文献类型。";
            return;
        }

        AvailableItemTypes.Clear();
        foreach (CslItemTypeProfile profile in profiles.Value
                     .OrderBy(profile => profile.ItemType == "general" ? 0 : 1)
                     .ThenBy(profile => profile.DisplayName, StringComparer.Ordinal))
        {
            AvailableItemTypes.Add(new ItemTypeOption(profile.ItemType, profile.DisplayName));
        }

        _availableItemTypesLoaded = true;
        Raise(nameof(AvailableItemTypes));
        Raise(nameof(SelectedItemTypeOption));
    }

    private string GetFieldValue(string key)
    {
        return Fields.Concat(MoreFields).FirstOrDefault(f => f.Key == key)?.Value ?? "";
    }

    private string GetSavedFieldValue(string key)
    {
        CacheCurrentFields();
        return _fieldValueCache.GetValueOrDefault(key, "");
    }

    private async Task SaveAsync()
    {
        CacheCurrentFields();
        string title = GetSavedFieldValue("Title");
        if (string.IsNullOrWhiteSpace(title))
        {
            Status = "标题不能为空。";
            Raise(nameof(Status));
            _main.Report(Status);
            return;
        }

        AppServices services = await _main.ServicesAsync();

        ItemFieldDescriptor? creatorField = GetCreatorField();
        List<ItemCreatorInput> creators = creatorField?.Creators
            .Select(c => c.IsLiteral
                ? new ItemCreatorInput(c.Role, Literal: NullIfWhiteSpace(c.Name))
                : new ItemCreatorInput(
                    c.Role,
                    NullIfWhiteSpace(c.Family),
                    NullIfWhiteSpace(c.Given),
                    Suffix: NullIfWhiteSpace(c.Suffix),
                    Particles: NullIfWhiteSpace(c.Particles)))
            .Where(c => c.Family is not null || c.Given is not null || c.Literal is not null)
            .ToList() ?? new List<ItemCreatorInput>();

        IReadOnlyList<ItemDateInput> dates = BuildDates();
        if (!TryBuildCustomFieldsJson(out string customFieldsJson))
        {
            return;
        }

        List<string> stagedOperationFailures = [];
        if (_itemId is null)
        {
            Result<ItemMetadata> created = await services.Items.CreateItemAsync(new CreateItemRequest(
                ItemType,
                title,
                GetOptionalFieldValue("Subtitle"),
                GetOptionalFieldValue("TitleShort"),
                PublicationTitle: NullIfWhiteSpace(GetSavedFieldValue("PublicationTitle")),
                ContainerTitleShort: GetOptionalFieldValue("ContainerTitleShort"),
                CollectionTitle: GetOptionalFieldValue("CollectionTitle"),
                Publisher: NullIfWhiteSpace(GetSavedFieldValue("Publisher")),
                Place: NullIfWhiteSpace(GetSavedFieldValue("Place")),
                Edition: GetOptionalFieldValue("Edition"),
                Genre: GetOptionalFieldValue("Genre"),
                Number: GetOptionalFieldValue("Number"),
                ChapterNumber: GetOptionalFieldValue("ChapterNumber"),
                Volume: NullIfWhiteSpace(GetSavedFieldValue("Volume")),
                Version: GetOptionalFieldValue("Version"),
                Issue: NullIfWhiteSpace(GetSavedFieldValue("Issue")),
                Pages: NullIfWhiteSpace(GetSavedFieldValue("Pages")),
                Language: NullIfWhiteSpace(GetSavedFieldValue("Language")),
                Status: GetOptionalFieldValue("Status"),
                Note: GetOptionalFieldValue("Note"),
                AbstractText: NullIfWhiteSpace(GetSavedFieldValue("AbstractText")),
                TagsJson: SerializeTags(),
                CollectionsJson: "[]",
                CustomFieldsJson: customFieldsJson,
                Creators: creators,
                Dates: dates,
                Identifiers: _pendingIdentifiers.ToArray()));

            if (created.IsFailure)
            {
                Status = created.ErrorMessage ?? "题录创建失败。";
                Raise(nameof(Status));
                _main.Report(Status);
                return;
            }

            _itemId = created.Value.ItemId;
            _loadedItem = created.Value;
            _pendingIdentifiers.Clear();
        }
        else
        {
            ItemMetadata loadedItem;
            if (_loadedItem is null)
            {
                Result<ItemMetadata> existing = await services.Items.GetItemAsync(_itemId.Value);
                if (existing.IsFailure)
                {
                    Status = existing.ErrorMessage ?? "无法加载待保存的题录。";
                    return;
                }

                loadedItem = existing.Value;
            }
            else
            {
                loadedItem = _loadedItem;
            }

            Result<ItemMetadata> updated = await services.Items.UpdateItemAsync(
                _itemId.Value,
                new UpdateItemRequest(
                    ItemType,
                    title,
                    GetOptionalFieldValue("Subtitle", loadedItem.Subtitle),
                    GetOptionalFieldValue("TitleShort", loadedItem.TitleShort),
                    PublicationTitle: NullIfWhiteSpace(GetSavedFieldValue("PublicationTitle")),
                    ContainerTitleShort: GetOptionalFieldValue("ContainerTitleShort", loadedItem.ContainerTitleShort),
                    CollectionTitle: GetOptionalFieldValue("CollectionTitle", loadedItem.CollectionTitle),
                    Publisher: NullIfWhiteSpace(GetSavedFieldValue("Publisher")),
                    Place: NullIfWhiteSpace(GetSavedFieldValue("Place")),
                    Edition: GetOptionalFieldValue("Edition", loadedItem.Edition),
                    Genre: GetOptionalFieldValue("Genre", loadedItem.Genre),
                    Number: GetOptionalFieldValue("Number", loadedItem.Number),
                    ChapterNumber: GetOptionalFieldValue("ChapterNumber", loadedItem.ChapterNumber),
                    Volume: NullIfWhiteSpace(GetSavedFieldValue("Volume")),
                    Version: GetOptionalFieldValue("Version", loadedItem.Version),
                    Issue: NullIfWhiteSpace(GetSavedFieldValue("Issue")),
                    Pages: NullIfWhiteSpace(GetSavedFieldValue("Pages")),
                    Language: NullIfWhiteSpace(GetSavedFieldValue("Language")),
                    Status: GetOptionalFieldValue("Status", loadedItem.Status),
                    Note: GetOptionalFieldValue("Note", loadedItem.Note),
                    AbstractText: NullIfWhiteSpace(GetSavedFieldValue("AbstractText")),
                    TagsJson: SerializeTags(),
                    CollectionsJson: loadedItem.CollectionsJson,
                    CustomFieldsJson: customFieldsJson,
                    Creators: creators,
                    Dates: dates,
                    ExpectedUpdatedAt: loadedItem.UpdatedAt));

            if (updated.IsFailure)
            {
                Status = updated.ErrorMessage ?? "题录保存失败。";
                Raise(nameof(Status));
                _main.Report(Status);
                return;
            }

            _loadedItem = updated.Value;
            stagedOperationFailures.AddRange(await ApplyStagedProjectionIdentifiersAsync(services));
        }

        stagedOperationFailures.AddRange(await ApplyStagedIdentifierAndFileOpsAsync(services));

        Status = ItemType == "general"
            ? $"题录已保存：{title.Trim()}。当前为通用文献，生成 CSL 前请先指定具体类型。"
            : $"题录已保存：{title.Trim()}";
        if (stagedOperationFailures.Count > 0)
        {
            Status += $"。部分暂存操作失败：{string.Join("；", stagedOperationFailures)}";
        }

        RaiseAll();
        _main.Report(Status);
        await RefreshIdentifiersAsync();
        await RefreshCslPreviewAsync();
        if (_itemId is not null)
        {
            _main.RefreshItemWorkspaceTabTitles(_itemId.Value.ToString(), title.Trim(), this);
        }
    }

    private Task AddIdentifierAsync()
    {
        if (string.IsNullOrWhiteSpace(IdentifierScheme) || string.IsNullOrWhiteSpace(IdentifierValue))
        {
            Status = "标识符类型和值不能为空。";
            Raise(nameof(Status));
            _main.Report(Status);
            return Task.CompletedTask;
        }

        ItemIdentifierInput input = new(IdentifierScheme.Trim().ToLowerInvariant(), IdentifierValue.Trim(),
            NullIfWhiteSpace(IdentifierNote));
        // Staged for new and loaded items alike; written to the database on save.
        _pendingIdentifiers.Add(input);
        Identifiers.Add(new IdentifierItemViewModel(input));
        Status = "标识符已暂存，保存题录时会一并写入。";
        IdentifierValue = "";
        IdentifierNote = "";
        SyncProjectionFields();
        RaiseAll();
        _main.Report(Status);
        return Task.CompletedTask;
    }

    private async Task ImportBiblatexFromClipboardAsync()
    {
        string? text = await _main.Clipboard.GetTextAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            Status = "剪贴板没有可导入的 BibLaTeX 文本。";
            Raise(nameof(Status));
            _main.Report(Status);
            return;
        }

        await _main.ImportBiblatexTextIntoEditorAsync(text, null, _itemId);
        if (_itemId is not null)
        {
            await LoadAsync(_itemId.Value.ToString());
        }
    }

    private async Task ImportBiblatexFromFileAsync()
    {
        string? path = await _main.FilePicker.OpenFileAsync("导入 BibLaTeX", "BibLaTeX", ["*.bib"]);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await ImportBiblatexFromPathAsync(path);
    }

    public async Task ImportBiblatexFromPathAsync(string path)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path);
        Result utf8 = Infrastructure.Bibliography.Biblatex.BiblatexImportPlanner.ReadUtf8Strict(
            bytes, out string text);
        if (utf8.IsFailure)
        {
            Status = utf8.ErrorMessage ?? "编码错误";
            Raise(nameof(Status));
            _main.Report(Status);
            return;
        }

        string? directory = Path.GetDirectoryName(path);
        await _main.ImportBiblatexTextIntoEditorAsync(text, directory, _itemId);
        if (_itemId is not null)
        {
            await LoadAsync(_itemId.Value.ToString());
        }
    }

    private Task RegisterFileAsync()
    {
        if (string.IsNullOrWhiteSpace(FilePath))
        {
            Status = "请填写关联文件路径。";
            Raise(nameof(Status));
            _main.Report(Status);
            return Task.CompletedTask;
        }

        // Staged until save: the file is registered and attached when the item itself is saved.
        _pendingFileRegistrations.Add(FilePath.Trim());
        LinkedFiles.Add(LinkedDocumentInstanceItemViewModel.PendingRegistration(
            FilePath.Trim(),
            CancelPendingFileRegistrationAsync));
        FilePath = "";
        Raise(nameof(FilePath));
        Status = "关联文件已暂存，保存题录时会一并注册。";
        Raise(nameof(Status));
        _main.Report(Status);
        return Task.CompletedTask;
    }

    private async Task RefreshIdentifiersAsync()
    {
        Identifiers.Clear();
        if (_itemId is not null)
        {
            AppServices services = await _main.ServicesAsync();
            Result<IReadOnlyList<ItemIdentifier>> identifiers =
                await services.Items.ListIdentifiersAsync(_itemId.Value);
            if (identifiers.IsSuccess)
            {
                foreach (ItemIdentifier identifier in identifiers.Value)
                {
                    // A staged projection edit temporarily replaces the persisted row in the UI,
                    // and a staged removal hides it until save (discard restores it).
                    if (_projectionStaged.ContainsKey(identifier.Scheme) ||
                        _pendingIdentifierRemovals.Contains(identifier.IdentifierId))
                    {
                        continue;
                    }

                    Identifiers.Add(new IdentifierItemViewModel(
                        identifier,
                        MetadataLookupUiBridge.CanLookup(services, identifier.Scheme),
                        LookupIdentifierAsync,
                        RemoveIdentifierAsync));
                }
            }
        }

        foreach ((string _, ItemIdentifierInput? staged) in _projectionStaged)
        {
            if (staged is not null)
            {
                Identifiers.Add(new IdentifierItemViewModel(staged));
            }
        }

        foreach (ItemIdentifierInput input in _pendingIdentifiers)
        {
            Identifiers.Add(new IdentifierItemViewModel(input));
        }

        SyncProjectionFields();
    }

    private async Task LookupIdentifierAsync(IdentifierItemViewModel row)
    {
        if (_itemId is null || row.ItemIdentifier is null || row.IsBusy)
        {
            return;
        }

        ItemId targetItemId = _itemId.Value;
        row.IsBusy = true;
        row.Status = "正在获取元数据...";
        try
        {
            MetadataLookupOutcome outcome = await LookupRunner(
                await _main.ServicesAsync(),
                targetItemId,
                row.ItemIdentifier,
                CancellationToken.None);
            if (!outcome.IsSuccess)
            {
                row.Status = outcome.Message;
                Status = $"元数据获取失败：{outcome.Message}";
                return;
            }

            if (_itemId == targetItemId)
            {
                await LoadAsync(targetItemId.ToString());
            }

            Status = "元数据已获取并应用。";
        }
        catch (OperationCanceledException)
        {
            row.Status = "已取消。";
            Status = "元数据获取已取消。";
        }
        catch (Exception exception)
        {
            row.Status = exception.Message;
            Status = $"元数据获取失败：{exception.Message}";
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    /// <summary>URL field "获取元数据": extracts a DOI/arXiv/PMID/ISBN from the URL projection value,
    /// upserts it as an identifier row, then runs the regular identifier metadata lookup. This is an
    /// explicit fetch action, so on a loaded item the identifier is persisted immediately.</summary>
    private async Task FetchMetadataFromUrlAsync()
    {
        string url = CurrentProjectionValue(BuiltInIdentifierSchemes.URL).Trim();
        if (url.Length == 0)
        {
            Status = "请先填写链接 URL。";
            Raise(nameof(Status));
            _main.Report(Status);
            return;
        }

        NormalizedIdentifier? extracted = UrlIdentifierExtractor.Extract(url);
        if (extracted is null)
        {
            Status = "未能从该 URL 识别出 DOI/arXiv 等标识符，请手动在「唯一标识符」中添加。";
            Raise(nameof(Status));
            _main.Report(Status);
            return;
        }

        if (_itemId is null)
        {
            // No persisted item yet: stage the identifier; lookup becomes available after saving.
            _pendingIdentifiers.RemoveAll(input =>
                string.Equals(input.Scheme, extracted.Scheme, StringComparison.OrdinalIgnoreCase));
            for (int i = Identifiers.Count - 1; i >= 0; i--)
            {
                if (Identifiers[i].IsPending &&
                    string.Equals(Identifiers[i].Scheme, extracted.Scheme, StringComparison.OrdinalIgnoreCase))
                {
                    Identifiers.RemoveAt(i);
                }
            }

            ItemIdentifierInput input = new(extracted.Scheme, extracted.Value, null);
            _pendingIdentifiers.Add(input);
            Identifiers.Add(new IdentifierItemViewModel(input));
            SyncProjectionFields();
            RaiseAll();
            Status = $"已从 URL 识别 {extracted.Scheme} 标识符并暂存，保存题录后可获取元数据。";
            Raise(nameof(Status));
            _main.Report(Status);
            return;
        }

        AppServices services = await _main.ServicesAsync();
        Result applied = await ApplyProjectionIdentifierAsync(
            services,
            extracted.Scheme,
            new ItemIdentifierInput(extracted.Scheme, extracted.Value, null));
        if (applied.IsFailure)
        {
            Status = $"标识符写入失败：{applied.ErrorMessage}";
            Raise(nameof(Status));
            _main.Report(Status);
            return;
        }

        await RefreshIdentifiersAsync();

        IdentifierItemViewModel? row = Identifiers.FirstOrDefault(candidate =>
            !candidate.IsPending &&
            string.Equals(candidate.Scheme, extracted.Scheme, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            Status = $"已添加 {extracted.Scheme} 标识符：{extracted.Value}";
            Raise(nameof(Status));
            _main.Report(Status);
            return;
        }

        if (!MetadataLookupUiBridge.CanLookup(services, extracted.Scheme))
        {
            Status = $"已添加 {extracted.Scheme} 标识符：{extracted.Value}（当前无可用元数据来源）。";
            Raise(nameof(Status));
            _main.Report(Status);
            return;
        }

        await LookupIdentifierAsync(row);
    }

    private Task RemoveIdentifierAsync(IdentifierItemViewModel row)
    {
        if (_itemId is null || row.ItemIdentifier is null || row.IsBusy)
        {
            return Task.CompletedTask;
        }

        // Staged until save: the row disappears immediately and the deletion is applied on save;
        // 放弃更改 reloads and restores it.
        _pendingIdentifierRemovals.Add(row.ItemIdentifier.IdentifierId);
        Identifiers.Remove(row);
        SyncProjectionFields();
        Status = "标识符已标记移除，保存题录时生效；放弃更改可恢复。";
        Raise(nameof(Status));
        _main.Report(Status);
        return Task.CompletedTask;
    }

    private async Task RefreshLinkedFilesAsync()
    {
        LinkedFiles.Clear();
        if (_itemId is not null)
        {
            AppServices services = await _main.ServicesAsync();
            Result<IReadOnlyList<DocumentInstance>> documents =
                await services.Documents.ListDocumentInstancesForItemAsync(_itemId.Value);
            if (documents.IsFailure)
            {
                Status = documents.ErrorMessage ?? "无法加载关联文件。";
                return;
            }

            foreach (DocumentInstance document in documents.Value
                         .Where(document => !_pendingDocumentRemovals.Contains(document.DocumentInstanceId))
                         .OrderByDescending(document => document.IsPrimary)
                         .ThenBy(document => document.CreatedAt))
            {
                string displayName = document.Title ?? document.DocumentInstanceId.ToString();
                if (document.FileAssetId is not null)
                {
                    Result<FileAsset> fileAsset = await services.Files.GetFileAssetAsync(document.FileAssetId.Value);
                    if (fileAsset.IsSuccess)
                    {
                        displayName = fileAsset.Value.FileName;
                    }
                }

                bool isPrimaryStaged = _pendingPrimaryDocumentId is { } pending &&
                                       pending == document.DocumentInstanceId;
                LinkedFiles.Add(new LinkedDocumentInstanceItemViewModel(
                    document,
                    displayName,
                    SetPrimaryDocumentAsync,
                    StageDocumentRemovalAsync,
                    isPrimaryStaged));
            }
        }

        foreach (string path in _pendingFileRegistrations)
        {
            LinkedFiles.Add(LinkedDocumentInstanceItemViewModel.PendingRegistration(
                path,
                CancelPendingFileRegistrationAsync));
        }
    }

    private async Task StageDocumentRemovalAsync(DocumentInstanceId documentInstanceId)
    {
        if (_itemId is null)
        {
            return;
        }

        if (!_pendingDocumentRemovals.Contains(documentInstanceId))
        {
            _pendingDocumentRemovals.Add(documentInstanceId);
        }

        if (_pendingPrimaryDocumentId == documentInstanceId)
        {
            _pendingPrimaryDocumentId = null;
        }

        Status = "关联文件已标记移除，保存题录时生效；放弃更改可恢复。";
        Raise(nameof(Status));
        _main.Report(Status);
        await RefreshLinkedFilesAsync();
    }

    private async Task CancelPendingFileRegistrationAsync(string path)
    {
        _pendingFileRegistrations.Remove(path);
        Status = "已取消关联文件注册。";
        Raise(nameof(Status));
        _main.Report(Status);
        await RefreshLinkedFilesAsync();
    }

    private async Task SetPrimaryDocumentAsync(DocumentInstanceId documentInstanceId)
    {
        if (_itemId is null)
        {
            Status = "请先保存题录，再设置主要文件。";
            return;
        }

        // Staged until save; the linked-files card marks the row right away.
        _pendingPrimaryDocumentId = documentInstanceId;
        Status = "主要文件已暂存，保存题录时生效。";
        Raise(nameof(Status));
        _main.Report(Status);
        await RefreshLinkedFilesAsync();
    }

    private static readonly (string FieldKey, string Role)[] DateFieldRoles =
    [
        ("IssuedDate", ItemDateRoles.Issued),
        ("AccessedDate", ItemDateRoles.Accessed),
        ("OriginalDate", ItemDateRoles.OriginalDate),
        ("EventDate", ItemDateRoles.EventDate),
        ("SubmittedDate", ItemDateRoles.Submitted)
    ];

    private IReadOnlyList<ItemDateInput> BuildDates()
    {
        List<ItemDateInput> dates = _loadedItem?.Dates
            .Select(static date => new ItemDateInput(
                date.Role,
                date.DatePartsJson,
                date.Circa,
                date.Season,
                date.Literal))
            .ToList() ?? [];
        foreach ((string fieldKey, string role) in DateFieldRoles)
        {
            // Every date role with a UI field is replaced from the form; roles without one
            // are preserved from the loaded item.
            if (role == ItemDateRoles.Issued || Fields.Concat(MoreFields).Any(field => field.Key == fieldKey))
            {
                ReplaceDate(dates, role, GetSavedFieldValue(fieldKey));
            }
        }

        return dates;
    }

    private static void ReplaceDate(List<ItemDateInput> dates, string role, string value)
    {
        dates.RemoveAll(date => date.Role == role);
        ItemDateInput? replacement = BuildDate(role, value);
        if (replacement is not null)
        {
            dates.Add(replacement);
        }
    }

    private static ItemDateInput? BuildDate(string role, string value)
    {
        string? trimmed = NullIfWhiteSpace(value);
        if (trimmed is null)
        {
            return null;
        }

        return trimmed.StartsWith('[')
            ? new ItemDateInput(role, trimmed)
            : new ItemDateInput(role, Literal: trimmed);
    }

    private string SerializeTags()
    {
        return JsonSerializer.Serialize(SplitNames(GetSavedFieldValue("TagsText")).ToArray());
    }

    private string? GetOptionalFieldValue(string key, string? existingValue = null)
    {
        return Fields.Concat(MoreFields).Any(field => field.Key == key)
            ? NullIfWhiteSpace(GetSavedFieldValue(key))
            : existingValue;
    }

    private bool TryBuildCustomFieldsJson(out string customFieldsJson)
    {
        Dictionary<string, string> fields = new(StringComparer.Ordinal);
        foreach (ExtraCslRowViewModel row in ExtraCslRows)
        {
            string? value = NullIfWhiteSpace(row.Value);
            if (value is null)
            {
                continue;
            }

            if (!fields.TryAdd(row.Key, value))
            {
                Status = $"更多 CSL 字段存在重复的字段名：{row.Key}。";
                customFieldsJson = "{}";
                return false;
            }
        }

        customFieldsJson = JsonSerializer.Serialize(fields);
        return true;
    }

    private static IEnumerable<string> SplitNames(string value)
    {
        return value.Split(new[] { ';', ',', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !string.IsNullOrWhiteSpace(part));
    }

    private static string FormatDate(IEnumerable<ItemDate> dates, string role, string? fallback)
    {
        ItemDate? date = dates.FirstOrDefault(candidate => candidate.Role == role);
        if (!string.IsNullOrWhiteSpace(date?.Literal))
        {
            return date.Literal;
        }

        return date is not null && date.DatePartsJson != "[]"
            ? date.DatePartsJson
            : fallback ?? "";
    }

    private static string FormatTags(string tagsJson)
    {
        try
        {
            return string.Join(", ", JsonSerializer.Deserialize<string[]>(tagsJson) ?? Array.Empty<string>());
        }
        catch
        {
            return "";
        }
    }

    private void LoadExtraCslRows(string customFieldsJson)
    {
        ExtraCslRows.Clear();
        try
        {
            using JsonDocument document = JsonDocument.Parse(customFieldsJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    string value = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString() ?? ""
                        : property.Value.GetRawText();
                    ExtraCslRows.Add(CreateExtraCslRow(
                        property.Name,
                        ExtraCslVariableCatalog.Find(property.Name),
                        value));
                }
            }
        }
        catch (JsonException)
        {
            // Unparseable legacy JSON simply yields no rows; nothing is overwritten until save.
        }

        RefreshExtraCslVariableChoices();
    }

    private Task AddExtraCslRow()
    {
        if (SelectedExtraCslVariable is not { } option)
        {
            return Task.CompletedTask;
        }

        ExtraCslRows.Add(CreateExtraCslRow(option.Key, option));
        SelectedExtraCslVariable = null;
        RefreshExtraCslVariableChoices();
        return Task.CompletedTask;
    }

    private ExtraCslRowViewModel CreateExtraCslRow(string key, ExtraCslVariableOption? option, string value = "")
    {
        ExtraCslRowViewModel row = new(
            key,
            option?.Label ?? key,
            option?.IsMultiline ?? false,
            RemoveExtraCslRow);
        row.ValueChanged = OnExtraCslRowValueChanged;
        row.Value = value;
        return row;
    }

    private void RemoveExtraCslRow(ExtraCslRowViewModel row)
    {
        if (row.IsProjection)
        {
            row.Value = "";
            return;
        }

        ExtraCslRows.Remove(row);
        RefreshExtraCslVariableChoices();
        SyncExtraCslFieldValues(row.Key, "");
    }

    /// <summary>Form field → extra-CSL row: writes into the shared <see cref="ExtraCslRows" />
    /// collection and keeps projected rows as the single editor for their CSL variable.</summary>
    private void OnExtraCslFieldValueChanged(ItemFieldDescriptor field, string value)
    {
        if (_suppressProjectionSync || field.ExtraCslVariableKey is null)
        {
            return;
        }

        string key = field.ExtraCslVariableKey;
        string? trimmed = NullIfWhiteSpace(value);
        ExtraCslRowViewModel? row = ExtraCslRows.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, key, StringComparison.Ordinal));
        if (trimmed is null)
        {
            if (row is not null)
            {
                if (row.IsProjection)
                {
                    row.Value = "";
                }
                else
                {
                    RemoveExtraCslRow(row);
                }
            }

            return;
        }

        if (row is null)
        {
            row = CreateExtraCslRow(key, ExtraCslVariableCatalog.Find(key), trimmed);
            ExtraCslRows.Add(row);
            RefreshExtraCslVariableChoices();
        }
        else
        {
            row.Value = trimmed;
        }
    }

    /// <summary>Extra-CSL row → form field: row edits, additions and removals are mirrored
    /// onto any extra-CSL-backed projection field for the same variable.</summary>
    private void OnExtraCslRowValueChanged(ExtraCslRowViewModel row)
    {
        if (_suppressProjectionSync)
        {
            return;
        }

        SyncExtraCslFieldValues(row.Key, row.Value);
    }

    private void SyncExtraCslFieldValues(string variableKey, string value)
    {
        _suppressProjectionSync = true;
        try
        {
            foreach (ItemFieldDescriptor field in Fields.Concat(MoreFields))
            {
                if (field.IsExtraCslBacked &&
                    string.Equals(field.ExtraCslVariableKey, variableKey, StringComparison.Ordinal))
                {
                    field.Value = value;
                }
            }
        }
        finally
        {
            _suppressProjectionSync = false;
        }
    }

    private void RefreshExtraCslVariableChoices()
    {
        AvailableExtraCslVariables.Clear();
        foreach (ExtraCslVariableOption option in ExtraCslVariableCatalog.Options)
        {
            if (ExtraCslRows.Any(row => string.Equals(row.Key, option.Key, StringComparison.Ordinal)))
            {
                continue;
            }

            AvailableExtraCslVariables.Add(option);
        }
    }

    private void SynchronizeExtraCslProjectionRows()
    {
        HashSet<string> projectionKeys = Fields
            .Where(field => field.IsExtraCslBacked && field.ExtraCslVariableKey is not null)
            .Select(field => field.ExtraCslVariableKey!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (ExtraCslRowViewModel row in ExtraCslRows.ToArray())
        {
            row.IsProjection = projectionKeys.Contains(row.Key);
            if (!row.IsProjection && string.IsNullOrWhiteSpace(row.Value))
            {
                ExtraCslRows.Remove(row);
            }
        }

        foreach (string key in projectionKeys)
        {
            ExtraCslRowViewModel? row = ExtraCslRows.FirstOrDefault(candidate =>
                string.Equals(candidate.Key, key, StringComparison.Ordinal));
            if (row is null)
            {
                row = CreateExtraCslRow(key, ExtraCslVariableCatalog.Find(key));
                ExtraCslRows.Add(row);
            }

            row.IsProjection = true;
        }

        RefreshExtraCslVariableChoices();
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private void OnProjectionFieldValueChanged(ItemFieldDescriptor field, string value)
    {
        if (_suppressProjectionSync || field.IdentifierScheme is null)
        {
            return;
        }

        if (_itemId is null)
        {
            SetProjectionValueForNewItem(field.IdentifierScheme, value);
        }
        else
        {
            SetProjectionValueForLoadedItem(field.IdentifierScheme, value);
        }
    }

    private void SetProjectionValueForNewItem(string scheme, string value)
    {
        _pendingIdentifiers.RemoveAll(input =>
            string.Equals(input.Scheme, scheme, StringComparison.OrdinalIgnoreCase));
        for (int i = Identifiers.Count - 1; i >= 0; i--)
        {
            if (Identifiers[i].IsPending &&
                string.Equals(Identifiers[i].Scheme, scheme, StringComparison.OrdinalIgnoreCase))
            {
                Identifiers.RemoveAt(i);
            }
        }

        string? trimmed = NullIfWhiteSpace(value);
        if (trimmed is not null)
        {
            ItemIdentifierInput input = new(scheme, trimmed, null);
            _pendingIdentifiers.Add(input);
            Identifiers.Add(new IdentifierItemViewModel(input));
        }
    }

    private void SetProjectionValueForLoadedItem(string scheme, string value)
    {
        // For an already-saved item the change is staged and applied to the database on save;
        // the identifiers card reflects the staged value immediately.
        _projectionStaged[scheme] = NullIfWhiteSpace(value) is { } trimmed
            ? new ItemIdentifierInput(scheme, trimmed, null)
            : null;
        RebuildProjectionRows();
    }

    private void RebuildProjectionRows()
    {
        for (int i = Identifiers.Count - 1; i >= 0; i--)
        {
            if (_projectionStaged.ContainsKey(Identifiers[i].Scheme))
            {
                Identifiers.RemoveAt(i);
            }
        }

        foreach ((string _, ItemIdentifierInput? staged) in _projectionStaged)
        {
            if (staged is not null)
            {
                Identifiers.Add(new IdentifierItemViewModel(staged));
            }
        }
    }

    private async Task<IReadOnlyList<string>> ApplyStagedProjectionIdentifiersAsync(AppServices services)
    {
        if (_itemId is null || _projectionStaged.Count == 0)
        {
            return [];
        }

        List<string> failures = [];
        foreach ((string scheme, ItemIdentifierInput? staged) in _projectionStaged.ToArray())
        {
            Result result = await ApplyProjectionIdentifierAsync(services, scheme, staged);
            if (result.IsSuccess)
            {
                _projectionStaged.Remove(scheme);
            }
            else
            {
                failures.Add($"标识符 {scheme}：{result.ErrorMessage}");
            }
        }

        return failures;
    }

    /// <summary>Upserts one scheme's projection: removes persisted rows that differ from the
    /// staged value and adds the staged value when missing.</summary>
    private async Task<Result> ApplyProjectionIdentifierAsync(AppServices services, string scheme,
        ItemIdentifierInput? staged)
    {
        if (_itemId is null)
        {
            return Result.Success();
        }

        Result<IReadOnlyList<ItemIdentifier>> identifiers =
            await services.Items.ListIdentifiersAsync(_itemId.Value);
        if (identifiers.IsFailure)
        {
            return Result.Failure(
                identifiers.ErrorCode ?? AppErrorCodes.DatabaseError,
                identifiers.ErrorMessage ?? "无法读取现有标识符。");
        }

        List<ItemIdentifier> persisted = identifiers.Value
            .Where(identifier => string.Equals(identifier.Scheme, scheme, StringComparison.OrdinalIgnoreCase))
            .ToList();
        bool alreadyStored = staged is not null &&
                             persisted.Any(identifier =>
                                 string.Equals(identifier.Value, staged.Value, StringComparison.Ordinal));
        if (alreadyStored)
        {
            return Result.Success();
        }

        foreach (ItemIdentifier identifier in persisted)
        {
            Result removed = await services.Items.RemoveIdentifierAsync(_itemId.Value, identifier.IdentifierId);
            if (removed.IsFailure)
            {
                return removed;
            }
        }

        if (staged is not null)
        {
            Result<ItemIdentifier> added =
                await services.Items.AddIdentifierAsync(_itemId.Value, staged.Scheme, staged.Value, staged.Note);
            if (added.IsFailure)
            {
                return Result.Failure(
                    added.ErrorCode ?? AppErrorCodes.DatabaseError,
                    added.ErrorMessage ?? "无法写入标识符。");
            }
        }

        return Result.Success();
    }

    /// <summary>Applies the staged identifier removals/additions, file registrations and the staged
    /// primary-document switch, in order, right after the item itself has been saved.</summary>
    private async Task<IReadOnlyList<string>> ApplyStagedIdentifierAndFileOpsAsync(AppServices services)
    {
        if (_itemId is null)
        {
            return [];
        }

        List<string> failures = [];
        foreach (IdentifierId identifierId in _pendingIdentifierRemovals.ToArray())
        {
            Result result = await services.Items.RemoveIdentifierAsync(_itemId.Value, identifierId);
            if (result.IsSuccess)
            {
                _pendingIdentifierRemovals.Remove(identifierId);
            }
            else
            {
                failures.Add($"移除标识符：{result.ErrorMessage}");
            }
        }

        foreach (ItemIdentifierInput input in _pendingIdentifiers.ToArray())
        {
            Result<ItemIdentifier> result =
                await services.Items.AddIdentifierAsync(_itemId.Value, input.Scheme, input.Value, input.Note);
            if (result.IsSuccess)
            {
                _pendingIdentifiers.Remove(input);
            }
            else
            {
                failures.Add($"添加标识符 {input.Scheme}：{result.ErrorMessage}");
            }
        }

        foreach (DocumentInstanceId documentInstanceId in _pendingDocumentRemovals.ToArray())
        {
            Result result =
                await services.Documents.RemoveDocumentInstanceAsync(_itemId.Value, documentInstanceId);
            if (result.IsSuccess)
            {
                _pendingDocumentRemovals.Remove(documentInstanceId);
            }
            else
            {
                failures.Add($"移除关联文件：{result.ErrorMessage}");
            }
        }

        foreach (string path in _pendingFileRegistrations.ToArray())
        {
            Result<FileAsset> asset = await services.Files.RegisterFileAsync(path);
            if (asset.IsFailure)
            {
                failures.Add($"注册关联文件 {Path.GetFileName(path)}：{asset.ErrorMessage ?? path}");
                continue;
            }

            Result<DocumentInstance> document = await services.Documents.AttachDocumentInstanceAsync(
                _itemId.Value,
                asset.Value.FileAssetId,
                DocumentInstanceType.PrimaryScan,
                GetFieldValue("Title"),
                false);
            if (document.IsFailure)
            {
                failures.Add($"关联文件 {Path.GetFileName(path)}：{document.ErrorMessage ?? path}");
                continue;
            }

            _pendingFileRegistrations.Remove(path);
        }

        if (_pendingPrimaryDocumentId is { } primaryDocumentId)
        {
            Result result = await services.Documents.SetPrimaryDocumentInstanceAsync(_itemId.Value, primaryDocumentId);
            if (result.IsSuccess)
            {
                _pendingPrimaryDocumentId = null;
            }
            else
            {
                failures.Add($"设置主要文件：{result.ErrorMessage}");
            }
        }

        await RefreshLinkedFilesAsync();
        return failures;
    }

    private void SyncProjectionFields()
    {
        _suppressProjectionSync = true;
        try
        {
            foreach (ItemFieldDescriptor field in Fields.Concat(MoreFields))
            {
                if (field.IsIdentifierBacked && field.IdentifierScheme is not null)
                {
                    field.Value = CurrentProjectionValue(field.IdentifierScheme);
                    continue;
                }

                if (field.IsExtraCslBacked && field.ExtraCslVariableKey is not null)
                {
                    field.Value = ExtraCslRows.LastOrDefault(row =>
                        string.Equals(row.Key, field.ExtraCslVariableKey, StringComparison.Ordinal))?.Value ?? "";
                }
            }
        }
        finally
        {
            _suppressProjectionSync = false;
        }
    }

    private string CurrentProjectionValue(string scheme)
    {
        if (_projectionStaged.TryGetValue(scheme, out ItemIdentifierInput? staged))
        {
            return staged?.Value ?? "";
        }

        return _pendingIdentifiers.LastOrDefault(input =>
                   string.Equals(input.Scheme, scheme, StringComparison.OrdinalIgnoreCase))?.Value
               ?? Identifiers.LastOrDefault(row =>
                       !row.IsPending && string.Equals(row.Scheme, scheme, StringComparison.OrdinalIgnoreCase))
                   ?.ItemIdentifier?.Value
               ?? "";
    }

    private Task AddCreatorAsync()
    {
        ItemFieldDescriptor? creatorField = GetCreatorField();
        creatorField?.Creators.Add(CreateCreatorItem());
        Raise(nameof(Creators));
        return Task.CompletedTask;
    }

    private CreatorItemViewModel CreateCreatorItem()
    {
        CreatorItemViewModel creator = new(RemoveCreator);
        ApplyRoleOptions(creator);
        return creator;
    }

    private void UpdateCreatorRoles(CslItemTypeProfile? profile)
    {
        _creatorRoleOptions = profile is null
            ? CreatorItemViewModel.DefaultRoleOptions()
            : profile.CreatorRoles
                .Select(role => new CreatorRoleOption(role, ItemCreatorRoles.DisplayLabelFor(role)))
                .ToArray();
        foreach (CreatorItemViewModel creator in _creatorCache)
        {
            ApplyRoleOptions(creator);
        }
    }

    private void ApplyRoleOptions(CreatorItemViewModel creator)
    {
        IReadOnlyList<CreatorRoleOption> options = _creatorRoleOptions;
        if (options.All(option => option.Key != creator.Role))
        {
            // Keep the loaded role selectable even when the current profile does not list it.
            options = options
                .Append(new CreatorRoleOption(creator.Role, ItemCreatorRoles.DisplayLabelFor(creator.Role)))
                .ToArray();
        }

        creator.AvailableRoles = options;
    }

    private void RemoveCreator(CreatorItemViewModel creator)
    {
        GetCreatorField()?.Creators.Remove(creator);
        _creatorCache.Remove(creator);
    }

    private ItemFieldDescriptor? GetCreatorField()
    {
        return Fields.FirstOrDefault(f => f.Type == "CreatorList");
    }

    private void SetFieldValue(string key, string value)
    {
        ItemFieldDescriptor? field = Fields.Concat(MoreFields).FirstOrDefault(f => f.Key == key);
        if (field is null)
        {
            return;
        }

        field.Value = value;
        RaiseEditorFieldProxies();
    }

    private void RaiseEditorFieldProxies()
    {
        Raise(nameof(Title));
        Raise(nameof(PublicationTitle));
        Raise(nameof(IssuedDate));
        Raise(nameof(Creators));
    }

    private void UpdateUnsavedCslPreviewState()
    {
        if (ItemType == "general")
        {
            HasCslPreviewWarning = true;
            CslPreviewText = "当前为通用文献，无法直接生成 CSL 引用。请先指定具体类型。";
            return;
        }

        if (_itemId is null)
        {
            HasCslPreviewWarning = false;
            CslPreviewText = "保存题录后可使用默认 CSL 样式预览。";
        }
    }

    private async Task RefreshCslPreviewAsync()
    {
        if (_itemId is null || ItemType == "general")
        {
            UpdateUnsavedCslPreviewState();
            return;
        }

        Result<CslRenderResult> rendered =
            await (await _main.ServicesAsync()).CslRenderer.RenderAsync(new CslRenderRequest([_itemId.Value]));
        if (rendered.IsFailure)
        {
            HasCslPreviewWarning = true;
            CslPreviewText = rendered.ErrorMessage ?? "无法生成 CSL 预览。";
            return;
        }

        HasCslPreviewWarning = rendered.Value.Warnings.Count > 0;
        CslPreviewText = rendered.Value.RenderedText;
    }

    public Task RefreshStylePreviewAsync()
    {
        return RefreshCslPreviewAsync();
    }

    private void RaiseAll()
    {
        foreach (string property in new[]
                 {
                     nameof(Header),
                     nameof(ItemIdText),
                     nameof(HasItem),
                     nameof(ItemType),
                     nameof(SelectedItemTypeOption),
                     nameof(Status),
                     nameof(IsGeneralTypeWarningVisible),
                     nameof(IsExtraCslCardVisible),
                     nameof(IsExtendedSectionEmpty),
                     nameof(HasMoreFields),
                     nameof(CslPreviewText),
                     nameof(HasCslPreviewWarning),
                     nameof(IdentifierScheme),
                     nameof(HasIdentifierSchemeShortcuts),
                     nameof(IdentifierValue),
                     nameof(IdentifierNote)
                 })
        {
            Raise(property);
        }

        RaiseEditorFieldProxies();
    }
}
