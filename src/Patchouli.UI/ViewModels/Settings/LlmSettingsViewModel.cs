using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using ToolkitRelayCommand = CommunityToolkit.Mvvm.Input.RelayCommand;
using Patchouli.Core.Credentials;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Llm;
using Patchouli.UI.ViewModels;

namespace Patchouli.UI.ViewModels.Settings;

/// <summary>
/// 「模型连接与聊天」section. The catalog supplies available provider types; the form displays providers with
/// stored credentials and the provider currently being added. API keys never enter
/// the settings file: each provider key is written through the existing
/// <see cref="ICredentialStore"/> (D2); saved API keys are loaded into masked inputs and tracked separately from edits.
/// Edits stay in memory until the debounced auto-save pipeline (or the unified save command) commits them.
/// </summary>
public sealed partial class LlmSettingsViewModel : SettingsSectionViewModelBase
{
    private readonly MainWindowViewModel _main;
    private Func<Task<ICredentialStore>> _credentialStoreFactory;
    private Func<Task<ILlmSubscriptionService>> _subscriptionServiceFactory;

    private Action<Uri> _openSubscriptionBrowser = uri =>
    {
        using Process? browser = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    };

    private LlmAppSettings _current;
    private LlmAppSettings _persisted;
    private bool _isConstructing;
    private bool _syncingDraft;
    private bool _isDirty;
    private string? _addingProviderId;

    public LlmSettingsViewModel(MainWindowViewModel main)
        : this(main, () => ResolveCredentialStoreAsync(main))
    {
    }

    internal LlmSettingsViewModel(MainWindowViewModel main, Func<Task<ICredentialStore>> credentialStoreFactory)
    {
        ArgumentNullException.ThrowIfNull(main);
        ArgumentNullException.ThrowIfNull(credentialStoreFactory);

        _isConstructing = true;
        _main = main;
        _credentialStoreFactory = credentialStoreFactory;
        _subscriptionServiceFactory = async () => new LlmSubscriptionService(await _credentialStoreFactory());
        _current = main.AppOptions.Llm;
        _persisted = _current;
        AddProviderCommand =
            new ToolkitRelayCommand(AddProvider, () => SelectedProviderToAdd is not null && !HasPendingProvider);
        CancelAddProviderCommand = new ToolkitRelayCommand(CancelAddProvider, () => HasPendingProvider);
        BuildProviders();
        SyncFromPersisted();
        _isConstructing = false;
    }

    /// <summary>Backing drafts in catalog order; only VisibleProviders are rendered by the view.</summary>
    [ExcludeFromDerivedGeneration]
    public ObservableCollection<LlmProviderSettingsRowViewModel> Providers { get; } = [];

    [ExcludeFromDerivedGeneration]
    public ObservableCollection<LlmProviderSettingsRowViewModel> VisibleProviders { get; } = [];

    [ExcludeFromDerivedGeneration]
    public ObservableCollection<LlmSelectionOption> AvailableProviderOptions { get; } = [];

    [ObservableProperty] public partial LlmSelectionOption? SelectedProviderToAdd { get; set; }

    partial void OnSelectedProviderToAddChanged(LlmSelectionOption? value)
    {
        AddProviderCommand.NotifyCanExecuteChanged();
    }

    [ExcludeFromDerivedGeneration] public bool HasPendingProvider => _addingProviderId is not null;

    [ExcludeFromDerivedGeneration] public bool HasNoProviders => VisibleProviders.Count == 0;

    public ToolkitRelayCommand AddProviderCommand { get; }
    public ToolkitRelayCommand CancelAddProviderCommand { get; }

    [ExcludeFromDerivedGeneration] public ObservableCollection<LlmSelectionOption> ChatProviderOptions { get; } = [];

    [ExcludeFromDerivedGeneration] public ObservableCollection<string> ChatModelOptions { get; } = [];

    [ExcludeFromDerivedGeneration] public ObservableCollection<LlmSelectionOption> OcrProviderOptions { get; } = [];

    [ExcludeFromDerivedGeneration] public ObservableCollection<string> OcrModelOptions { get; } = [];

    // A selector can clear its item while loading or refreshing the available providers.
    // That UI state must not overwrite the configured default or schedule a save.
    [ExcludeFromDerivedGeneration]
    public LlmSelectionOption? SelectedChatProvider
    {
        get => ChatProviderOptions.FirstOrDefault(option => option.ProviderId == ChatProviderId);
        set
        {
            if (value is not null && !IsWaitingForDraftSync())
            {
                ChatProviderId = value.ProviderId;
            }
        }
    }

    [ExcludeFromDerivedGeneration]
    public LlmSelectionOption? SelectedOcrProvider
    {
        get => OcrProviderOptions.FirstOrDefault(option => option.ProviderId == OcrProviderId);
        set
        {
            if (value is not null && !IsWaitingForDraftSync())
            {
                OcrProviderId = value.ProviderId;
            }
        }
    }

    [ExcludeFromDerivedGeneration] public string ChatProviderPlaceholder => DescribeUnavailableProvider(ChatProviderId);

    [ExcludeFromDerivedGeneration] public string OcrProviderPlaceholder => DescribeUnavailableProvider(OcrProviderId);

    private string DescribeUnavailableProvider(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return "未选择 Provider";
        }

        string name = Providers.FirstOrDefault(row => row.ProviderId == providerId)?.DisplayName ?? providerId;
        return $"{name}（未就绪）";
    }

    [ObservableProperty] public partial int AgentMaxRetries { get; set; } = LlmAppSettings.DefaultAgentMaxRetries;

    partial void OnAgentMaxRetriesChanged(int value)
    {
        if (IsWaitingForDraftSync())
        {
            return;
        }

        _current = _current with { AgentMaxRetries = Math.Clamp(value, 0, LlmAppSettings.MaxAgentMaxRetries) };
        MarkDirty("有未保存的更改");
    }

    [ObservableProperty]
    public partial int ToolResultMaxCharacters { get; set; } = LlmAppSettings.DefaultToolResultMaxCharacters;

    partial void OnToolResultMaxCharactersChanged(int value)
    {
        if (IsWaitingForDraftSync())
        {
            return;
        }

        _current = _current with
        {
            ToolResultMaxCharacters = Math.Clamp(value, LlmAppSettings.MinToolResultMaxCharacters,
                LlmAppSettings.MaxToolResultMaxCharacters)
        };
        MarkDirty("有未保存的更改");
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChatProviderPlaceholder))]
    public partial string ChatProviderId { get; set; } = "";

    partial void OnChatProviderIdChanged(string value)
    {
        string providerId = value ?? "";
        if (!IsWaitingForDraftSync())
        {
            _current = _current with { ChatProviderId = providerId };
        }

        RebuildChatModelOptions();
        Raise(nameof(SelectedChatProvider));
        if (!IsWaitingForDraftSync())
        {
            MarkDirty("有未保存的更改");
        }
    }

    [ObservableProperty] public partial string ChatModel { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OcrProviderPlaceholder))]
    public partial string OcrProviderId { get; set; } = "";

    partial void OnOcrProviderIdChanged(string value)
    {
        string providerId = value ?? "";
        if (!IsWaitingForDraftSync())
        {
            _current = _current with { OcrProviderId = providerId };
        }

        RebuildOcrModelOptions();
        Raise(nameof(SelectedOcrProvider));
        if (!IsWaitingForDraftSync())
        {
            MarkDirty("有未保存的更改");
        }
    }

    [ObservableProperty] public partial string OcrModel { get; set; } = "";

    public override bool SupportsEditing => true;

    [ExcludeFromDerivedGeneration] public override bool IsDirty => _isDirty;

    [ExcludeFromDerivedGeneration] public override bool CanSave => _isDirty && !IsSaving;

    public override async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        // Unsaved drafts stay in memory when switching sections; only clean sections re-sync (D4).
        if (!IsDirty)
        {
            SyncFromPersisted();
        }

        await RefreshProviderCredentialsAsync(cancellationToken);
        foreach (LlmProviderSettingsRowViewModel row in Providers.Where(row => row.IsSubscription && row.HasCredential))
        {
            await OnSubscriptionActionAsync(row, "refresh", cancellationToken);
        }

        string? problem = DescribeDraftProblem();
        ValidationState = problem is null ? SettingsValidationState.Valid : SettingsValidationState.Invalid;
        LastError = problem;
        if (problem is not null)
        {
            Status = $"已保存，仍需补全：{problem}";
        }
    }

    public override async Task SaveAsync()
    {
        if (!CanSave)
        {
            return;
        }

        SaveState = SettingsSaveState.Saving;
        Status = "正在保存...";

        // Secrets go to the credential store exactly once, before the non-secret settings are written.
        foreach (LlmProviderSettingsRowViewModel row in Providers)
        {
            if (!await StoreProviderSecretAsync(row))
            {
                return;
            }
        }

        LlmAppSettings draft = _current.Normalize();
        SettingsSaveResult saved = SettingsEqual(draft, _persisted)
            ? SettingsSaveResult.Success
            : await _main.UpdateAppOptionsAsync(
                _main.AppOptions with { Llm = draft }, LlmProviderCatalog.SectionName);
        if (!saved.IsSuccess)
        {
            LastError = saved.ErrorMessage ?? "无法保存 模型连接与聊天设置。";
            SaveState = SettingsSaveState.Failed;
            Status = "保存失败";
            Raise(nameof(IsDirty));
            Raise(nameof(CanSave));
            return;
        }

        _persisted = draft;
        SaveState = SettingsSaveState.Saved;
        _addingProviderId = null;
        RefreshDirtyState();
        await RefreshProviderCredentialsAsync(CancellationToken.None);
        string? validationError = DescribeDraftProblem();
        ValidationState = validationError is null ? SettingsValidationState.Valid : SettingsValidationState.Invalid;
        LastError = validationError;
        Status = validationError is null ? "已保存" : $"已保存，仍需补全：{validationError}";
    }

    public override Task DiscardAsync()
    {
        _isDirty = false;
        SyncFromPersisted();
        LastError = null;
        SaveState = SettingsSaveState.Clean;
        ValidationState = SettingsValidationState.Unknown;
        Status = "已放弃更改";
        return Task.CompletedTask;
    }

    private static async Task<ICredentialStore> ResolveCredentialStoreAsync(MainWindowViewModel main)
    {
        HostServices services = await main.ServicesAsync();
        return services.Credentials;
    }

    /// <summary>Redirects credential reads/writes at a supplied store (test seam).</summary>
    internal void UseCredentialStore(ICredentialStore credentialStore)
    {
        ArgumentNullException.ThrowIfNull(credentialStore);
        _credentialStoreFactory = () => Task.FromResult(credentialStore);
    }

    internal void UseSubscriptionService(ILlmSubscriptionService service, Action<Uri> openBrowser)
    {
        _subscriptionServiceFactory = () => Task.FromResult(service);
        _openSubscriptionBrowser = openBrowser;
    }

    private bool IsWaitingForDraftSync()
    {
        return _isConstructing || _syncingDraft;
    }

    private void BuildProviders()
    {
        Providers.Clear();
        foreach (LlmProviderCatalogEntry entry in LlmProviderCatalog.All)
        {
            LlmProviderSettingsRowViewModel row = new(entry, OnProviderRowChanged, OnRemoveApiKeyAsync,
                OnSubscriptionActionAsync);
            row.RemoveConnectionCommand = new AsyncCommand(() => RemoveConnectionAsync(row));
            Providers.Add(row);
        }
    }

    private async Task RemoveConnectionAsync(LlmProviderSettingsRowViewModel row)
    {
        if (row.HasCredential)
        {
            if (row.IsSubscription)
            {
                await OnSubscriptionActionAsync(row, "logout", CancellationToken.None);
            }
            else
            {
                await OnRemoveApiKeyAsync(row);
            }

            if (row.HasCredential)
            {
                return;
            }
        }

        LlmProviderAppSettings defaults = LlmAppSettings.Default().FindProvider(row.ProviderId)!;
        row.SyncFromSettings(defaults);
        _current = _current.WithProvider(defaults);
        if (_addingProviderId == row.ProviderId)
        {
            _addingProviderId = null;
        }

        RefreshProviderLists();
        MarkDirty("已移除连接，等待自动保存");
    }

    private void AddProvider()
    {
        if (HasPendingProvider || SelectedProviderToAdd is null)
        {
            return;
        }

        _addingProviderId = SelectedProviderToAdd.ProviderId;
        LlmProviderSettingsRowViewModel row = Providers.Single(item => item.ProviderId == _addingProviderId);
        row.IsAdded = true;
        _current = _current.WithProvider(row.ToSettings());
        RefreshProviderLists();
        MarkDirty("已添加连接，等待自动保存");
    }

    private void CancelAddProvider()
    {
        LlmProviderSettingsRowViewModel? row = Providers.FirstOrDefault(item => item.ProviderId == _addingProviderId);
        if (row is not null)
        {
            LlmProviderAppSettings settings = _persisted.FindProvider(row.ProviderId) ??
                                              LlmProviderAppSettings.FromCatalog(row.Entry);
            row.SyncFromSettings(settings);
            _current = _current.WithProvider(settings);
        }

        _addingProviderId = null;
        RefreshProviderLists();
        RefreshDirtyState();
    }

    private void RefreshProviderLists()
    {
        SynchronizeCollection(VisibleProviders,
            Providers.Where(IsProviderVisible));
        Dictionary<string, LlmSelectionOption> existing =
            AvailableProviderOptions.ToDictionary(option => option.ProviderId);
        string? selectedId = SelectedProviderToAdd?.ProviderId;
        SynchronizeCollection(AvailableProviderOptions,
            Providers.Where(row => !IsProviderVisible(row))
                .Select(row => existing.GetValueOrDefault(row.ProviderId) ??
                               new LlmSelectionOption(row.ProviderId, row.DisplayName)));
        SelectedProviderToAdd = AvailableProviderOptions.FirstOrDefault(option => option.ProviderId == selectedId) ??
                                AvailableProviderOptions.FirstOrDefault();
        Raise(nameof(HasPendingProvider));
        Raise(nameof(HasNoProviders));
        AddProviderCommand.NotifyCanExecuteChanged();
        CancelAddProviderCommand.NotifyCanExecuteChanged();
        BuildSelectionOptions();
    }

    private bool IsProviderVisible(LlmProviderSettingsRowViewModel row)
    {
        LlmProviderAppSettings defaults = LlmAppSettings.Default().FindProvider(row.ProviderId)!;
        return row.IsAdded || row.HasCredential || row.ProviderId == _addingProviderId ||
               row.ToSettings() != defaults;
    }

    // Preserve existing controls and keyboard focus while the auto-save pipeline refreshes readiness.
    private static void SynchronizeCollection<T>(ObservableCollection<T> collection, IEnumerable<T> desired)
    {
        T[] items = desired.ToArray();
        for (int index = collection.Count - 1; index >= 0; index--)
        {
            if (!items.Contains(collection[index]))
            {
                collection.RemoveAt(index);
            }
        }

        for (int index = 0; index < items.Length; index++)
        {
            int existingIndex = collection.IndexOf(items[index]);
            if (existingIndex < 0)
            {
                collection.Insert(index, items[index]);
            }
            else if (existingIndex != index)
            {
                collection.Move(existingIndex, index);
            }
        }
    }

    /// <summary>Merges one row draft into the pending settings and marks the section dirty.</summary>
    private void OnProviderRowChanged(LlmProviderSettingsRowViewModel row)
    {
        if (IsWaitingForDraftSync())
        {
            return;
        }

        if (_current.FindProvider(row.ProviderId)?.AuthenticationMode != row.AuthenticationMode)
        {
            _addingProviderId ??= row.ProviderId;
        }

        _current = _current.WithProvider(row.ToSettings());
        row.ApplyReadiness(LlmProviderReadiness.Evaluate(row.Entry, row.ToSettings(), row.HasCredential));
        BuildSelectionOptions();
        MarkDirty("有未保存的更改");
    }

    private async Task OnSubscriptionActionAsync(LlmProviderSettingsRowViewModel row, string action,
        CancellationToken cancellationToken)
    {
        if (!row.IsSubscription)
        {
            return;
        }

        try
        {
            ILlmSubscriptionService service = await _subscriptionServiceFactory();
            row.SubscriptionStatusText = action == "login" ? "请在浏览器中完成订阅登录…" : "正在读取订阅状态…";
            if (action == "login")
            {
                await service.LoginAsync(row.ProviderId, _openSubscriptionBrowser, cancellationToken);
                _main.NotifyLlmCredentialsChanged();
            }
            else if (action == "logout")
            {
                await service.LogoutAsync(row.ProviderId, cancellationToken);
                _main.NotifyLlmCredentialsChanged();
                row.SubscriptionModels.Clear();
                row.SubscriptionStatusText = "已退出订阅登录";
                row.MarkCredentialRemoved();
                OnProviderRowChanged(row);
                _addingProviderId = row.ProviderId;
                RefreshProviderLists();
                return;
            }

            LlmSubscriptionAccount? account = await service.GetAccountAsync(row.ProviderId, cancellationToken);
            if (account is null)
            {
                row.SubscriptionStatusText = "尚未登录订阅";
                return;
            }

            IReadOnlyList<LlmSubscriptionModel> models =
                await service.ListModelsAsync(row.ProviderId, cancellationToken);
            SynchronizeCollection(row.SubscriptionModels, models.Select(model => model.Id));
            if (!models.Any(model => model.Id == row.Model))
            {
                row.Model = models.FirstOrDefault(model => model.IsDefault)?.Id ?? models.FirstOrDefault()?.Id ?? "";
            }

            row.SubscriptionStatusText = $"{account.Email} · {account.Plan} · {models.Count} 个可用模型";
            if (ChatProviderId == row.ProviderId && !models.Any(model => model.Id == ChatModel))
            {
                ChatModel = row.Model;
            }

            RebuildChatModelOptions();

            await RefreshProviderCredentialsAsync(cancellationToken);
            OnProviderRowChanged(row);
        }
        catch (OperationCanceledException)
        {
            row.SubscriptionStatusText = "已取消订阅操作";
        }
        catch (Exception)
        {
            row.SubscriptionStatusText = "订阅操作失败，请检查网络、重新登录或刷新模型。";
        }
    }

    private async Task OnRemoveApiKeyAsync(LlmProviderSettingsRowViewModel row)
    {
        try
        {
            ICredentialStore store = await _credentialStoreFactory();
            Result removed = await store.RemoveAsync(row.ProviderId, CancellationToken.None);
            if (removed.IsFailure)
            {
                LastError = removed.ErrorMessage ?? $"无法移除 {row.DisplayName} 的 API key。";
                Status = "移除凭据失败";
                SaveState = SettingsSaveState.Failed;
                return;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LastError = $"无法移除 {row.DisplayName} 的 API key：{exception.Message}";
            Status = "移除凭据失败";
            SaveState = SettingsSaveState.Failed;
            return;
        }

        row.MarkCredentialRemoved();
        _main.NotifyLlmCredentialsChanged();
        OnProviderRowChanged(row);
        RefreshProviderLists();
        Status = $"{row.DisplayName} 的 API key 已移除。";
    }

    private async Task<bool> StoreProviderSecretAsync(LlmProviderSettingsRowViewModel row)
    {
        if (row.IsSubscription)
        {
            return true;
        }

        string secret = row.ApiKeyInput.Trim();
        if (!row.HasPendingApiKey)
        {
            return true;
        }

        try
        {
            ICredentialStore store = await _credentialStoreFactory();
            Result<ProviderCredentialMetadata> saved = await store.SaveAsync(row.ProviderId, row.DisplayName, secret,
                CancellationToken.None);
            if (saved.IsFailure)
            {
                LastError = saved.ErrorMessage ?? $"无法保存 {row.DisplayName} 的 API key。";
                SaveState = SettingsSaveState.Failed;
                Status = "保存失败";
                Raise(nameof(IsDirty));
                Raise(nameof(CanSave));
                return false;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LastError = $"无法保存 {row.DisplayName} 的 API key：{exception.Message}";
            SaveState = SettingsSaveState.Failed;
            Status = "保存失败";
            Raise(nameof(IsDirty));
            Raise(nameof(CanSave));
            return false;
        }

        // Record the accepted baseline without replacing a newer edit made during the write.
        row.AcceptStoredSecret(secret);
        _main.NotifyLlmCredentialsChanged();
        return true;
    }

    /// <summary>Refreshes readiness and loads saved API keys into their masked inputs.</summary>
    private async Task RefreshProviderCredentialsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<LlmProviderReadiness> readiness;
        ICredentialStore store;
        try
        {
            store = await _credentialStoreFactory();
            readiness = await LlmProviderClientFactory.InspectAsync(_current, store, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            Status = $"读取凭据状态失败：{exception.Message}";
            return;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        Dictionary<string, LlmProviderReadiness> byProviderId = readiness
            .ToDictionary(item => item.ProviderId, StringComparer.OrdinalIgnoreCase);
        foreach (LlmProviderSettingsRowViewModel row in Providers)
        {
            row.ApplyReadiness(byProviderId.GetValueOrDefault(row.ProviderId));
            if (!row.IsSubscription)
            {
                Result<string> savedSecret = row.HasCredential
                    ? await store.GetActiveSecretForProviderAsync(row.ProviderId, cancellationToken)
                    : Result<string>.Success("");
                row.LoadStoredSecret(savedSecret.IsSuccess ? savedSecret.Value : "");
            }
        }

        if (Providers.Any(row => row.ProviderId == _addingProviderId && row.Readiness?.IsConfigured == true))
        {
            _addingProviderId = null;
        }

        RefreshProviderLists();
    }

    private void SyncFromPersisted()
    {
        _addingProviderId = null;
        _syncingDraft = true;
        try
        {
            _current = _persisted;

            foreach (LlmProviderSettingsRowViewModel row in Providers)
            {
                row.SyncFromSettings(_persisted.FindProvider(row.ProviderId) ??
                                     LlmProviderAppSettings.FromCatalog(row.Entry));
            }

            RefreshProviderLists();
            (string chatProviderId, string chatModel) = _persisted.ChatSelection;
            (string ocrProviderId, string ocrModel) = _persisted.OcrSelection;
            ChatProviderId = chatProviderId;
            ChatModel = chatModel;
            OcrProviderId = ocrProviderId;
            OcrModel = ocrModel;
            AgentMaxRetries = _persisted.AgentMaxRetries;
            ToolResultMaxCharacters = _persisted.ToolResultMaxCharacters;
            RebuildChatModelOptions();
            RebuildOcrModelOptions();
            _isDirty = false;
            Raise(nameof(IsDirty));
            Raise(nameof(CanSave));
        }
        finally
        {
            _syncingDraft = false;
        }
    }

    private void BuildSelectionOptions()
    {
        bool wasSyncing = _syncingDraft;
        string chatId = ChatProviderId;
        string chatModel = ChatModel;
        string ocrId = OcrProviderId;
        string ocrModel = OcrModel;
        _syncingDraft = true;
        try
        {
            FillOptions(ChatProviderOptions);
            FillOptions(OcrProviderOptions, true);
            ChatProviderId = chatId;
            ChatModel = chatModel;
            OcrProviderId = ocrId;
            OcrModel = ocrModel;
        }
        finally
        {
            _syncingDraft = wasSyncing;
        }

        Raise(nameof(SelectedChatProvider));
        Raise(nameof(SelectedOcrProvider));
    }

    private void FillOptions(ObservableCollection<LlmSelectionOption> options, bool requireVision = false)
    {
        Dictionary<string, LlmSelectionOption> existing = options.ToDictionary(option => option.ProviderId);
        SynchronizeCollection(options, Providers.Where(row => row.Readiness?.IsConfigured == true &&
                                                              (!requireVision || row.Readiness.SupportsVision))
            .Select(row => existing.GetValueOrDefault(row.ProviderId) ??
                           new LlmSelectionOption(row.ProviderId, row.DisplayName)));
    }

    private void RebuildChatModelOptions()
    {
        FillModelOptions(ChatModelOptions, ChatProviderId, ChatModel);
    }

    private void RebuildOcrModelOptions()
    {
        FillModelOptions(OcrModelOptions, OcrProviderId, OcrModel);
    }

    /// <summary>Model ids already known for the provider: its configured model plus a blank row so a model
    /// typed by hand is accepted by the editable combo box.</summary>
    private void FillModelOptions(ObservableCollection<string> options, string providerId, string selectedModel)
    {
        options.Clear();
        options.Add("");
        LlmProviderAppSettings? row = _current.FindProvider(providerId);
        if (row is not null && !string.IsNullOrWhiteSpace(row.Model))
        {
            options.Add(row.Model.Trim());
        }

        LlmProviderSettingsRowViewModel? providerRow =
            Providers.FirstOrDefault(provider => provider.ProviderId == providerId);
        if (providerRow?.IsSubscription == true)
        {
            foreach (string model in providerRow.SubscriptionModels.Where(model => !options.Contains(model)))
            {
                options.Add(model);
            }
        }

        if (!string.IsNullOrWhiteSpace(selectedModel) && !options.Contains(selectedModel.Trim()))
        {
            options.Add(selectedModel.Trim());
        }
    }

    private string? DescribeDraftProblem()
    {
        Dictionary<string, LlmProviderReadiness> readiness = Providers
            .Where(row => row.Readiness is not null)
            .ToDictionary(row => row.ProviderId, row => row.Readiness!, StringComparer.OrdinalIgnoreCase);
        if (readiness.Count == 0)
        {
            return null;
        }

        List<string> problems = [];
        foreach (LlmProviderSettingsRowViewModel row in VisibleProviders.Where(row =>
                     row.Readiness?.IsConfigured == false))
        {
            problems.Add($"{row.DisplayName}：{row.DiagnosticText}");
        }

        if (!string.IsNullOrWhiteSpace(ChatProviderId) &&
            readiness.TryGetValue(ChatProviderId, out LlmProviderReadiness? chat)
            && !chat.IsConfigured)
        {
            problems.Add($"聊天默认 provider「{chat.DisplayName}」尚未配置完成：{chat.Diagnostic}");
        }

        if (!string.IsNullOrWhiteSpace(OcrProviderId) &&
            readiness.TryGetValue(OcrProviderId, out LlmProviderReadiness? ocr) && !ocr.IsConfigured)
        {
            problems.Add($"OCR 默认 provider「{ocr.DisplayName}」尚未配置完成：{ocr.Diagnostic}");
        }

        return problems.Count == 0 ? null : string.Join(" ", problems);
    }

    private void MarkDirty(string message)
    {
        RefreshDirtyState();
        if (!_isDirty)
        {
            return;
        }

        LastError = null;
        SaveState = SettingsSaveState.Dirty;
        Status = message;
        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
    }

    private static bool SettingsEqual(LlmAppSettings first, LlmAppSettings second)
    {
        LlmAppSettings normalizedFirst = first.Normalize();
        LlmAppSettings normalizedSecond = second.Normalize();
        return normalizedFirst.Providers.SequenceEqual(normalizedSecond.Providers) &&
               normalizedFirst with { Providers = normalizedSecond.Providers } == normalizedSecond;
    }

    private void RefreshDirtyState()
    {
        _isDirty = !SettingsEqual(_current, _persisted) ||
                   Providers.Any(row => row.HasPendingApiKey);
        if (!_isDirty && SaveState is SettingsSaveState.Dirty or SettingsSaveState.Failed)
        {
            LastError = null;
            SaveState = SettingsSaveState.Clean;
            Status = "无未保存的更改";
        }
        else if (_isDirty && SaveState == SettingsSaveState.Saved)
        {
            SaveState = SettingsSaveState.Dirty;
            Status = "有未保存的更改";
        }

        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
    }

    partial void OnChatModelChanged(string value)
    {
        if (IsWaitingForDraftSync())
        {
            return;
        }

        _current = _current with { ChatModel = value ?? "" };
        MarkDirty("有未保存的更改");
    }

    partial void OnOcrModelChanged(string value)
    {
        if (IsWaitingForDraftSync())
        {
            return;
        }

        _current = _current with { OcrModel = value ?? "" };
        MarkDirty("有未保存的更改");
    }
}

/// <summary>One catalog provider row of the LLM form. The shape is identical for every provider; the extra
/// fields are driven by <see cref="LlmProviderCatalogEntry.RequiresSubscription"/> and
/// <see cref="LlmProviderCatalogEntry.RequiresDeployment"/> rather than by a provider id.</summary>
public sealed partial class LlmProviderSettingsRowViewModel : ViewModelBase
{
    private readonly Action<LlmProviderSettingsRowViewModel> _onChanged;
    private readonly Func<LlmProviderSettingsRowViewModel, Task> _onRemoveApiKey;
    private readonly Func<LlmProviderSettingsRowViewModel, string, CancellationToken, Task> _onSubscriptionAction;

    private bool _syncingDraft;
    private bool _hasCredential;
    private string _storedApiKey = "";

    internal LlmProviderSettingsRowViewModel(LlmProviderCatalogEntry entry,
        Action<LlmProviderSettingsRowViewModel> onChanged,
        Func<LlmProviderSettingsRowViewModel, Task> onRemoveApiKey,
        Func<LlmProviderSettingsRowViewModel, string, CancellationToken, Task> onSubscriptionAction)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(onChanged);
        ArgumentNullException.ThrowIfNull(onRemoveApiKey);

        Entry = entry;
        _onChanged = onChanged;
        _onRemoveApiKey = onRemoveApiKey;
        _onSubscriptionAction = onSubscriptionAction;
        RemoveApiKeyCommand = new AsyncCommand(() => _onRemoveApiKey(this));
        LoginSubscriptionCommand = new AsyncCommand(ct => _onSubscriptionAction(this, "login", ct));
        RefreshSubscriptionModelsCommand = new AsyncCommand(ct => _onSubscriptionAction(this, "refresh", ct));
        LogoutSubscriptionCommand = new AsyncCommand(ct => _onSubscriptionAction(this, "logout", ct));
        CancelSubscriptionLoginCommand = new ToolkitRelayCommand(LoginSubscriptionCommand.Cancel);
        LoginSubscriptionCommand.PropertyChanged += (_, _) => Raise(nameof(IsSubscriptionBusy));
        RefreshSubscriptionModelsCommand.PropertyChanged += (_, _) => Raise(nameof(IsSubscriptionBusy));
        LogoutSubscriptionCommand.PropertyChanged += (_, _) => Raise(nameof(IsSubscriptionBusy));
        SubscriptionModels.CollectionChanged += (_, _) => Raise(nameof(SelectedSubscriptionModel));
        if (LlmSubscriptionCatalog.Find(entry.ProviderId) is { } subscription)
        {
            AuthenticationOptions.Add(new LlmSelectionOption(LlmAuthenticationModes.Subscription,
                subscription.DisplayName));
        }
        else
        {
            AuthenticationOptions.Add(new LlmSelectionOption(LlmAuthenticationModes.ApiKey, "API key"));
        }
    }

    public LlmProviderCatalogEntry Entry { get; }

    [ExcludeFromDerivedGeneration] public string ProviderId => Entry.ProviderId;

    [ExcludeFromDerivedGeneration] public string DisplayName => Entry.DisplayName;

    /// <summary>The catalog default endpoint, shown as the field default; empty means the vendor default.</summary>
    [ExcludeFromDerivedGeneration]
    public string DefaultBaseUrl => Entry.DefaultBaseUrl;

    /// <summary>True when the catalog entry cannot be called without an explicit endpoint.</summary>
    [ExcludeFromDerivedGeneration]
    public bool RequiresBaseUrl => Entry.RequiresBaseUrl;

    /// <summary>Azure OpenAI: subscription, deployment and api-version fields are shown for this row only.</summary>
    [ExcludeFromDerivedGeneration]
    public bool IsAzure => Entry.IsAzure;

    [ExcludeFromDerivedGeneration] public bool IsAzureSubscriptionRequired => Entry.RequiresSubscription;

    [ExcludeFromDerivedGeneration] public bool IsAzureDeploymentRequired => Entry.RequiresDeployment;

    [ExcludeFromDerivedGeneration]
    public string ModelWatermark => string.IsNullOrWhiteSpace(Entry.DefaultBaseUrl) ? "模型名称" : "模型名称（可选）";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedSubscriptionModel))]
    public partial string Model { get; set; } = "";

    [ObservableProperty]
    public partial int ContextWindowTokens { get; set; } = LlmProviderAppSettings.DefaultContextWindowTokens;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSubscription))]
    [NotifyPropertyChangedFor(nameof(CredentialStatusText))]
    [NotifyPropertyChangedFor(nameof(SelectedAuthenticationOption))]
    public partial string AuthenticationMode { get; set; } = LlmAuthenticationModes.ApiKey;

    /// <summary>Only an actual supported selection may change authentication and reset its model.</summary>
    [ExcludeFromDerivedGeneration]
    public LlmSelectionOption? SelectedAuthenticationOption
    {
        get => AuthenticationOptions.FirstOrDefault(option => option.ProviderId == AuthenticationMode);
        set
        {
            if (value is not null && AuthenticationOptions.Contains(value))
            {
                AuthenticationMode = value.ProviderId;
            }
        }
    }

    /// <summary>Selection changes from inactive or rebuilding controls must not erase the configured model.</summary>
    [ExcludeFromDerivedGeneration]
    public string? SelectedSubscriptionModel
    {
        get => IsSubscription && SubscriptionModels.Contains(Model) ? Model : null;
        set
        {
            if (IsSubscription && value is not null && SubscriptionModels.Contains(value))
            {
                Model = value;
            }
        }
    }

    [ExcludeFromDerivedGeneration]
    public bool SupportsSubscription => LlmSubscriptionCatalog.Find(ProviderId) is not null;

    [ExcludeFromDerivedGeneration]
    public bool IsSubscription => AuthenticationMode == LlmAuthenticationModes.Subscription;

    [ExcludeFromDerivedGeneration]
    public bool IsSubscriptionBusy => LoginSubscriptionCommand.IsRunning ||
                                      RefreshSubscriptionModelsCommand.IsRunning || LogoutSubscriptionCommand.IsRunning;

    [ExcludeFromDerivedGeneration] public ObservableCollection<LlmSelectionOption> AuthenticationOptions { get; } = [];
    [ExcludeFromDerivedGeneration] public ObservableCollection<string> SubscriptionModels { get; } = [];
    [ObservableProperty] public partial string SubscriptionStatusText { get; set; } = "尚未登录订阅";

    public AsyncCommand LoginSubscriptionCommand { get; }
    public AsyncCommand RefreshSubscriptionModelsCommand { get; }
    public AsyncCommand LogoutSubscriptionCommand { get; }
    public ToolkitRelayCommand CancelSubscriptionLoginCommand { get; }

    [ObservableProperty] public partial string BaseUrl { get; set; } = "";

    /// <summary>Masked input containing the saved API key or a replacement draft.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CredentialStatusText))]
    public partial string ApiKeyInput { get; set; } = "";

    [ObservableProperty] public partial string Subscription { get; set; } = "";

    [ObservableProperty] public partial string Deployment { get; set; } = "";

    [ObservableProperty] public partial string ApiVersion { get; set; } = "";

    [ExcludeFromDerivedGeneration] public LlmProviderReadiness? Readiness { get; private set; }

    /// <summary>True when a credential for this provider exists in the credential store.</summary>
    [ExcludeFromDerivedGeneration]
    public bool HasCredential => _hasCredential;

    [ExcludeFromDerivedGeneration] public string ApiKeyPlaceholder => "输入后保存写入本机凭据存储";

    [ExcludeFromDerivedGeneration]
    internal bool HasPendingApiKey => !IsSubscription && !string.IsNullOrWhiteSpace(ApiKeyInput) &&
                                      !string.Equals(ApiKeyInput.Trim(), _storedApiKey, StringComparison.Ordinal);

    /// <summary>The only credential information the form ever shows: configured or not, never the secret.</summary>
    [ExcludeFromDerivedGeneration]
    public string CredentialStatusText => IsSubscription
        ? _hasCredential ? "已登录订阅" : "尚未登录订阅"
        : HasPendingApiKey
            ? "保存后更新 API key"
            : _hasCredential
                ? "已配置 API key"
                : "未配置 API key";

    [ExcludeFromDerivedGeneration] public string DiagnosticText => Readiness?.Diagnostic ?? "";

    public AsyncCommand RemoveApiKeyCommand { get; }

    public AsyncCommand RemoveConnectionCommand { get; internal set; } = null!;

    internal LlmProviderAppSettings ToSettings()
    {
        return new LlmProviderAppSettings(ProviderId, DisplayName, BaseUrl.Trim(), Model.Trim(),
                Subscription.Trim(), Deployment.Trim(), ApiVersion.Trim(), AuthenticationMode)
            { ContextWindowTokens = Math.Max(4096, ContextWindowTokens), IsAdded = IsAdded };
    }

    internal bool IsAdded { get; set; }

    internal void SyncFromSettings(LlmProviderAppSettings settings)
    {
        _syncingDraft = true;
        try
        {
            IsAdded = settings.IsAdded;
            Model = settings.Model;
            ContextWindowTokens = settings.ContextWindowTokens;
            BaseUrl = settings.BaseUrl;
            ApiKeyInput = _storedApiKey;
            Subscription = settings.Subscription;
            Deployment = settings.Deployment;
            ApiVersion = settings.ApiVersion;
            AuthenticationMode = settings.AuthenticationMode;
        }
        finally
        {
            _syncingDraft = false;
        }
    }

    internal void ApplyReadiness(LlmProviderReadiness? readiness)
    {
        Readiness = readiness;
        _hasCredential = readiness?.HasCredential ?? false;
        Raise(nameof(Readiness));
        Raise(nameof(HasCredential));
        Raise(nameof(ApiKeyPlaceholder));
        Raise(nameof(CredentialStatusText));
        Raise(nameof(DiagnosticText));
    }

    internal void LoadStoredSecret(string secret)
    {
        bool hasPendingEdit = HasPendingApiKey;
        _storedApiKey = secret;
        _syncingDraft = true;
        try
        {
            if (!hasPendingEdit)
            {
                ApiKeyInput = secret;
            }
        }
        finally
        {
            _syncingDraft = false;
        }

        Raise(nameof(CredentialStatusText));
    }

    /// <summary>Accepts the saved baseline while retaining any newer input.</summary>
    internal void AcceptStoredSecret(string storedSecret)
    {
        _syncingDraft = true;
        try
        {
            _storedApiKey = storedSecret;
            if (ApiKeyInput.Trim() == storedSecret)
            {
                ApiKeyInput = storedSecret;
            }
        }
        finally
        {
            _syncingDraft = false;
        }

        _hasCredential = true;
        Raise(nameof(HasCredential));
        Raise(nameof(ApiKeyPlaceholder));
        Raise(nameof(CredentialStatusText));
    }

    internal void MarkCredentialRemoved()
    {
        _hasCredential = false;
        _storedApiKey = "";
        ApiKeyInput = "";
        Raise(nameof(HasCredential));
        Raise(nameof(ApiKeyPlaceholder));
        Raise(nameof(CredentialStatusText));
    }

    partial void OnModelChanged(string value)
    {
        NotifyDraftChanged();
    }

    partial void OnContextWindowTokensChanged(int value)
    {
        NotifyDraftChanged();
    }

    partial void OnAuthenticationModeChanged(string value)
    {
        if (_syncingDraft)
        {
            return;
        }

        ApiKeyInput = "";
        Model = "";
        ApplyReadiness(null);
        SubscriptionModels.Clear();
        NotifyDraftChanged();
    }

    partial void OnBaseUrlChanged(string value)
    {
        NotifyDraftChanged();
    }

    partial void OnApiKeyInputChanged(string value)
    {
        NotifyDraftChanged();
    }

    partial void OnSubscriptionChanged(string value)
    {
        NotifyDraftChanged();
    }

    partial void OnDeploymentChanged(string value)
    {
        NotifyDraftChanged();
    }

    partial void OnApiVersionChanged(string value)
    {
        NotifyDraftChanged();
    }

    private void NotifyDraftChanged()
    {
        if (_syncingDraft)
        {
            return;
        }

        _onChanged(this);
    }
}

/// <summary>Provider choice for the chat/OCR default selectors.</summary>
public sealed class LlmSelectionOption
{
    public LlmSelectionOption(string providerId, string displayName)
    {
        ProviderId = providerId;
        DisplayName = displayName;
    }

    public string ProviderId { get; }

    public string DisplayName { get; }
}
