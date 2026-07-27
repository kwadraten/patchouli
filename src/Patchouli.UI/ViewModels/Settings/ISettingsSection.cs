using System.Threading.Tasks;

namespace Patchouli.UI.ViewModels.Settings;

public interface ISettingsSection
{
    SettingsSaveState SaveState { get; }
    SettingsValidationState ValidationState { get; }
    bool IsSaving { get; }
    bool RequiresReload { get; }
    string EffectiveSourceText { get; }
    string ScopeText { get; }
    bool SupportsEditing { get; }
    bool IsDirty { get; }
    bool CanSave { get; }
    string SaveStateText { get; }
    string? LastError { get; }

    Task LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync();
    Task DiscardAsync();
}

public abstract class SettingsSectionViewModelBase : ViewModelBase, ISettingsSection
{
    private string _status = "";
    private SettingsSaveState _saveState = SettingsSaveState.Clean;
    private SettingsValidationState _validationState = SettingsValidationState.Unknown;
    private string? _lastError;

    public string Status
    {
        get => _status;
        protected set
        {
            if (_status != value)
            {
                _status = value;
                Raise();
                Raise(nameof(SaveStateText));
            }
        }
    }

    public string SaveStateText => Status;

    public SettingsSaveState SaveState
    {
        get => _saveState;
        protected set
        {
            if (_saveState != value)
            {
                _saveState = value;
                Raise();
                Raise(nameof(IsSaving));
            }
        }
    }

    public SettingsValidationState ValidationState
    {
        get => _validationState;
        protected set
        {
            if (_validationState != value)
            {
                _validationState = value;
                Raise();
            }
        }
    }

    public string? LastError
    {
        get => _lastError;
        protected set
        {
            if (_lastError != value)
            {
                _lastError = value;
                Raise();
            }
        }
    }

    public bool IsSaving => SaveState == SettingsSaveState.Saving;

    private bool _requiresReload;

    public virtual bool RequiresReload
    {
        get => _requiresReload;
        protected set
        {
            if (_requiresReload != value)
            {
                _requiresReload = value;
                Raise();
            }
        }
    }

    public virtual string EffectiveSourceText => "本机 JSON 设置";
    public virtual string ScopeText => "仅此设备";
    public abstract bool SupportsEditing { get; }
    public abstract bool IsDirty { get; }
    public abstract bool CanSave { get; }

    public virtual Task LoadAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public abstract Task SaveAsync();
    public abstract Task DiscardAsync();
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
