using Patchouli.UI.ViewModels.Csl;

namespace Patchouli.UI.ViewModels.Settings;

/// <summary>The online subpage shares installed-state and catalog operations with the style manager.</summary>
public sealed class CslOnlineStylesSettingsViewModel : SettingsSectionViewModelBase
{
    public CslOnlineStylesSettingsViewModel(CslStyleManagerViewModel styles)
    {
        Styles = styles;
    }

    public CslStyleManagerViewModel Styles { get; }
    public override bool SupportsEditing => false;
    public override bool IsDirty => false;
    public override bool CanSave => false;

    public override Task LoadAsync(CancellationToken cancellationToken = default)
    {
        return Styles.InitializeCatalogAsync();
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
