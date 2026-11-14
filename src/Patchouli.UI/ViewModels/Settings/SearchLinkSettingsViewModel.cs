using System.Threading;
using System.Threading.Tasks;

namespace Patchouli.UI.ViewModels.Settings;

/// <summary>「链接解析」settings tab. The toolbar parsing switch is an immediately persisted
/// device preference and intentionally has no draft lifecycle; protocol registration remains
/// owned by <see cref="VirtualLinkSettingsViewModel"/> under 行为 → 链接与系统.</summary>
public sealed class SearchLinkSettingsViewModel : SettingsSectionViewModelBase
{
    public SearchLinkSettingsViewModel(VirtualLinkSettingsViewModel virtualLinks)
    {
        VirtualLinks = virtualLinks;
    }

    public VirtualLinkSettingsViewModel VirtualLinks { get; }

    public override bool SupportsEditing => true;
    public override bool IsDirty => false;
    public override bool CanSave => false;

    public override Task LoadAsync(CancellationToken cancellationToken = default)
    {
        return VirtualLinks.LoadAsync(cancellationToken);
    }

    public override Task SaveAsync()
    {
        return Task.CompletedTask;
    }

    public override Task DiscardAsync()
    {
        return VirtualLinks.DiscardAsync();
    }
}
