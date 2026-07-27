using System.Collections.ObjectModel;
using Patchouli.UI.ViewModels;

namespace Patchouli.UI.ViewModels.Settings;

public sealed class MetadataLookupSettingsViewModel : SettingsSectionViewModelBase
{
    private readonly MainWindowViewModel _main;

    public MetadataLookupSettingsViewModel(MainWindowViewModel main)
    {
        _main = main;
        RestoreDefaultsCommand = new RelayCommand(_ => Load(MetadataLookupAppSettings.Default(), true));
        SaveCommand = new AsyncCommand(SaveAsync);
        DiscardCommand = new AsyncCommand(DiscardAsync);
        Load(_main.AppOptions.MetadataLookup, false);
    }

    public ObservableCollection<MetadataSourceSettingsRowViewModel> Sources { get; } = new();
    public RelayCommand RestoreDefaultsCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand DiscardCommand { get; }

    public override bool SupportsEditing => true;
    public override bool CanSave => IsDirty;

    private bool _isDirty;

    public override bool IsDirty => _isDirty;

    private void SetDirty(bool value)
    {
        if (_isDirty == value)
        {
            return;
        }

        _isDirty = value;
        Raise(nameof(IsDirty));
    }

    public override string EffectiveSourceText => _main.AppOptions.Sync.SyncMetadataLookup
        ? "数据库同步基准"
        : "本机 JSON 设置";

    public override string ScopeText => _main.AppOptions.Sync.SyncMetadataLookup ? "随库同步" : "仅此设备";

    public override async Task SaveAsync()
    {
        SaveState = SettingsSaveState.Saving;
        Status = "正在保存...";
        MetadataLookupAppSettings settings = new(Sources
            .Select(source => new MetadataSourcePreference(source.SourceId, source.Enabled))
            .ToArray());
        SettingsSaveResult saved = await _main.SaveMetadataLookupSettingsAsync(settings);
        if (saved.IsSuccess)
        {
            SetDirty(false);
            SaveState = SettingsSaveState.Saved;
            ValidationState = SettingsValidationState.Valid;
            Status = "已保存";
            LastError = null;
        }
        else
        {
            SaveState = SettingsSaveState.Failed;
            Status = $"保存失败：{saved.ErrorMessage}";
            LastError = saved.ErrorMessage;
        }
    }

    public override Task DiscardAsync()
    {
        Load(_main.AppOptions.MetadataLookup, false);
        SaveState = SettingsSaveState.Clean;
        Status = "已放弃更改";
        return Task.CompletedTask;
    }

    internal void ReloadFromEffectiveSettingsIfClean(MetadataLookupAppSettings settings)
    {
        if (!IsDirty)
        {
            Load(settings, false);
        }

        Raise(nameof(EffectiveSourceText));
        Raise(nameof(ScopeText));
    }

    internal void MarkDirty()
    {
        SetDirty(true);
        SaveState = SettingsSaveState.Dirty;
        Status = "有未保存的更改";
    }

    internal void Move(MetadataSourceSettingsRowViewModel source, int offset)
    {
        int current = Sources.IndexOf(source);
        int destination = current + offset;
        if (current < 0 || destination < 0 || destination >= Sources.Count)
        {
            return;
        }

        Sources.Move(current, destination);
        RefreshPositions();
        MarkDirty();
    }

    private void Load(MetadataLookupAppSettings settings, bool dirty)
    {
        Sources.Clear();
        foreach (MetadataSourcePreference preference in MetadataLookupAppSettings.MergeWithDefaults(settings.Sources)
                     .Sources)
        {
            MetadataSourceDescriptor descriptor =
                Definitions.TryGetValue(preference.SourceId, out MetadataSourceDescriptor? definition)
                    ? definition
                    : new MetadataSourceDescriptor(preference.SourceId, "其他标识符来源");
            Sources.Add(new MetadataSourceSettingsRowViewModel(this, preference.SourceId, descriptor.Name,
                descriptor.Description, preference.Enabled));
        }

        RefreshPositions();
        SetDirty(dirty);
        SaveState = dirty ? SettingsSaveState.Dirty : SettingsSaveState.Saved;
        Status = dirty ? "有未保存的更改" : "已保存";
    }

    private void RefreshPositions()
    {
        for (int index = 0; index < Sources.Count; index++)
        {
            Sources[index].SetPosition(index == 0, index == Sources.Count - 1);
        }
    }

    private sealed record MetadataSourceDescriptor(string Name, string Description);

    private static readonly IReadOnlyDictionary<string, MetadataSourceDescriptor> Definitions =
        new Dictionary<string, MetadataSourceDescriptor>(StringComparer.OrdinalIgnoreCase)
        {
            ["calis"] = new("CALIS 联合目录", "ISBN，中国大陆高校联合目录"),
            ["nlc"] = new("中国国家图书馆", "ISBN，中国国家图书馆公共目录"),
            ["ndl"] = new("日本国立国会图书馆 (NDL)", "NDLBibID、JPNO、NDLJP"),
            ["cinii"] = new("CiNii", "NCID、NAID、CRID"),
            ["library-of-congress"] = new("Library of Congress", "LCCN"),
            ["dnb"] = new("Deutsche Nationalbibliothek", "DNB ID、GND、URN:NBN:DE"),
            ["bnf"] = new("Bibliothèque nationale de France", "ARK、FRBNF"),
            ["pmc-id-converter"] = new("PMC ID Converter", "DOI、PMID、PMCID、MID"),
            ["pubmed"] = new("PubMed", "PMID"),
            ["arxiv"] = new("arXiv", "arXiv ID"),
            ["open-library"] = new("Open Library", "ISBN、OCLC、LCCN、OLID"),
            ["crossref"] = new("Crossref", "DOI"),
            ["datacite"] = new("DataCite", "DOI"),
            ["openalex"] = new("OpenAlex", "DOI、PMID、PMCID、MAG、OpenAlex"),
            ["semantic-scholar"] = new("Semantic Scholar", "Paper ID、DOI、arXiv、PMID"),
            ["google-books"] = new("Google Books", "ISBN、Google volume ID")
        };
}

public sealed class MetadataSourceSettingsRowViewModel : ViewModelBase
{
    private readonly MetadataLookupSettingsViewModel _parent;
    private bool _enabled;
    private bool _isFirst;
    private bool _isLast;

    internal MetadataSourceSettingsRowViewModel(MetadataLookupSettingsViewModel parent, string sourceId, string name,
        string description, bool enabled)
    {
        _parent = parent;
        SourceId = sourceId;
        Name = name;
        Description = description;
        _enabled = enabled;
        MoveUpCommand = new RelayCommand(_ => _parent.Move(this, -1));
        MoveDownCommand = new RelayCommand(_ => _parent.Move(this, 1));
    }

    public string SourceId { get; }
    public string Name { get; }
    public string Description { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }

            _enabled = value;
            Raise();
            _parent.MarkDirty();
        }
    }

    public bool CanMoveUp => !_isFirst;
    public bool CanMoveDown => !_isLast;

    internal void SetPosition(bool isFirst, bool isLast)
    {
        _isFirst = isFirst;
        _isLast = isLast;
        Raise(nameof(CanMoveUp));
        Raise(nameof(CanMoveDown));
    }
}
