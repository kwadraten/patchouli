using System.Threading.Tasks;

namespace Patchouli.UI.ViewModels.Settings;

public interface ISettingsSection
{
    bool SupportsEditing { get; }
    bool IsDirty { get; }
    bool CanSave { get; }
    string SaveStateText { get; }
    string? LastError { get; }

    Task SaveAsync();
    Task DiscardAsync();
}
