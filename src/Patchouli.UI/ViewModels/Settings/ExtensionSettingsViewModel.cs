using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Credentials;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Composition;
using Patchouli.Host.Extensions;

namespace Patchouli.UI.ViewModels.Settings;

/// <summary>Configuration and source are independent drafts; saving never executes a translation.</summary>
public sealed partial class ExtensionSettingsViewModel : SettingsSectionViewModelBase
{
    private readonly MainWindowViewModel _main;
    private IExtensionManager? _manager;
    private HostServices? _services;
    private ExtensionPackageSnapshot? _source;
    private Task _selectionLoadTask = Task.CompletedTask;
    private bool _settingSelection;
    private long _selectionVersion;
    private long _configRevision;
    private IReadOnlyList<string> _credentialReferences = [];
    private Dictionary<string, string> _draftSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>> _pendingSources = new(StringComparer.Ordinal);
    private bool _loadingSource;

    public ExtensionSettingsViewModel(MainWindowViewModel main)
    {
        _main = main;
        RefreshCommand = new AsyncCommand(() => LoadAsync());
        SaveConfigurationCommand = new AsyncCommand(SaveConfigurationAsync);
        SaveSourceCommand = new AsyncCommand(SaveSourceAsync);
        CheckCommand = new AsyncCommand(CheckAsync);
        ActivateCommand = new AsyncCommand(ActivateAsync);
        CopyCommand = new AsyncCommand(CopyAsync);
        TestCommand = new AsyncCommand(TestAsync);
        PromoteCommand = new AsyncCommand(PromoteAsync);
        RollbackCommand = new AsyncCommand(RollbackAsync);
        ResetCommand = new RelayCommand(_ => ResetConfiguration());
        ClearCacheCommand = new AsyncCommand(ClearCacheAsync);
        SaveAuthorizationCommand = new AsyncCommand(SaveAuthorizationAsync);
        TestDraftCommand = new AsyncCommand(TestDraftAsync);
    }

    public ObservableCollection<ExtensionListItem> Extensions { get; } = [];
    public ObservableCollection<ExtensionConfigFieldViewModel> Fields { get; } = [];
    public ObservableCollection<ExtensionDiagnostic> Diagnostics { get; } = [];
    public ObservableCollection<string> SourceFiles { get; } = [];
    [ObservableProperty] public partial string? SelectedSourceFile { get; set; }
    [ObservableProperty] public partial ExtensionListItem? SelectedExtension { get; set; }
    [ObservableProperty] public partial string SourceText { get; set; } = "";
    [ObservableProperty] public partial string CopyId { get; set; } = "";
    [ObservableProperty] public partial string SampleText { get; set; } = "Hello, world.";
    [ObservableProperty] public partial string SampleResult { get; set; } = "";
    [ObservableProperty] public partial bool AllowHttp { get; set; }
    [ObservableProperty] public partial bool AllowLlm { get; set; }
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand SaveConfigurationCommand { get; }
    public AsyncCommand SaveSourceCommand { get; }
    public AsyncCommand CheckCommand { get; }
    public AsyncCommand ActivateCommand { get; }
    public AsyncCommand CopyCommand { get; }
    public AsyncCommand TestCommand { get; }
    public AsyncCommand PromoteCommand { get; }
    public AsyncCommand RollbackCommand { get; }
    public RelayCommand ResetCommand { get; }
    public AsyncCommand ClearCacheCommand { get; }
    public AsyncCommand SaveAuthorizationCommand { get; }
    public AsyncCommand TestDraftCommand { get; }
    public override bool SupportsEditing => false;
    public override bool IsDirty => false;
    public override bool CanSave => false;

    partial void OnSourceTextChanged(string value)
    {
        if (!_loadingSource && SelectedSourceFile is { } file)
        {
            _draftSources[file] = value;
        }
    }

    partial void OnSelectedSourceFileChanged(string? value)
    {
        if (value is not null)
        {
            _loadingSource = true;
            SourceText = _draftSources.GetValueOrDefault(value, "");
            _loadingSource = false;
        }
    }

    partial void OnSelectedExtensionChanged(ExtensionListItem? value)
    {
        if (_source is { } previous)
        {
            bool changed = _draftSources.Count != previous.Sources.Count || _draftSources.Any(pair =>
                !previous.Sources.TryGetValue(pair.Key, out string? original) || original != pair.Value);
            if (changed)
            {
                _pendingSources[previous.Id] = new Dictionary<string, string>(_draftSources, StringComparer.Ordinal);
            }
            else
            {
                _pendingSources.Remove(previous.Id);
            }
        }

        long version = Interlocked.Increment(ref _selectionVersion);
        if (_settingSelection)
        {
            return;
        }

        IExtensionManager? manager = _manager;
        HostServices? services = _services;
        _selectionLoadTask = manager is null || services is null
            ? Task.CompletedTask
            : LoadSelectedAsync(value, version, manager, services);
        _selectionLoadTask.Observe(nameof(ExtensionSettingsViewModel), "load-extension");
    }

    public override async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        HostServices services = await _main.ServicesAsync();
        await services.TranslationExtensions.InitializeAsync(cancellationToken);
        if (!ReferenceEquals(services, await _main.ServicesAsync()))
        {
            return;
        }

        _services = services;
        IExtensionManager manager = services.TranslationExtensions.Manager;
        _manager = manager;
        Result<IReadOnlyList<ProviderCredentialMetadata>> credentials =
            await services.Credentials.ListAsync(cancellationToken);
        _credentialReferences = credentials.IsSuccess
            ? credentials.Value.Where(item => item.Status == ProviderCredentialStatus.Active)
                .Select(item => item.ProviderId).ToArray()
            : [];
        string? selected = SelectedExtension?.Id;
        Extensions.Clear();
        foreach (ExtensionGenerationInfo generation in manager.List().Where(item => item.IsActive))
        {
            await using IExtensionLease? lease = await manager.AcquireAsync(generation.Id, cancellationToken);
            if (lease is not null)
            {
                Extensions.Add(new ExtensionListItem(lease.Descriptor.Id, lease.Descriptor.Name,
                    generation.Fingerprint, lease.Descriptor.Kind));
            }
        }

        HashSet<string> listed = Extensions.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (string id in services.TranslationExtensions.ListWorkingPackages().Where(id => !listed.Contains(id)))
        {
            ExtensionPackageSnapshot working = await manager.ReadSourceAsync(id, cancellationToken);
            Extensions.Add(new ExtensionListItem(id, id + "（未激活）", working.Fingerprint, IsActive: false));
        }

        if (!ReferenceEquals(services, await _main.ServicesAsync()))
        {
            return;
        }

        _settingSelection = true;
        try
        {
            SelectedExtension = Extensions.FirstOrDefault(item => item.Id == selected) ?? Extensions.FirstOrDefault();
        }
        finally
        {
            _settingSelection = false;
        }

        long selectionVersion = Interlocked.Increment(ref _selectionVersion);
        _selectionLoadTask = LoadSelectedAsync(SelectedExtension, selectionVersion, manager, services);
        _selectionLoadTask.Observe(nameof(ExtensionSettingsViewModel), "load-extension");
        await _selectionLoadTask;
        Status = "选择扩展后填写配置，或编辑其源码。";
    }

    private async Task LoadSelectedAsync(ExtensionListItem? item, long version,
        IExtensionManager manager, HostServices services)
    {
        if (item is null)
        {
            return;
        }

        ExtensionPackageSnapshot source = await manager.ReadSourceAsync(item.Id);
        ExtensionConfigSnapshot config = await manager.ReadConfigAsync(item.Id);
        ExtensionCheckResult check = await manager.CheckAsync(source);
        if (version != Volatile.Read(ref _selectionVersion)
            || !ReferenceEquals(services, _services) || !ReferenceEquals(manager, _manager))
        {
            return;
        }

        _source = source;
        ExtensionNetworkGrant grant = services.TranslationExtensions.Authorization.Get(item.Id);
        AllowHttp = grant.Http;
        AllowLlm = grant.Llm;
        _configRevision = config.Revision;
        _draftSources = new Dictionary<string, string>(
            _pendingSources.GetValueOrDefault(source.Id) ?? source.Sources, StringComparer.Ordinal);
        SourceFiles.Clear();
        foreach (string file in source.Sources.Keys.OrderBy(file => file, StringComparer.Ordinal))
        {
            SourceFiles.Add(file);
        }

        SelectedSourceFile = null;
        SelectedSourceFile = SourceFiles.Contains("main.fsx") ? "main.fsx" : SourceFiles.FirstOrDefault();
        ApplyFields(check.ConfigFields, config.Values);

        ShowDiagnostics(check);
        CopyId = item.Id + ".custom";
    }

    private void ApplyFields(IReadOnlyList<ConfigFieldDescriptor> descriptors,
        IReadOnlyDictionary<string, string> values)
    {
        Fields.Clear();
        foreach (ConfigFieldDescriptor field in descriptors)
        {
            Fields.Add(new ExtensionConfigFieldViewModel(field,
                values.GetValueOrDefault(field.Key) ?? field.DefaultValue ?? "",
                _main.AppOptions.Llm.Providers.Select(provider => provider.ProviderId).ToArray(),
                _credentialReferences,
                _main.AppOptions.Llm.Providers.GroupBy(provider => provider.ProviderId, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key,
                        group => (IReadOnlyList<string>)group.Select(provider => provider.Model)
                            .Where(model => !string.IsNullOrWhiteSpace(model)).Distinct(StringComparer.Ordinal)
                            .ToArray(),
                        StringComparer.Ordinal)));
        }
    }

    private ExtensionPackageSnapshot Draft()
    {
        if (_source is null)
        {
            throw new InvalidOperationException("请先选择扩展。");
        }

        return new ExtensionPackageSnapshot(_source.Id, _draftSources);
    }

    private async Task CheckAsync()
    {
        if (_manager is not null && _source is not null)
        {
            long version = Volatile.Read(ref _selectionVersion);
            ExtensionCheckResult check = await _manager.CheckAsync(Draft());
            if (version != Volatile.Read(ref _selectionVersion))
            {
                return;
            }

            ShowDiagnostics(check);
            if (check.Succeeded)
            {
                ApplyFields(check.ConfigFields,
                    Fields.ToDictionary(field => field.Descriptor.Key, field => field.Value));
            }
        }
    }

    private async Task SaveSourceAsync()
    {
        if (_manager is null || _source is null)
        {
            return;
        }

        long version = Volatile.Read(ref _selectionVersion);
        ExtensionPackageSnapshot draft = Draft();
        HostServices services = await _main.ServicesAsync();
        await services.TranslationExtensions.SaveDraftAsync(draft);
        if (version != Volatile.Read(ref _selectionVersion))
        {
            return;
        }

        _source = draft;
        _pendingSources.Remove(draft.Id);
        Status = "源码已保存；当前运行版本未切换。";
    }

    private async Task ActivateAsync()
    {
        if (_manager is null || _source is null)
        {
            return;
        }

        long version = Volatile.Read(ref _selectionVersion);
        ExtensionPackageSnapshot draft = Draft();
        HostServices services = await _main.ServicesAsync();
        ExtensionCheckResult result = await services.TranslationExtensions.ActivateAsync(draft);
        if (result.Succeeded)
        {
            await _manager.SaveSourceAsync(draft.Id, draft.Sources);
        }

        if (version != Volatile.Read(ref _selectionVersion))
        {
            return;
        }

        ShowDiagnostics(result);
        if (result.Succeeded)
        {
            _source = draft;
            _pendingSources.Remove(draft.Id);
            ApplyFields(result.ConfigFields, Fields.ToDictionary(field => field.Descriptor.Key, field => field.Value));
            Status = "已激活；已开始的任务继续使用原版本，LKG 未提升。";
        }
    }

    private async Task SaveConfigurationAsync()
    {
        if (_manager is null || _source is null)
        {
            return;
        }

        string? error = Fields.Select(field => field.Validate()).FirstOrDefault(value => value is not null);
        if (error is not null)
        {
            LastError = error;
            Status = error;
            return;
        }

        await _manager.SaveConfigAsync(_source.Id,
            new ExtensionConfigSnapshot(Fields.ToDictionary(field => field.Descriptor.Key, field => field.Value),
                ++_configRevision));
        LastError = null;
        Status = "配置已保存，对之后启动的任务生效。";
    }

    private async Task CopyAsync()
    {
        if (_manager is null || _source is null)
        {
            return;
        }

        string id = CopyId.Trim();
        if (id.Length == 0 || id.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_')) ||
            _manager.List().Any(item => item.Id == id) ||
            Extensions.Any(item => item.Id == id))
        {
            Status = "请填写未使用的扩展 ID（字母、数字、点、横线或下划线）。";
            return;
        }

        Dictionary<string, string> sources = new(Draft().Sources, StringComparer.Ordinal);
        // The export descriptor is part of the ordinary source; replace only its explicit identity literal.
        sources["main.fsx"] =
            sources["main.fsx"].Replace('"' + _source.Id + '"', '"' + id + '"', StringComparison.Ordinal);
        if (sources.TryGetValue("extension.yml", out string? manifest))
        {
            sources["extension.yml"] = string.Join("\n", manifest.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n').Select(line => line.StartsWith("id:", StringComparison.Ordinal) ? "id: " + id : line));
        }

        ExtensionPackageSnapshot copy = new(id, sources);
        await _manager.SaveSourceAsync(id, sources);
        HostServices services = await _main.ServicesAsync();
        ShowDiagnostics(await services.TranslationExtensions.ActivateAsync(copy));
        await LoadAsync();
        SelectedExtension = Extensions.FirstOrDefault(item => item.Id == id);
    }

    private async Task TestAsync()
    {
        if (SelectedExtension is null)
        {
            return;
        }

        HostServices services = await _main.ServicesAsync();
        TranslationExtensionResult result = await services.TranslationExtensions.TestAsync(
            SelectedExtension.Id, SampleText, _main.AppOptions.Llm.Translation.TargetLanguage);
        SampleResult = string.Join("\n", result.Outcomes.Select(item =>
            item.GetTranslatedText() ?? item.ErrorMessage ?? item.Kind.ToString()));
        Status = "样例请求已完成；未写入书库。";
    }

    private async Task ClearCacheAsync()
    {
        if (SelectedExtension is not { } selected)
        {
            return;
        }

        HostServices services = await _main.ServicesAsync();
        bool cleared = await services.TranslationExtensions.ClearDerivedStateAsync(selected.Id);
        Status = cleared ? "派生缓存已清空；已提交译文保留。" : "此扩展没有可清理的派生缓存。";
    }

    private async Task TestDraftAsync()
    {
        if (_source is null)
        {
            return;
        }

        HostServices services = await _main.ServicesAsync();
        ExtensionConfigSnapshot config = new(Fields.ToDictionary(field => field.Descriptor.Key, field => field.Value),
            _configRevision);
        TranslationExtensionResult result = await services.TranslationExtensions.TestDraftAsync(Draft(), config,
            SampleText, _main.AppOptions.Llm.Translation.TargetLanguage);
        SampleResult = string.Join("\n",
            result.Outcomes.Select(item => item.GetTranslatedText() ?? item.ErrorMessage ?? item.Kind.ToString()));
        Status = "草稿样例契约测试完成；当前 Active、LKG 和书库内容均未变更。";
    }

    private async Task SaveAuthorizationAsync()
    {
        if (SelectedExtension is not { } selected)
        {
            return;
        }

        ExtensionNetworkGrant grant = new(AllowHttp, AllowLlm);
        HostServices services = await _main.ServicesAsync();
        services.TranslationExtensions.Authorization.Save(selected.Id, grant);
        Status = "联网授权已保存，对之后启动的任务生效。";
    }

    private async Task PromoteAsync()
    {
        if (_manager is not null && SelectedExtension is { } selected)
        {
            await _manager.PromoteLastKnownGoodAsync(selected.Id);
            Status = "已将当前运行版本确认为可用恢复版本。";
        }
    }

    private async Task RollbackAsync()
    {
        if (_manager is not null && SelectedExtension is { } selected)
        {
            HostServices services = await _main.ServicesAsync();
            bool restored = await services.TranslationExtensions.RollbackAsync(selected.Id);
            Status = restored ? "运行版本已回滚；工作源码与书库内容保留。" : "没有兼容的可用恢复版本。";
        }
    }

    private void ResetConfiguration()
    {
        foreach (ExtensionConfigFieldViewModel field in Fields)
        {
            field.Value = field.Descriptor.DefaultValue ?? "";
        }

        Status = "已填入默认值，保存配置后生效。";
    }

    private void ShowDiagnostics(ExtensionCheckResult result)
    {
        Diagnostics.Clear();
        foreach (ExtensionDiagnostic diagnostic in result.Diagnostics)
        {
            Diagnostics.Add(diagnostic);
        }

        ValidationState = result.Succeeded ? SettingsValidationState.Valid : SettingsValidationState.Invalid;
        Status = result.Succeeded ? "静态检查通过，未执行翻译。" : "检查未通过，当前运行版本保留。";
    }

    public override Task SaveAsync()
    {
        return SaveConfigurationAsync();
    }

    public override Task DiscardAsync()
    {
        return LoadAsync();
    }
}

public sealed record ExtensionListItem(
    string Id,
    string Name,
    string Fingerprint,
    ExtensionKind Kind = ExtensionKind.Translation,
    bool IsActive = true)
{
    public override string ToString()
    {
        return Name;
    }
}

public sealed partial class ExtensionConfigFieldViewModel : ViewModelBase
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _models;

    public ExtensionConfigFieldViewModel(ConfigFieldDescriptor descriptor, string value,
        IReadOnlyList<string> providers,
        IReadOnlyList<string>? credentialReferences = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? models = null)
    {
        _models = models ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        Descriptor = descriptor;
        Value = value;
        Providers = new[] { "" }.Concat(providers).ToArray();
        CredentialReferences = new[] { "" }.Concat(credentialReferences ?? []).ToArray();
        Choices = descriptor.Choices ?? [];
        IsModel = descriptor.Kind == ConfigValueKind.Model;
        IsBoolean = descriptor.Kind == ConfigValueKind.Boolean;
        IsChoice = descriptor.Kind == ConfigValueKind.Choice;
        IsCredential = descriptor.Kind == ConfigValueKind.CredentialReference;
        IsCapability = descriptor.Kind == ConfigValueKind.Capability;
    }

    public ConfigFieldDescriptor Descriptor { get; }
    public IReadOnlyList<string> Providers { get; }
    public IReadOnlyList<string> CredentialReferences { get; }
    public IReadOnlyList<string> Choices { get; }
    public bool IsModel { get; }
    public bool IsBoolean { get; }
    public bool IsChoice { get; }
    public bool IsCredential { get; }
    public bool IsCapability { get; }
    public bool IsText => !IsModel && !IsBoolean && !IsChoice && !IsCredential && !IsCapability;
    [ObservableProperty] public partial string Value { get; set; } = "";

    public bool BooleanValue
    {
        get => bool.TryParse(Value, out bool value) && value;
        set => Value = value ? "true" : "false";
    }

    public string ModelProvider
    {
        get => ReadModelPart("providerId");
        set => WriteModel(value, ModelName);
    }

    public string ModelName
    {
        get => ReadModelPart("model");
        set => WriteModel(ModelProvider, value);
    }

    [ExcludeFromDerivedGeneration]
    public IReadOnlyList<string> ModelSuggestions => _models.GetValueOrDefault(ModelProvider) ?? [];

    partial void OnValueChanged(string value)
    {
        Raise(nameof(BooleanValue));
        Raise(nameof(ModelProvider));
        Raise(nameof(ModelName));
        Raise(nameof(ModelSuggestions));
    }

    private string ReadModelPart(string name)
    {
        if (string.IsNullOrWhiteSpace(Value))
        {
            return "";
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(Value);
            return document.RootElement.TryGetProperty(name, out JsonElement part) ? part.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
            return name == "model" ? Value : "";
        }
    }

    private void WriteModel(string provider, string model)
    {
        Value = string.IsNullOrWhiteSpace(provider) && string.IsNullOrWhiteSpace(model)
            ? ""
            : JsonSerializer.Serialize(new { providerId = provider, model });
    }

    public string? Validate()
    {
        if (Descriptor.Required && string.IsNullOrWhiteSpace(Value))
        {
            return $"{Descriptor.Label}为必填项。";
        }

        if (Descriptor.Kind is ConfigValueKind.Integer or ConfigValueKind.Decimal)
        {
            if (!decimal.TryParse(Value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal number) ||
                (Descriptor.Kind == ConfigValueKind.Integer && decimal.Truncate(number) != number) ||
                (Descriptor.Minimum is { } minimum && number < minimum) ||
                (Descriptor.Maximum is { } maximum && number > maximum))
            {
                return $"{Descriptor.Label}的数值或范围无效。";
            }
        }

        return null;
    }
}
