using System.Threading;
using System.Threading.Tasks;

namespace Patchouli.UI.ViewModels.Settings;

/// <summary>「链接与系统」settings tab: patchouli:// registration, foreground activation, CLI PATH
/// registration and the device-local network proxy. The three owners keep their existing
/// persistence lifecycles; this page only hosts their cards.</summary>
public sealed class SystemIntegrationSettingsViewModel : SettingsSectionViewModelBase
{
    public SystemIntegrationSettingsViewModel(
        VirtualLinkSettingsViewModel virtualLinks,
        NetworkProxySettingsViewModel networkProxy,
        McpSettingsViewModel mcp)
    {
        VirtualLinks = virtualLinks;
        NetworkProxy = networkProxy;
        Mcp = mcp;
    }

    public VirtualLinkSettingsViewModel VirtualLinks { get; }
    public NetworkProxySettingsViewModel NetworkProxy { get; }
    public McpSettingsViewModel Mcp { get; }

    public override bool SupportsEditing => true;
    public override bool IsDirty => false;
    public override bool CanSave => false;

    public override async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await VirtualLinks.LoadAsync(cancellationToken);
        await NetworkProxy.LoadAsync(cancellationToken);
        await Mcp.RefreshCliStatusAsync();
    }

    public override Task SaveAsync()
    {
        return Task.CompletedTask;
    }

    public override Task DiscardAsync()
    {
        return Task.CompletedTask;
    }
}
