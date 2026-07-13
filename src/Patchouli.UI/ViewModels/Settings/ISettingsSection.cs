using System.Threading.Tasks;

namespace Patchouli.UI.ViewModels.Settings;

public interface ISettingsSection
{
    SettingsSaveState SaveState => IsDirty ? SettingsSaveState.Dirty : SettingsSaveState.Clean;
    SettingsValidationState ValidationState => LastError is null ? SettingsValidationState.Valid : SettingsValidationState.Invalid;
    bool IsSaving => SaveState == SettingsSaveState.Saving;
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
