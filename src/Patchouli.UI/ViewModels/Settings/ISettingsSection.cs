using System.Threading.Tasks;

namespace Patchouli.UI.ViewModels.Settings;

public interface ISettingsSection
{
    SettingsSaveState SaveState => SaveStateText.StartsWith("正在保存", StringComparison.Ordinal)
        ? SettingsSaveState.Saving
        : LastError is not null || SaveStateText.Contains("失败", StringComparison.Ordinal)
            ? SettingsSaveState.Failed
            : IsDirty
                ? SettingsSaveState.Dirty
                : SaveStateText.StartsWith("已保存", StringComparison.Ordinal)
                    ? SettingsSaveState.Saved
                    : SettingsSaveState.Clean;

    SettingsValidationState ValidationState =>
        LastError is null ? SettingsValidationState.Valid : SettingsValidationState.Invalid;

    bool IsSaving => SaveStateText.StartsWith("正在保存", StringComparison.Ordinal);
    bool RequiresReload => false;
    bool SupportsEditing { get; }
    bool IsDirty { get; }
    bool CanSave { get; }
    string SaveStateText { get; }
    string? LastError { get; }

    Task SaveAsync();
    Task DiscardAsync();
}

public enum SettingsSaveState
{
    Clean,
    Dirty,
    Saving,
    Saved,
    Failed
}

public enum SettingsValidationState
{
    Unknown,
    Valid,
    Invalid
}
