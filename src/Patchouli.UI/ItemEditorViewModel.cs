using System.Collections.ObjectModel;
using System.Text.Json;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;

namespace Patchouli.UI;

public sealed class CreatorItemViewModel : ViewModelBase
{
    private string _role = ItemCreatorRoles.Author;
    private string _literal = "";

    public string Role
    {
        get => _role;
        set { _role = value; Raise(); }
    }

    public string Literal
    {
        get => _literal;
        set { _literal = value; Raise(); }
    }

    public IReadOnlyList<string> AvailableRoles { get; } = new[]
    {
        ItemCreatorRoles.Author,
        ItemCreatorRoles.Editor,
        ItemCreatorRoles.Translator,
        "container-author"
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

    public ItemEditorViewModel(MainWindowViewModel main)
    {
        _main = main;
        NewCommand = new AsyncCommand(NewAsync);
        SaveCommand = new AsyncCommand(SaveAsync);
        AddCreatorCommand = new AsyncCommand(AddCreatorAsync);
        AddIdentifierCommand = new AsyncCommand(AddIdentifierAsync);
        RegisterFileCommand = new AsyncCommand(RegisterFileAsync);
    }

    public string Header => _itemId is null ? "新建题录" : "编辑题录";
    public string ItemIdText => _itemId?.ToString() ?? "";
    
    private string _itemType = "book";
    public string ItemType 
    { 
        get => _itemType; 
        set { _itemType = value; Raise(); } 
    }

    public IReadOnlyList<string> AvailableItemTypes { get; } = new[]
    {
        "book", "article-journal", "chapter", "thesis", "report", "webpage", 
        "dataset", "manuscript", "patent", "standard", "interview"
    };

    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string IssuedDate { get; set; } = "";
    public string AccessedDate { get; set; } = "";
    public string OriginalDate { get; set; } = "";
    public string PublicationTitle { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Place { get; set; } = "";
    public string Volume { get; set; } = "";
    public string Issue { get; set; } = "";
    public string Pages { get; set; } = "";
    public string Language { get; set; } = "";
    public string AbstractText { get; set; } = "";
    public string TagsText { get; set; } = "";

    // Identifier specific bindings
    private string _identifierScheme = "DOI";
    public string IdentifierScheme
    {
        get => _identifierScheme;
        set { _identifierScheme = value; Raise(); }
    }
    public string IdentifierValue { get; set; } = "";
    public string IdentifierNote { get; set; } = "";
    
    public IReadOnlyList<string> AvailableIdentifierSchemes { get; } = new[]
    {
        "DOI", "ISBN", "ISSN", "PMID", "PMCID", "arXiv", "JSTOR", "URL", 
        "archive_id", "call_number", "jpno", "ndlbibid", "custom"
    };

    public string FilePath { get; set; } = "";
    public string Status { get; private set; } = "填写题录信息后保存。";
    
    public ObservableCollection<CreatorItemViewModel> Creators { get; } = new();
    public ObservableCollection<string> Identifiers { get; } = new();
    public ObservableCollection<string> LinkedFiles { get; } = new();
    
    public bool HasItem => _itemId is not null;
    
    public AsyncCommand NewCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand AddCreatorCommand { get; }
    public AsyncCommand AddIdentifierCommand { get; }
    public AsyncCommand RegisterFileCommand { get; }

    public Task NewAsync()
    {
        _itemId = null;
        ItemType = "book";
        Title = "";
        Subtitle = "";
        Creators.Clear();
        IssuedDate = "";
        AccessedDate = "";
        OriginalDate = "";
        PublicationTitle = "";
        Publisher = "";
        Place = "";
        Volume = "";
        Issue = "";
        Pages = "";
        Language = "";
        AbstractText = "";
        TagsText = "";
        IdentifierScheme = "DOI";
        IdentifierValue = "";
        IdentifierNote = "";
        FilePath = "";
        Status = "正在新建题录。";
        Identifiers.Clear();
        LinkedFiles.Clear();
        RaiseAll();
        return Task.CompletedTask;
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
        ItemType = item.Value.ItemType;
        Title = item.Value.Title;
        Subtitle = item.Value.Subtitle ?? "";
        
        Creators.Clear();
        foreach (var creator in item.Value.Creators)
        {
            Creators.Add(new CreatorItemViewModel(RemoveCreator)
            {
                Role = creator.Role,
                Literal = creator.DisplayName
            });
        }

        IssuedDate = FormatDate(item.Value.Dates, ItemDateRoles.Issued, item.Value.Date);
        AccessedDate = FormatDate(item.Value.Dates, ItemDateRoles.Accessed, null);
        OriginalDate = FormatDate(item.Value.Dates, ItemDateRoles.OriginalDate, null);
        PublicationTitle = item.Value.PublicationTitle ?? "";
        Publisher = item.Value.Publisher ?? "";
        Place = item.Value.Place ?? "";
        Volume = item.Value.Volume ?? "";
        Issue = item.Value.Issue ?? "";
        Pages = item.Value.Pages ?? "";
        Language = item.Value.Language ?? "";
        AbstractText = item.Value.Abstract ?? "";
        TagsText = FormatTags(item.Value.TagsJson);
        Status = $"正在编辑：{item.Value.Title}";
        
        await RefreshIdentifiersAsync();
        await RefreshLinkedFilesAsync();
        RaiseAll();
    }

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Title))
        {
            Status = "标题不能为空。";
            Raise(nameof(Status));
            _main.Report(Status);
            return;
        }

        var services = await _main.ServicesAsync();
        
        var creators = Creators
            .Select(c => new ItemCreatorInput(c.Role, Literal: NullIfWhiteSpace(c.Literal)))
            .Where(c => c.Literal != null)
            .ToList();
            
        var dates = BuildDates();
        if (_itemId is null)
        {
            var created = await services.Items.CreateItemAsync(
                ItemType,
                Title,
                subtitle: Subtitle,
                publicationTitle: PublicationTitle,
                publisher: Publisher,
                place: Place,
                volume: Volume,
                issue: Issue,
                pages: Pages,
                language: Language,
                abstractText: AbstractText,
                tagsJson: SerializeTags(),
                creators: creators,
                dates: dates);

            if (created.IsFailure)
            {
                Status = created.ErrorMessage ?? "题录创建失败。";
                Raise(nameof(Status));
                _main.Report(Status);
                return;
            }

            _itemId = created.Value.ItemId;
        }
        else
        {
            var updated = await services.Items.UpdateItemAsync(
                _itemId.Value,
                new UpdateItemRequest(
                    ItemType,
                    Title,
                    Subtitle: NullIfWhiteSpace(Subtitle),
                    PublicationTitle: NullIfWhiteSpace(PublicationTitle),
                    Publisher: NullIfWhiteSpace(Publisher),
                    Place: NullIfWhiteSpace(Place),
                    Volume: NullIfWhiteSpace(Volume),
                    Issue: NullIfWhiteSpace(Issue),
                    Pages: NullIfWhiteSpace(Pages),
                    Language: NullIfWhiteSpace(Language),
                    AbstractText: NullIfWhiteSpace(AbstractText),
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

        Status = $"题录已保存：{Title.Trim()}";
        RaiseAll();
        _main.Report(Status);
        await _main.Shell.RefreshItemsAsync();
    }

    private Task AddCreatorAsync()
    {
        Creators.Add(new CreatorItemViewModel(RemoveCreator));
        return Task.CompletedTask;
    }

    private void RemoveCreator(CreatorItemViewModel creator)
    {
        Creators.Remove(creator);
    }

    private async Task AddIdentifierAsync()
    {
        if (_itemId is null)
        {
            Status = "请先保存题录，再添加标识符。";
            Raise(nameof(Status));
            _main.Report(Status);
            return;
        }

        var result = await (await _main.ServicesAsync()).Items.AddIdentifierAsync(
            _itemId.Value,
            IdentifierScheme,
            IdentifierValue,
            IdentifierNote);
            
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
            Status = "请填写 FileAsset 路径。";
            Raise(nameof(Status));
            _main.Report(Status);
            return;
        }

        var services = await _main.ServicesAsync();
        var asset = await services.Files.RegisterFileAsync(FilePath);
        if (asset.IsFailure)
        {
            Status = asset.ErrorMessage ?? "FileAsset 注册失败。";
            Raise(nameof(Status));
            _main.Report(Status);
            return;
        }

        var document = await services.Documents.AttachDocumentInstanceAsync(
            _itemId.Value,
            asset.Value.FileAssetId,
            DocumentInstanceType.PrimaryScan,
            Title,
            makePrimary: true);

        Status = document.IsSuccess ? "关联文件已注册并挂载为 DocumentInstance。" : document.ErrorMessage ?? "DocumentInstance 挂载失败。";
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
            BuildDate(ItemDateRoles.Issued, IssuedDate),
            BuildDate(ItemDateRoles.Accessed, AccessedDate),
            BuildDate(ItemDateRoles.OriginalDate, OriginalDate)
        }.Where(date => date is not null).Cast<ItemDateInput>().ToArray();
    }

    private static ItemDateInput? BuildDate(string role, string value)
    {
        var trimmed = NullIfWhiteSpace(value);
        return trimmed is null ? null : new ItemDateInput(role, Literal: trimmed);
    }

    private string SerializeTags()
    {
        return JsonSerializer.Serialize(SplitNames(TagsText).ToArray());
    }

    private static IEnumerable<string> SplitNames(string value) =>
        value.Split(new[] { ';', ',', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !string.IsNullOrWhiteSpace(part));

    private static string FormatDate(IEnumerable<ItemDate> dates, string role, string? fallback)
    {
        var date = dates.FirstOrDefault(candidate => candidate.Role == role);
        return date?.Literal ?? fallback ?? "";
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

    private void RaiseAll()
    {
        foreach (var property in new[]
        {
            nameof(Header), nameof(ItemIdText), nameof(ItemType), nameof(Title), nameof(Subtitle),
            nameof(IssuedDate), nameof(AccessedDate), nameof(OriginalDate),
            nameof(PublicationTitle), nameof(Publisher), nameof(Place), nameof(Volume), nameof(Issue), nameof(Pages),
            nameof(Language), nameof(AbstractText), nameof(TagsText), nameof(IdentifierScheme), nameof(IdentifierValue),
            nameof(IdentifierNote), nameof(FilePath), nameof(Status), nameof(Identifiers), nameof(LinkedFiles), nameof(HasItem)
        })
        {
            Raise(property);
        }
    }
}
