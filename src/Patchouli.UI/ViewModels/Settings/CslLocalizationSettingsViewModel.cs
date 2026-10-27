using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Csl;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.UI.ViewModels;

namespace Patchouli.UI.ViewModels.Settings;

/// <summary>Device-local language-to-locale rules used by CSL styles.</summary>
public sealed partial class CslLocalizationSettingsViewModel : SettingsSectionViewModelBase
{
    private static readonly IReadOnlyList<CslLanguageOption> KnownLanguages =
    [
        new("zh", "中文（zh）"), new("en", "英语（en）"), new("ja", "日语（ja）"),
        new("ko", "韩语（ko）"), new("de", "德语（de）"), new("fr", "法语（fr）"),
        new("es", "西班牙语（es）"), new("pt", "葡萄牙语（pt）"), new("ru", "俄语（ru）"),
        new("it", "意大利语（it）"), new("nl", "荷兰语（nl）"), new("ar", "阿拉伯语（ar）"),
        new("cs", "捷克语（cs）"), new("da", "丹麦语（da）"), new("el", "希腊语（el）"),
        new("fi", "芬兰语（fi）"), new("he", "希伯来语（he）"), new("hi", "印地语（hi）"),
        new("hu", "匈牙利语（hu）"), new("id", "印度尼西亚语（id）"), new("no", "挪威语（no）"),
        new("pl", "波兰语（pl）"), new("ro", "罗马尼亚语（ro）"), new("sv", "瑞典语（sv）"),
        new("th", "泰语（th）"), new("tr", "土耳其语（tr）"), new("uk", "乌克兰语（uk）"),
        new("vi", "越南语（vi）")
    ];

    private static readonly IReadOnlyList<CslLocaleOption> KnownLocales =
    [
        new("", "使用默认输出"),
        new("zh-CN", "中文（简体，中国）zh-CN"), new("zh-TW", "中文（繁体，台湾）zh-TW"),
        new("zh-HK", "中文（繁体，香港）zh-HK"), new("zh-Hans", "中文（简体）zh-Hans"),
        new("zh-Hant", "中文（繁体）zh-Hant"), new("en-US", "英语（美国）en-US"),
        new("en-GB", "英语（英国）en-GB"), new("ja-JP", "日语（日本）ja-JP"),
        new("ko-KR", "韩语（韩国）ko-KR"), new("de-DE", "德语（德国）de-DE"),
        new("de-AT", "德语（奥地利）de-AT"), new("de-CH", "德语（瑞士）de-CH"),
        new("fr-FR", "法语（法国）fr-FR"), new("fr-CA", "法语（加拿大）fr-CA"),
        new("es-ES", "西班牙语（西班牙）es-ES"), new("es-MX", "西班牙语（墨西哥）es-MX"),
        new("pt-BR", "葡萄牙语（巴西）pt-BR"), new("pt-PT", "葡萄牙语（葡萄牙）pt-PT"),
        new("ru-RU", "俄语（俄罗斯）ru-RU"), new("it-IT", "意大利语（意大利）it-IT"),
        new("nl-NL", "荷兰语（荷兰）nl-NL"), new("ar-SA", "阿拉伯语（沙特）ar-SA"),
        new("hi-IN", "印地语（印度）hi-IN"), new("id-ID", "印度尼西亚语（印度尼西亚）id-ID"),
        new("sv-SE", "瑞典语（瑞典）sv-SE"), new("th-TH", "泰语（泰国）th-TH"),
        new("tr-TR", "土耳其语（土耳其）tr-TR"), new("vi-VN", "越南语（越南）vi-VN"),
        new("cs-CZ", "捷克语（捷克）cs-CZ"), new("da-DK", "丹麦语（丹麦）da-DK"),
        new("el-GR", "希腊语（希腊）el-GR"), new("fi-FI", "芬兰语（芬兰）fi-FI"),
        new("he-IL", "希伯来语（以色列）he-IL"), new("hu-HU", "匈牙利语（匈牙利）hu-HU"),
        new("no-NO", "挪威语（挪威）no-NO"), new("pl-PL", "波兰语（波兰）pl-PL"),
        new("ro-RO", "罗马尼亚语（罗马尼亚）ro-RO"), new("uk-UA", "乌克兰语（乌克兰）uk-UA")
    ];

    private readonly MainWindowViewModel _main;
    private readonly HashSet<CslLanguageLocaleMappingRowViewModel> _observedRows = new();
    private Dictionary<string, string> _persistedMappings = new(StringComparer.OrdinalIgnoreCase);
    private string _persistedLocale = "";
    private CslLocaleOption _selectedDefaultLocale;
    private long _editVersion;
    private bool _isLoading;

    public CslLocalizationSettingsViewModel(MainWindowViewModel main)
    {
        _main = main;
        DefaultLocaleOptions = new ObservableCollection<CslLocaleOption>(KnownLocales.Skip(1));
        DefaultLocaleOptions.Insert(0, new CslLocaleOption("", "使用样式默认"));
        _selectedDefaultLocale = DefaultLocaleOptions[0];
        Rows.CollectionChanged += OnRowsCollectionChanged;
        foreach ((string language, string locale) in CslSettings.DefaultLanguageLocaleMappings)
        {
            Rows.Add(CreateRow(language, locale));
        }

        _persistedMappings = BuildMappings();
        AddMappingCommand = new RelayCommand(_ => AddMapping());
        RefreshValidationAndState();
    }

    public override bool SupportsEditing => true;

    [ExcludeFromDerivedGeneration]
    public override bool IsDirty => !_isLoading && (!TryBuildMappings(out Dictionary<string, string> mappings) ||
                                                    !MappingsEqual(mappings, _persistedMappings) ||
                                                    SelectedDefaultLocale.Locale != _persistedLocale);

    [ExcludeFromDerivedGeneration]
    public override bool CanSave => IsDirty && ValidationState != SettingsValidationState.Invalid && !IsSaving;

    public ObservableCollection<CslLanguageLocaleMappingRowViewModel> Rows { get; } = new();

    public ObservableCollection<CslLocaleOption> DefaultLocaleOptions { get; }

    public CslLocaleOption SelectedDefaultLocale
    {
        get => _selectedDefaultLocale;
        set
        {
            if (value is not null && SetProperty(ref _selectedDefaultLocale, value))
            {
                RefreshValidationAndState();
            }
        }
    }

    public RelayCommand AddMappingCommand { get; }

    public override async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (IsDirty || IsSaving)
        {
            return;
        }

        await LoadCoreAsync(cancellationToken);
    }

    private async Task LoadCoreAsync(CancellationToken cancellationToken = default)
    {
        long editVersion = _editVersion;
        try
        {
            HostServices services = await _main.ServicesAsync();
            int generation = _main.LibraryGeneration;
            Result<CslSettings> result = await Task.Run(() => services.CslStore.GetSettingsAsync(cancellationToken),
                cancellationToken);
            if (!_main.IsCurrentLibraryContext(services, generation))
            {
                return;
            }

            if (result.IsFailure)
            {
                ReportSaveFailure(result.ErrorMessage ?? "无法读取题录本地化规则。");
                return;
            }

            if (_editVersion != editVersion || IsSaving)
            {
                return;
            }

            _isLoading = true;
            string savedLocale = result.Value.Locale ?? "";
            CslLocaleOption? selected = DefaultLocaleOptions.FirstOrDefault(option => option.Locale == savedLocale);
            if (selected is null)
            {
                selected = new CslLocaleOption(savedLocale, $"保留现有 Locale（{savedLocale}）");
                DefaultLocaleOptions.Add(selected);
            }

            SelectedDefaultLocale = selected;
            _persistedLocale = savedLocale;
            Rows.Clear();
            foreach ((string language, string locale) in result.Value.EffectiveLanguageLocaleMappings)
            {
                Rows.Add(CreateRow(language, locale));
            }

            _persistedMappings = BuildMappings();
            ValidationState = SettingsValidationState.Valid;
            SaveState = SettingsSaveState.Clean;
            LastError = null;
            Status = "题录本地化规则已加载。";
        }
        finally
        {
            _isLoading = false;
            RefreshValidationAndState();
            Raise(nameof(IsDirty));
            Raise(nameof(CanSave));
        }
    }

    public override async Task SaveAsync()
    {
        if (!CanSave || !TryBuildMappings(out Dictionary<string, string> snapshot))
        {
            RefreshValidationAndState();
            return;
        }

        SaveState = SettingsSaveState.Saving;
        LastError = null;
        string localeSnapshot = SelectedDefaultLocale.Locale;
        try
        {
            HostServices services = await _main.ServicesAsync();
            int generation = _main.LibraryGeneration;
            Result<CslSettings> result =
                await Task.Run(() => services.CslStore.SaveLocalizationAsync(localeSnapshot, snapshot));
            if (!_main.IsCurrentLibraryContext(services, generation))
            {
                return;
            }

            if (result.IsFailure)
            {
                ReportSaveFailure(result.ErrorMessage ?? "无法保存题录本地化规则。");
                return;
            }

            _persistedMappings = new Dictionary<string, string>(snapshot, StringComparer.OrdinalIgnoreCase);
            _persistedLocale = localeSnapshot;
            bool hasNewEdits = IsDirty;
            SaveState = hasNewEdits ? SettingsSaveState.Dirty : SettingsSaveState.Saved;
            Status = hasNewEdits
                ? "已保存开始时的题录本地化规则，仍有新更改待保存。"
                : "题录本地化规则已保存。";
            Raise(nameof(IsDirty));
            Raise(nameof(CanSave));
            await _main.RefreshCslPreviewsAsync();
        }
        catch (Exception ex)
        {
            ReportSaveFailure(ex.Message);
        }
    }

    public override Task DiscardAsync()
    {
        return LoadCoreAsync();
    }

    private CslLanguageLocaleMappingRowViewModel CreateRow(string language, string locale)
    {
        return new CslLanguageLocaleMappingRowViewModel(
            CreateLanguageOptions(language), CreateLocaleOptions(locale), language, locale, RemoveMapping);
    }

    private static ObservableCollection<CslLanguageOption> CreateLanguageOptions(string selectedCode)
    {
        ObservableCollection<CslLanguageOption> options = new() { new CslLanguageOption("", "选择语言") };
        foreach (CslLanguageOption option in KnownLanguages)
        {
            options.Add(option);
        }

        if (selectedCode.Length > 0 && !options.Any(option =>
                string.Equals(option.Code, selectedCode, StringComparison.OrdinalIgnoreCase)))
        {
            string? normalized = ItemLanguage.Normalize(selectedCode);
            string? primary = ItemLanguage.GetPrimaryLanguage(normalized);
            CslLanguageOption? known = KnownLanguages.FirstOrDefault(option =>
                string.Equals(option.Code, primary, StringComparison.OrdinalIgnoreCase));
            options.Add(new CslLanguageOption(selectedCode,
                known is null ? $"保留原语言码（{selectedCode}）" : $"{known.DisplayName}（原值：{selectedCode}）"));
        }

        return options;
    }

    private static ObservableCollection<CslLocaleOption> CreateLocaleOptions(string selectedLocale)
    {
        ObservableCollection<CslLocaleOption> options = new(KnownLocales);
        if (!options.Any(option => string.Equals(option.Locale, selectedLocale, StringComparison.Ordinal)))
        {
            options.Add(new CslLocaleOption(selectedLocale, $"保留现有 locale（{selectedLocale}）"));
        }

        return options;
    }

    private void AddMapping()
    {
        Rows.Add(CreateRow("", "en-US"));
    }

    private void RemoveMapping(CslLanguageLocaleMappingRowViewModel row)
    {
        Rows.Remove(row);
    }

    private void OnRowsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (CslLanguageLocaleMappingRowViewModel row in _observedRows.ToArray())
        {
            if (!Rows.Contains(row))
            {
                row.PropertyChanged -= OnRowPropertyChanged;
                _observedRows.Remove(row);
            }
        }

        foreach (CslLanguageLocaleMappingRowViewModel row in Rows)
        {
            if (_observedRows.Add(row))
            {
                row.PropertyChanged += OnRowPropertyChanged;
            }
        }

        RefreshValidationAndState();
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CslLanguageLocaleMappingRowViewModel.SelectedLanguage) or
            nameof(CslLanguageLocaleMappingRowViewModel.SelectedLocale))
        {
            RefreshValidationAndState();
        }
    }

    private void RefreshValidationAndState()
    {
        if (_isLoading)
        {
            return;
        }

        _editVersion++;
        if (!TryBuildMappings(out _))
        {
            ValidationState = SettingsValidationState.Invalid;
            Status = HasDuplicateLanguage()
                ? "源语言重复，请为每种语言只保留一条映射。"
                : "请选择每条规则的源语言。";
        }
        else
        {
            ValidationState = SettingsValidationState.Valid;
            Status = IsDirty ? "有未保存的题录本地化规则。" : "题录本地化规则已保存。";
        }

        if (SaveState is not (SettingsSaveState.Saving or SettingsSaveState.Failed))
        {
            SaveState = IsDirty ? SettingsSaveState.Dirty : SettingsSaveState.Clean;
        }

        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
    }

    private bool TryBuildMappings(out Dictionary<string, string> mappings)
    {
        mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> seenLanguages = new(StringComparer.OrdinalIgnoreCase);
        foreach (CslLanguageLocaleMappingRowViewModel row in Rows)
        {
            string? source = row.SelectedLanguage?.Code.Trim();
            if (string.IsNullOrEmpty(source))
            {
                return false;
            }

            string duplicateKey = ItemLanguage.GetPrimaryLanguage(source) ?? source;
            if (!seenLanguages.Add(duplicateKey))
            {
                return false;
            }

            string mappingKey = NormalizeMappingKey(source);
            string locale = row.SelectedLocale?.Locale.Trim() ?? "";
            if (locale.Length > 0)
            {
                mappings.Add(mappingKey, locale);
            }
        }

        return true;
    }

    private Dictionary<string, string> BuildMappings()
    {
        return TryBuildMappings(out Dictionary<string, string> mappings)
            ? mappings
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private bool HasDuplicateLanguage()
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (CslLanguageLocaleMappingRowViewModel row in Rows)
        {
            string? source = row.SelectedLanguage?.Code.Trim();
            if (!string.IsNullOrEmpty(source) && !seen.Add(ItemLanguage.GetPrimaryLanguage(source) ?? source))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeMappingKey(string source)
    {
        string? normalized = ItemLanguage.Normalize(source);
        string? primary = ItemLanguage.GetPrimaryLanguage(normalized);
        CslLanguageOption? known = KnownLanguages.FirstOrDefault(option =>
            string.Equals(option.Code, primary, StringComparison.OrdinalIgnoreCase));
        return known is null || !string.Equals(normalized, primary, StringComparison.OrdinalIgnoreCase)
            ? source
            : primary!;
    }

    private static bool MappingsEqual(IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right)
    {
        return left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out string? value) &&
                                                             string.Equals(pair.Value, value,
                                                                 StringComparison.Ordinal));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Rows.CollectionChanged -= OnRowsCollectionChanged;
            foreach (CslLanguageLocaleMappingRowViewModel row in _observedRows)
            {
                row.PropertyChanged -= OnRowPropertyChanged;
            }

            _observedRows.Clear();
        }

        base.Dispose(disposing);
    }
}

public sealed record CslLanguageOption(string Code, string DisplayName);

public sealed record CslLocaleOption(string Locale, string DisplayName);

public sealed partial class CslLanguageLocaleMappingRowViewModel : ObservableObject
{
    public CslLanguageLocaleMappingRowViewModel(
        ObservableCollection<CslLanguageOption> languageOptions,
        ObservableCollection<CslLocaleOption> localeOptions,
        string language,
        string locale,
        Action<CslLanguageLocaleMappingRowViewModel> remove)
    {
        LanguageOptions = languageOptions;
        LocaleOptions = localeOptions;
        _selectedLanguage = languageOptions.FirstOrDefault(option =>
            string.Equals(option.Code, language, StringComparison.OrdinalIgnoreCase));
        _selectedLocale = localeOptions.FirstOrDefault(option =>
            string.Equals(option.Locale, locale, StringComparison.Ordinal));
        RemoveCommand = new RelayCommand(_ => remove(this));
    }

    private CslLanguageOption? _selectedLanguage;
    private CslLocaleOption? _selectedLocale;

    public CslLanguageOption? SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (value is not null)
            {
                SetProperty(ref _selectedLanguage, value);
            }
        }
    }

    public CslLocaleOption? SelectedLocale
    {
        get => _selectedLocale;
        set
        {
            if (value is not null)
            {
                SetProperty(ref _selectedLocale, value);
            }
        }
    }

    public ObservableCollection<CslLanguageOption> LanguageOptions { get; }
    public ObservableCollection<CslLocaleOption> LocaleOptions { get; }
    public RelayCommand RemoveCommand { get; }
}
