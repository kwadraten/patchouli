using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Credentials;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.UI.Diagnostics;

namespace Patchouli.UI.ViewModels.Settings;

/// <summary>
/// The MinerU engine-row configuration dialog: real model version, polling timeout and masked
/// credential backed by the existing <see cref="MainWindowViewModel"/> MinerU persistence paths.
/// MinerU is built-in and has no extension package, so this dialog deliberately exposes no FSX
/// source surface. The <see cref="HostServices"/> and library generation captured at load time
/// guard every read/apply — a library switch while the dialog is open makes edits and saves
/// report honestly instead of persisting into the new context.
/// </summary>
public sealed partial class MinerUConfigurationDialogViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private HostServices? _services;
    private int _libraryGeneration;
    private int _editVersion;
    private string _persistedToken = "";
    private string _persistedModelVersion;
    private int _persistedPollingTimeoutSeconds;
    private bool _isSyncing;

    public MinerUConfigurationDialogViewModel(MainWindowViewModel main)
    {
        ArgumentNullException.ThrowIfNull(main);
        _main = main;
        _persistedModelVersion = NormalizeModelVersion(main.AppOptions.MinerU.ModelVersion);
        _persistedPollingTimeoutSeconds = main.AppOptions.MinerU.PollingTimeoutSeconds;
        MinerUModelVersion = _persistedModelVersion;
        MinerUPollingTimeoutSeconds = _persistedPollingTimeoutSeconds;
        SaveCommand = new AsyncCommand(SaveAsync);
        RemoveCredentialCommand = new AsyncCommand(RemoveCredentialAsync);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MinerUCredentialStatus))]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    public partial string MinerUTokenInput { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    public partial string MinerUModelVersion { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    public partial int MinerUPollingTimeoutSeconds { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty] public partial string Status { get; set; } = "";

    [ObservableProperty] public partial string? LastError { get; set; }

    public IReadOnlyList<string> MinerUModelVersionOptions { get; } =
        new ReadOnlyCollection<string>(["vlm", "pipeline"]);

    /// <summary>Persisted credential fact — independent of whatever the input currently shows.</summary>
    [ExcludeFromDerivedGeneration]
    public bool HasPersistedCredential => !string.IsNullOrWhiteSpace(_persistedToken);

    /// <summary>Credential display: the persisted fact plus a pending-replacement hint.</summary>
    [ExcludeFromDerivedGeneration]
    public string MinerUCredentialStatus =>
        (HasPersistedCredential ? "已配置 ProviderCredential" : "未配置 ProviderCredential") +
        (MinerUTokenInput.Trim().Length > 0 && MinerUTokenInput.Trim() != _persistedToken
            ? " · 已输入新 token，保存后生效"
            : "");

    [ExcludeFromDerivedGeneration] public bool CanEdit => !IsBusy;

    [ExcludeFromDerivedGeneration]
    public bool IsDirty => MinerUTokenInput.Trim() != _persistedToken ||
                           MinerUModelVersion != _persistedModelVersion ||
                           MinerUPollingTimeoutSeconds != _persistedPollingTimeoutSeconds;

    [ExcludeFromDerivedGeneration] public bool CanSave => IsDirty && !IsBusy;

    public AsyncCommand SaveCommand { get; }
    public AsyncCommand RemoveCredentialCommand { get; }

    /// <summary>Closing signal for the dialog window: true after a successful save.</summary>
    public Action<bool>? RequestClose { get; set; }

    [ExcludeFromDerivedGeneration]
    private bool IsStaleContext =>
        _services is not null && !_main.IsCurrentLibraryContext(_services, _libraryGeneration);

    public async Task LoadAsync()
    {
        HostServices services = await _main.ServicesAsync();
        // Credential reads stay off the dispatcher; SQLite async often completes synchronously.
        Result<string> secret =
            await Task.Run(() => services.Credentials.GetActiveSecretForProviderAsync(ProviderIds.MinerU));
        _services = services;
        _libraryGeneration = _main.LibraryGeneration;
        if (IsStaleContext)
        {
            Status = "书库已切换，请重新打开 MinerU 配置。";
            return;
        }

        string token = secret.IsSuccess ? secret.Value : "";
        _isSyncing = true;
        try
        {
            _persistedToken = token;
            MinerUTokenInput = token;
            _persistedModelVersion = NormalizeModelVersion(_main.AppOptions.MinerU.ModelVersion);
            MinerUModelVersion = _persistedModelVersion;
            _persistedPollingTimeoutSeconds = _main.AppOptions.MinerU.PollingTimeoutSeconds;
            MinerUPollingTimeoutSeconds = _persistedPollingTimeoutSeconds;
        }
        finally
        {
            _isSyncing = false;
        }

        Raise(nameof(HasPersistedCredential));
        Raise(nameof(MinerUCredentialStatus));
        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
    }

    partial void OnMinerUTokenInputChanged(string value)
    {
        if (!_isSyncing)
        {
            _editVersion++;
        }
    }

    partial void OnMinerUModelVersionChanged(string value)
    {
        if (!_isSyncing)
        {
            _editVersion++;
        }
    }

    partial void OnMinerUPollingTimeoutSecondsChanged(int value)
    {
        if (_isSyncing)
        {
            return;
        }

        _editVersion++;
        int clamped = Math.Clamp(value, 30, 3600);
        if (clamped != value)
        {
            _isSyncing = true;
            MinerUPollingTimeoutSeconds = clamped;
            _isSyncing = false;
        }
    }

    private async Task SaveAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (IsStaleContext)
        {
            LastError = "书库已切换；请重新打开 MinerU 配置窗口后再保存。";
            Status = "无法保存";
            return;
        }

        IsBusy = true;
        LastError = null;
        int editVersion = _editVersion;
        try
        {
            // An empty token field never authorizes a credential removal: model-only saves keep
            // the persisted secret; only the explicit remove command deletes it.
            string token = MinerUTokenInput.Trim();
            string modelVersion = NormalizeModelVersion(MinerUModelVersion);
            int pollingTimeout = Math.Clamp(MinerUPollingTimeoutSeconds, 30, 3600);
            bool saved = token.Length > 0
                ? await _main.SaveMinerUSettingsAsync(token, modelVersion, pollingTimeout)
                : await _main.SaveMinerUModelSettingsAsync(modelVersion, pollingTimeout);
            if (!saved)
            {
                LastError = "MinerU 设置保存失败。";
                Status = "保存失败";
                return;
            }

            // Persisted facts update unconditionally — the host call already committed them;
            // token facts only change when a token was actually supplied.
            _persistedModelVersion = modelVersion;
            _persistedPollingTimeoutSeconds = pollingTimeout;
            if (token.Length > 0)
            {
                _persistedToken = token;
            }

            if (IsStaleContext)
            {
                Status = "已保存；书库已切换，请重新打开窗口确认。";
                return;
            }

            if (_editVersion == editVersion)
            {
                _isSyncing = true;
                // An empty-token model-only save keeps the stored secret — mirror the real
                // persisted credential back into the masked input instead of leaving it blank.
                MinerUTokenInput = _persistedToken;
                MinerUModelVersion = modelVersion;
                MinerUPollingTimeoutSeconds = pollingTimeout;
                _isSyncing = false;
                Status = "已保存";
                RequestClose?.Invoke(true);
            }
            else
            {
                Status = "已保存；检测到保存期间的新修改，请再次保存。";
            }

            Raise(nameof(HasPersistedCredential));
            Raise(nameof(MinerUCredentialStatus));
        }
        catch (Exception exception)
        {
            UnexpectedExceptions.Sink.Report(exception, "mineru-config-dialog", nameof(SaveAsync));
            LastError = exception.Message;
            Status = "保存失败：" + exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RemoveCredentialAsync()
    {
        if (!HasPersistedCredential || IsBusy)
        {
            return;
        }

        if (IsStaleContext)
        {
            LastError = "书库已切换；请重新打开 MinerU 配置窗口后再移除凭据。";
            Status = "无法移除凭据";
            return;
        }

        IsBusy = true;
        LastError = null;
        int editVersion = _editVersion;
        try
        {
            // The host path runs its own confirmation dialog; a declined removal is not an error.
            bool removed = await _main.RemoveMinerUCredentialAsync();
            if (!removed)
            {
                Status = "未移除凭据。";
                return;
            }

            _persistedToken = "";
            if (IsStaleContext)
            {
                Status = "MinerU 凭据已移除；书库已切换，请重新打开窗口确认。";
                return;
            }

            if (_editVersion == editVersion)
            {
                _isSyncing = true;
                MinerUTokenInput = "";
                _isSyncing = false;
            }

            Status = "MinerU 凭据已移除。";
            Raise(nameof(HasPersistedCredential));
            Raise(nameof(MinerUCredentialStatus));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string NormalizeModelVersion(string? value)
    {
        return string.Equals(value, "pipeline", StringComparison.OrdinalIgnoreCase) ? "pipeline" : "vlm";
    }
}
