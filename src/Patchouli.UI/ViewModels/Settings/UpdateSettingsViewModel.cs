using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Results;
using Patchouli.Core.Settings;
using Patchouli.Host.Composition;
using Patchouli.UI.Diagnostics;
using Patchouli.UI.ViewModels;

namespace Patchouli.UI.ViewModels.Settings;

/// <summary>
/// Issue #15 「应用更新」设置节：自动检查更新开关（设备本地设置，修改后立即
/// 持久化）与上次检查时间展示。关闭后不再做后台自动检查；手动「检查更新」
/// 不受该开关影响。
/// </summary>
public sealed partial class UpdateSettingsViewModel : SettingsSectionViewModelBase
{
    private readonly MainWindowViewModel _main;
    private bool _isConstructing = true;
    private bool _isSyncing;

    public UpdateSettingsViewModel(MainWindowViewModel main)
    {
        _main = main;
        _isConstructing = false;
    }

    [ObservableProperty] public partial bool AutoCheckEnabled { get; set; } = true;

    [ObservableProperty] public partial string LastCheckText { get; private set; } = "尚未检查";

    partial void OnAutoCheckEnabledChanged(bool value)
    {
        if (_isConstructing || _isSyncing)
        {
            return;
        }

        // Device-local system setting: persist immediately like the file
        // handling behavior settings rather than going through auto-save.
        SaveNowAsync().Observe("settings", "save-update-check-settings");
    }

    public override bool SupportsEditing => true;
    public override bool IsDirty => false;
    public override bool CanSave => false;

    public override async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            HostServices services = await _main.ServicesAsync();
            Result<UpdateCheckSettings> result =
                await services.UpdateCheckSettings.GetSettingsAsync(cancellationToken);
            if (result.IsFailure)
            {
                SetStatus($"读取更新设置失败：{result.ErrorMessage}");
                return;
            }

            SyncFromSettings(result.Value);
            SetStatus(string.Empty);
        }
        catch (Exception exception)
        {
            SetStatus($"读取更新设置失败：{exception.Message}");
        }
    }

    public override Task SaveAsync()
    {
        return Task.CompletedTask;
    }

    public override Task DiscardAsync()
    {
        return LoadAsync();
    }

    /// <summary>Opens the update check flow manually (same pipeline as the menu entry).</summary>
    public AsyncCommand CheckForUpdatesCommand => _main.CheckForUpdatesCommand;

    private async Task SaveNowAsync()
    {
        SaveState = SettingsSaveState.Saving;
        SetStatus("正在保存…");
        try
        {
            HostServices services = await _main.ServicesAsync();
            // SaveAutoCheckAsync holds the settings-file write gate across
            // the read-modify-write, so a concurrent RecordCheckAsync from an
            // update check cannot interleave and clobber the flag.
            Result<UpdateCheckSettings> saved =
                await services.UpdateCheckSettings.SaveAutoCheckAsync(AutoCheckEnabled);
            if (saved.IsFailure)
            {
                LastError = saved.ErrorMessage ?? "保存失败。";
                SaveState = SettingsSaveState.Failed;
                SetStatus($"保存失败：{LastError}");
                return;
            }

            LastError = null;
            SaveState = SettingsSaveState.Saved;
            SyncLastCheckText(saved.Value.LastCheckUtc);
            SetStatus("已保存。");
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            SaveState = SettingsSaveState.Failed;
            SetStatus($"保存失败：{exception.Message}");
        }
    }

    private void SyncFromSettings(UpdateCheckSettings settings)
    {
        _isSyncing = true;
        try
        {
            AutoCheckEnabled = settings.AutoCheckEnabled;
            SyncLastCheckText(settings.LastCheckUtc);
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private void SyncLastCheckText(DateTimeOffset? lastCheckUtc)
    {
        LastCheckText = lastCheckUtc.HasValue
            ? $"上次检查：{lastCheckUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm}"
            : "尚未检查";
    }

    private void SetStatus(string message)
    {
        Status = message;
    }
}
