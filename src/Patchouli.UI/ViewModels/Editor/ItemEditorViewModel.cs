using System.Collections.ObjectModel;
using System.Text.Json;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Csl;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.UI.ViewModels;

namespace Patchouli.UI.ViewModels.Editor;

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

    public IReadOnlyList<string> AvailableRoles { get; } = new[]
    {
        ItemCreatorRoles.Author,
        ItemCreatorRoles.Editor,
        ItemCreatorRoles.Translator,
        ItemCreatorRoles.ContainerAuthor
    };

    public AsyncCommand RemoveCommand { get; }

    public CreatorItemViewModel(Action<CreatorItemViewModel> onRemove)
    {
        RemoveCommand = new AsyncCommand(() =>
        {
            onRemove(this);
            return Task.CompletedTask;
        });
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
        Func<DocumentInstanceId, Task> setPrimary)
    {
        DocumentInstanceId = document.DocumentInstanceId;
        DisplayName = displayName;
        InstanceType = document.InstanceType;
        Status = document.Status;
        IsPrimary = document.IsPrimary;
        SetPrimaryCommand = new AsyncCommand(() => setPrimary(DocumentInstanceId));
    }

    public DocumentInstanceId DocumentInstanceId { get; }
    public string DisplayName { get; }
    public string InstanceType { get; }
    public string Status { get; }
    public bool IsPrimary { get; }
    public bool CanSetPrimary => !IsPrimary;
    public AsyncCommand SetPrimaryCommand { get; }
}

public sealed class IdentifierItemViewModel : ViewModelBase
{
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
        DisplayText = $"{Format(identifier.Scheme, identifier.Value, identifier.Note)}（保存题录时写入）";
        CanLookup = false;
        LookupCommand = new AsyncCommand(() => Task.CompletedTask);
        RemoveCommand = new AsyncCommand(() => Task.CompletedTask);
    }

    public ItemIdentifier? ItemIdentifier { get; }
    public string DisplayText { get; }
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

public sealed class ItemEditorViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private ItemId? _itemId;
    private readonly List<ItemIdentifierInput> _pendingIdentifiers = new();
    private readonly Dictionary<string, string> _fieldValueCache = new(StringComparer.Ordinal);
    private readonly List<CreatorItemViewModel> _creatorCache = new();
    private readonly ObservableCollection<CreatorItemViewModel> _emptyCreators = new();
    private string _cslPreviewText = "保存题录后可使用默认 CSL 样式预览。";
    private bool _hasCslPreviewWarning;

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

        BuildFields(null);
    }

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
            Raise(nameof(IsGeneralTypeWarningVisible));
            BuildFieldsAsync().Observe(nameof(ItemEditorViewModel), nameof(BuildFieldsAsync));
            UpdateUnsavedCslPreviewState();
        }
    }

    public bool IsGeneralTypeWarningVisible => _itemType == "general";

    public IReadOnlyList<string> AvailableItemTypes { get; } = new[]
    {
        "general", "book", "article-journal", "chapter", "thesis", "report", "webpage",
        "manuscript", "paper-conference", "patent", "standard"
    };

    public ObservableCollection<ItemFieldDescriptor> Fields { get; } = new();

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

        Fields.Clear();
        IReadOnlyList<ItemFieldDefinition> profile = CslItemTypeProfileService.GetProfile(itemTypeProfile);

        foreach (ItemFieldDefinition def in profile)
        {
            ItemFieldDescriptor field = new(def.Key, def.Label, def.Type);
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

            Fields.Add(field);
        }

        RaiseEditorFieldProxies();
    }

    private async Task BuildFieldsAsync()
    {
        Result<CslItemTypeProfile> profileResult =
            await (await _main.ServicesAsync()).ItemTypeProfiles.GetProfileAsync(_itemType);
        BuildFields(profileResult.IsSuccess ? profileResult.Value : null);
    }

    private void CacheCurrentFields()
    {
        foreach (ItemFieldDescriptor field in Fields)
        {
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
    public ObservableCollection<LinkedDocumentInstanceItemViewModel> LinkedFiles { get; } = new();

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

    public Task NewAsync()
    {
        _itemId = null;
        _fieldValueCache.Clear();
        _creatorCache.Clear();
        ItemType = "general";

        foreach (ItemFieldDescriptor f in Fields)
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
        LinkedFiles.Clear();
        UpdateUnsavedCslPreviewState();
        RaiseAll();
        return Task.CompletedTask;
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
        _itemType = item.Value.ItemType;
        Raise(nameof(ItemType));
        Raise(nameof(IsGeneralTypeWarningVisible));

        _fieldValueCache.Clear();
        _creatorCache.Clear();
        _fieldValueCache["Title"] = item.Value.Title;
        _fieldValueCache["Subtitle"] = item.Value.Subtitle ?? "";
        _fieldValueCache["IssuedDate"] = FormatDate(item.Value.Dates, ItemDateRoles.Issued, item.Value.Date);
        _fieldValueCache["PublicationTitle"] = item.Value.PublicationTitle ?? "";
        _fieldValueCache["Publisher"] = item.Value.Publisher ?? "";
        _fieldValueCache["Place"] = item.Value.Place ?? "";
        _fieldValueCache["Volume"] = item.Value.Volume ?? "";
        _fieldValueCache["Issue"] = item.Value.Issue ?? "";
        _fieldValueCache["Pages"] = item.Value.Pages ?? "";
        _fieldValueCache["Language"] = item.Value.Language ?? "";
        _fieldValueCache["AbstractText"] = item.Value.Abstract ?? "";
        _fieldValueCache["TagsText"] = FormatTags(item.Value.TagsJson);
        foreach (ItemCreator creator in item.Value.Creators)
        {
            CreatorItemViewModel editableCreator = CreateCreatorItem();
            editableCreator.LoadFrom(creator);
            _creatorCache.Add(editableCreator);
        }

        Fields.Clear();
        Result<CslItemTypeProfile> profileResult = await services.ItemTypeProfiles.GetProfileAsync(_itemType);
        IReadOnlyList<ItemFieldDefinition> profile =
            CslItemTypeProfileService.GetProfile(profileResult.IsSuccess ? profileResult.Value : null);

        foreach (ItemFieldDefinition def in profile)
        {
            ItemFieldDescriptor field = new(def.Key, def.Label, def.Type);

            if (def.Type == "CreatorList")
            {
                field.AddCreatorCommand = new AsyncCommand(() =>
                {
                    field.Creators.Add(CreateCreatorItem());
                    return Task.CompletedTask;
                });

                foreach (CreatorItemViewModel creator in _creatorCache)
                {
                    field.Creators.Add(creator);
                }
            }
            else
            {
                field.Value = _fieldValueCache.GetValueOrDefault(def.Key, "");
            }

            Fields.Add(field);
        }

        Status = $"正在编辑：{item.Value.Title}";
        _pendingIdentifiers.Clear();

        await RefreshIdentifiersAsync();
        await RefreshLinkedFilesAsync();
        await RefreshCslPreviewAsync();
        RaiseAll();
    }

    private string GetFieldValue(string key)
    {
        return Fields.FirstOrDefault(f => f.Key == key)?.Value ?? "";
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

        if (_itemId is null)
        {
            Result<ItemMetadata> created = await services.Items.CreateItemAsync(new CreateItemRequest(
                ItemType,
                title,
                NullIfWhiteSpace(GetSavedFieldValue("Subtitle")),
                PublicationTitle: NullIfWhiteSpace(GetSavedFieldValue("PublicationTitle")),
                Publisher: NullIfWhiteSpace(GetSavedFieldValue("Publisher")),
                Place: NullIfWhiteSpace(GetSavedFieldValue("Place")),
                Volume: NullIfWhiteSpace(GetSavedFieldValue("Volume")),
                Issue: NullIfWhiteSpace(GetSavedFieldValue("Issue")),
                Pages: NullIfWhiteSpace(GetSavedFieldValue("Pages")),
                Language: NullIfWhiteSpace(GetSavedFieldValue("Language")),
                AbstractText: NullIfWhiteSpace(GetSavedFieldValue("AbstractText")),
                TagsJson: SerializeTags(),
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
            _pendingIdentifiers.Clear();
        }
        else
        {
            Result<ItemMetadata> updated = await services.Items.UpdateItemAsync(
                _itemId.Value,
                new UpdateItemRequest(
                    ItemType,
                    title,
                    NullIfWhiteSpace(GetSavedFieldValue("Subtitle")),
                    PublicationTitle: NullIfWhiteSpace(GetSavedFieldValue("PublicationTitle")),
                    Publisher: NullIfWhiteSpace(GetSavedFieldValue("Publisher")),
                    Place: NullIfWhiteSpace(GetSavedFieldValue("Place")),
                    Volume: NullIfWhiteSpace(GetSavedFieldValue("Volume")),
                    Issue: NullIfWhiteSpace(GetSavedFieldValue("Issue")),
                    Pages: NullIfWhiteSpace(GetSavedFieldValue("Pages")),
                    Language: NullIfWhiteSpace(GetSavedFieldValue("Language")),
                    AbstractText: NullIfWhiteSpace(GetSavedFieldValue("AbstractText")),
                    TagsJson: SerializeTags(),
                    Creators: creators,
                    Dates: dates));

            if (updated.IsFailure)
            {
                Status = updated.ErrorMessage ?? "题录保存失败。";
                Raise(nameof(Status));
                _main.Report(Status);
                return;
            }
        }

        Status = ItemType == "general"
            ? $"题录已保存：{title.Trim()}。当前为通用文献，生成 CSL 前请先指定具体类型。"
            : $"题录已保存：{title.Trim()}";
        RaiseAll();
        _main.Report(Status);
        await RefreshIdentifiersAsync();
        await RefreshCslPreviewAsync();
        await _main.Shell.RefreshItemsAsync();
        if (_itemId is not null)
        {
            _main.RefreshItemWorkspaceTabTitles(_itemId.Value.ToString(), title.Trim(), this);
        }
    }

    private async Task AddIdentifierAsync()
    {
        if (string.IsNullOrWhiteSpace(IdentifierScheme) || string.IsNullOrWhiteSpace(IdentifierValue))
        {
            Status = "标识符类型和值不能为空。";
            Raise(nameof(Status));
            _main.Report(Status);
            return;
        }

        ItemIdentifierInput input = new(IdentifierScheme.Trim().ToLowerInvariant(), IdentifierValue.Trim(),
            NullIfWhiteSpace(IdentifierNote));
        if (_itemId is null)
        {
            _pendingIdentifiers.Add(input);
            Identifiers.Add(new IdentifierItemViewModel(input));
            Status = "标识符已暂存，保存题录时会一并写入。";
            IdentifierValue = "";
            IdentifierNote = "";
            RaiseAll();
            _main.Report(Status);
            return;
        }

        Result<ItemIdentifier> result = await (await _main.ServicesAsync()).Items.AddIdentifierAsync(
            _itemId.Value,
            input.Scheme,
            input.Value,
            input.Note);

        Status = result.IsSuccess ? "标识符已添加。" : result.ErrorMessage ?? "标识符添加失败。";
        IdentifierValue = "";
        IdentifierNote = "";
        await RefreshIdentifiersAsync();
        RaiseAll();
        _main.Report(Status);
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

    private async Task RegisterFileAsync()
    {
        if (_itemId is null)
        {
            Status = "请先保存题录，再注册关联文件。";
            Raise(nameof(Status));
            _main.Report(Status);
            return;
        }

        if (string.IsNullOrWhiteSpace(FilePath))
        {
            Status = "请填写关联文件路径。";
            Raise(nameof(Status));
            _main.Report(Status);
            return;
        }

        AppServices services = await _main.ServicesAsync();
        Result<FileAsset> asset = await services.Files.RegisterFileAsync(FilePath);
        if (asset.IsFailure)
        {
            Status = asset.ErrorMessage ?? "关联文件注册失败。";
            Raise(nameof(Status));
            _main.Report(Status);
            return;
        }

        Result<DocumentInstance> document = await services.Documents.AttachDocumentInstanceAsync(
            _itemId.Value,
            asset.Value.FileAssetId,
            DocumentInstanceType.PrimaryScan,
            GetFieldValue("Title"),
            false);

        Status = document.IsSuccess
            ? document.Value.IsPrimary ? "关联文件已注册并设为主要文件。" : "关联文件已注册。"
            : document.ErrorMessage ?? "关联文件挂载失败。";
        await RefreshLinkedFilesAsync();
        RaiseAll();
        _main.Report(Status);
        await _main.Shell.RefreshItemsAsync();
    }

    private async Task RefreshIdentifiersAsync()
    {
        Identifiers.Clear();
        if (_itemId is null)
        {
            foreach (ItemIdentifierInput identifier in _pendingIdentifiers)
            {
                Identifiers.Add(new IdentifierItemViewModel(identifier));
            }

            return;
        }

        AppServices services = await _main.ServicesAsync();
        Result<IReadOnlyList<ItemIdentifier>> identifiers = await services.Items.ListIdentifiersAsync(_itemId.Value);
        if (identifiers.IsSuccess)
        {
            foreach (ItemIdentifier identifier in identifiers.Value)
            {
                Identifiers.Add(new IdentifierItemViewModel(
                    identifier,
                    MetadataLookupUiBridge.CanLookup(services, identifier.Scheme),
                    LookupIdentifierAsync,
                    RemoveIdentifierAsync));
            }
        }
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
            MetadataLookupOutcome outcome = await MetadataLookupUiBridge.LookupAsync(
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

            await _main.Shell.RefreshItemsAsync();
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

    private async Task RemoveIdentifierAsync(IdentifierItemViewModel row)
    {
        if (_itemId is null || row.ItemIdentifier is null || row.IsBusy)
        {
            return;
        }

        row.IsBusy = true;
        row.Status = "正在移除...";
        try
        {
            Result result = await (await _main.ServicesAsync()).Items.RemoveIdentifierAsync(
                _itemId.Value,
                row.ItemIdentifier.IdentifierId);
            if (result.IsFailure)
            {
                row.Status = result.ErrorMessage ?? "移除标识符失败。";
                Status = $"移除标识符失败：{row.Status}";
                return;
            }

            await RefreshIdentifiersAsync();
            await RefreshCslPreviewAsync();
            Status = "标识符已移除。";
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    private async Task RefreshLinkedFilesAsync()
    {
        LinkedFiles.Clear();
        if (_itemId is null)
        {
            return;
        }

        AppServices services = await _main.ServicesAsync();
        Result<IReadOnlyList<DocumentInstance>> documents =
            await services.Documents.ListDocumentInstancesForItemAsync(_itemId.Value);
        if (documents.IsFailure)
        {
            Status = documents.ErrorMessage ?? "无法加载关联文件。";
            return;
        }

        foreach (DocumentInstance document in documents.Value.OrderByDescending(document => document.IsPrimary)
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

            LinkedFiles.Add(new LinkedDocumentInstanceItemViewModel(document, displayName, SetPrimaryDocumentAsync));
        }
    }

    private async Task SetPrimaryDocumentAsync(DocumentInstanceId documentInstanceId)
    {
        if (_itemId is null)
        {
            Status = "请先保存题录，再设置主要文件。";
            return;
        }

        Result result = await (await _main.ServicesAsync()).Documents.SetPrimaryDocumentInstanceAsync(
            _itemId.Value,
            documentInstanceId);
        Status = result.IsSuccess ? "主要文件已切换。" : result.ErrorMessage ?? "主要文件切换失败。";
        if (!result.IsSuccess)
        {
            return;
        }

        await RefreshLinkedFilesAsync();
        await _main.Shell.RefreshItemsAsync();
    }

    private IReadOnlyList<ItemDateInput> BuildDates()
    {
        return new[]
        {
            BuildDate(ItemDateRoles.Issued, GetSavedFieldValue("IssuedDate"))
        }.Where(date => date is not null).Cast<ItemDateInput>().ToArray();
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

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
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
        return new CreatorItemViewModel(RemoveCreator);
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
        ItemFieldDescriptor? field = Fields.FirstOrDefault(f => f.Key == key);
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

    private void RaiseAll()
    {
        foreach (string property in new[]
                 {
                     nameof(Header),
                     nameof(ItemIdText),
                     nameof(HasItem),
                     nameof(ItemType),
                     nameof(Status),
                     nameof(IsGeneralTypeWarningVisible),
                     nameof(CslPreviewText),
                     nameof(HasCslPreviewWarning),
                     nameof(IdentifierScheme),
                     nameof(IdentifierValue),
                     nameof(IdentifierNote)
                 })
        {
            Raise(property);
        }

        RaiseEditorFieldProxies();
    }
}
