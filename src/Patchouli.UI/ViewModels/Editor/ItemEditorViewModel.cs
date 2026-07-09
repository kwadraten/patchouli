using System.Collections.ObjectModel;
using System.Text.Json;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Csl;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.UI.ViewModels;

namespace Patchouli.UI.ViewModels.Editor;

public sealed class CreatorItemViewModel : ViewModelBase
{
    private string _role = ItemCreatorRoles.Author;
    private string _family = "";
    private string _given = "";
    private string _literal = "";

    public string Role
    {
        get => _role;
        set
        {
            if (_role == value) return;
            _role = value;
            Raise();
        }
    }

    public string Literal
    {
        get => _literal;
        set
        {
            if (_literal == value) return;
            _literal = value;
            Raise();
        }
    }

    public string Family
    {
        get => _family;
        set
        {
            if (_family == value) return;
            _family = value;
            Raise();
        }
    }

    public string Given
    {
        get => _given;
        set
        {
            if (_given == value) return;
            _given = value;
            Raise();
        }
    }

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
            if (_itemType == value) return;
            _itemType = value; 
            Raise(); 
            Raise(nameof(IsGeneralTypeWarningVisible));
            _ = BuildFieldsAsync();
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
            if (_cslPreviewText == value) return;
            _cslPreviewText = value;
            Raise();
        }
    }

    public bool HasCslPreviewWarning
    {
        get => _hasCslPreviewWarning;
        private set
        {
            if (_hasCslPreviewWarning == value) return;
            _hasCslPreviewWarning = value;
            Raise();
        }
    }

    private void BuildFields(Patchouli.Core.Bibliography.CslItemTypeProfile? itemTypeProfile)
    {
        CacheCurrentFields();

        Fields.Clear();
        var profile = CslItemTypeProfileService.GetProfile(itemTypeProfile);

        foreach (var def in profile)
        {
            var field = new ItemFieldDescriptor(def.Key, def.Label, def.Type);
            if (_fieldValueCache.TryGetValue(def.Key, out var val))
            {
                field.Value = val;
            }
            if (def.Type == "CreatorList")
            {
                if (_creatorCache.Count > 0)
                {
                    foreach (var c in _creatorCache) field.Creators.Add(c);
                }
                else if (field.Creators.Count == 0)
                {
                    field.Creators.Add(new CreatorItemViewModel(c => field.Creators.Remove(c)));
                }
                field.AddCreatorCommand = new AsyncCommand(() => 
                {
                    field.Creators.Add(new CreatorItemViewModel(c => field.Creators.Remove(c)));
                    return Task.CompletedTask;
                });
            }
            Fields.Add(field);
        }

        RaiseEditorFieldProxies();
    }

    private async Task BuildFieldsAsync()
    {
        var profileResult = await (await _main.ServicesAsync()).ItemTypeProfiles.GetProfileAsync(_itemType);
        BuildFields(profileResult.IsSuccess ? profileResult.Value : null);
    }

    private void CacheCurrentFields()
    {
        foreach (var field in Fields)
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
                if (value.Contains("失败", StringComparison.Ordinal) || value.Contains("不能", StringComparison.Ordinal) || value.Contains("无法", StringComparison.Ordinal)) _main.ReportError(value);
                else _main.Report(value);
            }
        }
    }
    
    public ObservableCollection<string> Identifiers { get; } = new();
    public ObservableCollection<string> LinkedFiles { get; } = new();
    
    public AsyncCommand NewCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand DiscardCommand { get; }
    public AsyncCommand AddCreatorCommand { get; }
    public AsyncCommand AddIdentifierCommand { get; }
    public AsyncCommand RegisterFileCommand { get; }

    public string FilePath { get; set; } = "";

    // Identifier specific bindings
    private string _identifierScheme = "DOI";
    public string IdentifierScheme
    {
        get => _identifierScheme;
        set
        {
            if (_identifierScheme == value) return;
            _identifierScheme = value;
            Raise();
        }
    }
    public string IdentifierValue { get; set; } = "";
    public string IdentifierNote { get; set; } = "";

    public IReadOnlyList<string> AvailableIdentifierSchemes { get; } = new[]
    {
        "DOI", "ISBN", "URL", "ISSN", "PMID", "PMC", "arXiv"
    };

    public Task NewAsync()
    {
        _itemId = null;
        _fieldValueCache.Clear();
        _creatorCache.Clear();
        ItemType = "general";
        
        foreach (var f in Fields)
        {
            f.Value = "";
            if (f.Type == "CreatorList")
            {
                f.Creators.Clear();
                f.Creators.Add(new CreatorItemViewModel(c => f.Creators.Remove(c)));
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
        var services = await _main.ServicesAsync();
        var parsed = ItemId.Parse(itemId);
        var item = await services.Items.GetItemAsync(parsed);
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
        foreach (var creator in item.Value.Creators)
        {
            _creatorCache.Add(new CreatorItemViewModel(c => _creatorCache.Remove(c))
            {
                Role = creator.Role,
                Family = creator.Family ?? "",
                Given = creator.Given ?? "",
                Literal = creator.Literal ?? ""
            });
        }

        Fields.Clear();
        var profileResult = await services.ItemTypeProfiles.GetProfileAsync(_itemType);
        var profile = CslItemTypeProfileService.GetProfile(profileResult.IsSuccess ? profileResult.Value : null);

        foreach (var def in profile)
        {
            var field = new ItemFieldDescriptor(def.Key, def.Label, def.Type);
            
            if (def.Type == "CreatorList")
            {
                field.AddCreatorCommand = new AsyncCommand(() => 
                {
                    field.Creators.Add(new CreatorItemViewModel(c => field.Creators.Remove(c)));
                    return Task.CompletedTask;
                });
                
                foreach (var creator in _creatorCache)
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

    private string GetFieldValue(string key) => Fields.FirstOrDefault(f => f.Key == key)?.Value ?? "";

    private string GetSavedFieldValue(string key)
    {
        CacheCurrentFields();
        return _fieldValueCache.GetValueOrDefault(key, "");
    }

    private async Task SaveAsync()
    {
        CacheCurrentFields();
        var title = GetSavedFieldValue("Title");
        if (string.IsNullOrWhiteSpace(title))
        {
            Status = "标题不能为空。";
            Raise(nameof(Status));
            _main.Report(Status);
            return;
        }

        var services = await _main.ServicesAsync();
        
        var creatorField = GetCreatorField();
        var creators = creatorField?.Creators
            .Select(c => new ItemCreatorInput(
                c.Role,
                Family: NullIfWhiteSpace(c.Family),
                Given: NullIfWhiteSpace(c.Given),
                Literal: NullIfWhiteSpace(c.Literal)))
            .Where(c => c.Family is not null || c.Given is not null || c.Literal is not null)
            .ToList() ?? new List<ItemCreatorInput>();
            
        var dates = BuildDates();
        
        if (_itemId is null)
        {
            var created = await services.Items.CreateItemAsync(new CreateItemRequest(
                ItemType,
                title,
                Subtitle: NullIfWhiteSpace(GetSavedFieldValue("Subtitle")),
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
            var updated = await services.Items.UpdateItemAsync(
                _itemId.Value,
                new UpdateItemRequest(
                    ItemType,
                    title,
                    Subtitle: NullIfWhiteSpace(GetSavedFieldValue("Subtitle")),
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
        if (string.IsNullOrWhiteSpace(IdentifierValue))
        {
            Status = "标识符内容不能为空。";
            Raise(nameof(Status));
            _main.Report(Status);
            return;
        }

        var input = new ItemIdentifierInput(IdentifierScheme.Trim(), IdentifierValue.Trim(), NullIfWhiteSpace(IdentifierNote));
        if (_itemId is null)
        {
            _pendingIdentifiers.Add(input);
            Identifiers.Add($"{FormatIdentifier(input)}（保存题录时写入）");
            Status = "标识符已暂存，保存题录时会一并写入。";
            IdentifierValue = "";
            IdentifierNote = "";
            RaiseAll();
            _main.Report(Status);
            return;
        }

        var result = await (await _main.ServicesAsync()).Items.AddIdentifierAsync(
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

        var services = await _main.ServicesAsync();
        var asset = await services.Files.RegisterFileAsync(FilePath);
        if (asset.IsFailure)
        {
            Status = asset.ErrorMessage ?? "关联文件注册失败。";
            Raise(nameof(Status));
            _main.Report(Status);
            return;
        }

        var document = await services.Documents.AttachDocumentInstanceAsync(
            _itemId.Value,
            asset.Value.FileAssetId,
            DocumentInstanceType.PrimaryScan,
            GetFieldValue("Title"),
            makePrimary: true);

        Status = document.IsSuccess ? "关联文件已注册并设为主文档。" : document.ErrorMessage ?? "主文档挂载失败。";
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
            foreach (var identifier in _pendingIdentifiers)
            {
                Identifiers.Add($"{FormatIdentifier(identifier)}（保存题录时写入）");
            }
            return;
        }

        var identifiers = await (await _main.ServicesAsync()).Items.ListIdentifiersAsync(_itemId.Value);
        if (identifiers.IsSuccess)
        {
            foreach (var identifier in identifiers.Value)
            {
                Identifiers.Add($"{identifier.Scheme}: {identifier.Value}");
            }
        }
    }

    private async Task RefreshLinkedFilesAsync()
    {
        LinkedFiles.Clear();
        if (_itemId is null)
        {
            return;
        }

        await using var connection = (await _main.ServicesAsync()).ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        var rows = await Dapper.SqlMapper.QueryAsync<string>(
            connection,
            """
            select coalesce(f.file_name, d.title, d.document_instance_id)
            from document_instances d
            left join file_assets f on f.file_asset_id = d.file_asset_id
            where d.item_id = @ItemId
            order by d.is_primary desc, d.created_at;
            """,
            new { ItemId = _itemId.Value.ToString() });
        foreach (var row in rows)
        {
            LinkedFiles.Add(row);
        }
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
        var trimmed = NullIfWhiteSpace(value);
        if (trimmed is null)
        {
            return null;
        }

        return trimmed.StartsWith('[')
            ? new ItemDateInput(role, DatePartsJson: trimmed)
            : new ItemDateInput(role, Literal: trimmed);
    }

    private string SerializeTags()
    {
        return JsonSerializer.Serialize(SplitNames(GetSavedFieldValue("TagsText")).ToArray());
    }

    private static IEnumerable<string> SplitNames(string value) =>
        value.Split(new[] { ';', ',', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !string.IsNullOrWhiteSpace(part));

    private static string FormatDate(IEnumerable<ItemDate> dates, string role, string? fallback)
    {
        var date = dates.FirstOrDefault(candidate => candidate.Role == role);
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

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private Task AddCreatorAsync()
    {
        var creatorField = GetCreatorField();
        creatorField?.Creators.Add(new CreatorItemViewModel(c => creatorField.Creators.Remove(c)));
        Raise(nameof(Creators));
        return Task.CompletedTask;
    }

    private ItemFieldDescriptor? GetCreatorField() => Fields.FirstOrDefault(f => f.Type == "CreatorList");

    private void SetFieldValue(string key, string value)
    {
        var field = Fields.FirstOrDefault(f => f.Key == key);
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

        var rendered = await (await _main.ServicesAsync()).CslRenderer.RenderAsync(new CslRenderRequest([_itemId.Value]));
        if (rendered.IsFailure)
        {
            HasCslPreviewWarning = true;
            CslPreviewText = rendered.ErrorMessage ?? "无法生成 CSL 预览。";
            return;
        }

        HasCslPreviewWarning = rendered.Value.Warnings.Count > 0;
        CslPreviewText = rendered.Value.RenderedText;
    }

    private static string FormatIdentifier(ItemIdentifierInput identifier)
        => string.IsNullOrWhiteSpace(identifier.Note)
            ? $"{identifier.Scheme}: {identifier.Value}"
            : $"{identifier.Scheme}: {identifier.Value} ({identifier.Note})";

    private void RaiseAll()
    {
        foreach (var property in new[]
        {
            nameof(Header),
            nameof(ItemIdText),
            nameof(HasItem),
            nameof(ItemType),
            nameof(Status),
            nameof(IsGeneralTypeWarningVisible),
            nameof(CslPreviewText),
            nameof(HasCslPreviewWarning)
        })
        {
            Raise(property);
        }
        RaiseEditorFieldProxies();
    }
}
