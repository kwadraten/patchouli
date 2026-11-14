using System.Threading;
using System.Threading.Tasks;

namespace Patchouli.UI.ViewModels.Settings;

/// <summary>「访问权限」settings tab. It is a presentation split of
/// <see cref="McpSettingsViewModel"/>: the same owner and draft keep save-and-restart semantics
/// and the tab is never auto-saved on its own.</summary>
public sealed class McpPermissionsSettingsViewModel : SettingsSectionViewModelBase
{
    public McpPermissionsSettingsViewModel(McpSettingsViewModel mcp)
    {
        Mcp = mcp;
    }

    public McpSettingsViewModel Mcp { get; }

    public override bool SupportsEditing => Mcp.SupportsEditing;
    public override bool IsDirty => Mcp.IsDirty;
    public override bool CanSave => Mcp.CanSave;

    public override Task LoadAsync(CancellationToken cancellationToken = default)
    {
        return Mcp.LoadAsync(cancellationToken);
    }

    public override Task SaveAsync()
    {
        return Mcp.SaveAsync();
    }

    public override Task DiscardAsync()
    {
        return Mcp.DiscardAsync();
    }
}
