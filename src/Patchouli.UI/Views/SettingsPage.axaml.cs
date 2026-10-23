using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Threading.Tasks;
using Patchouli.UI.ViewModels.Settings;

namespace Patchouli.UI.Views;

public sealed partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => EnableAutoSaveForDataContext();
        AttachedToVisualTree += (_, _) => EnableAutoSaveForDataContext();
    }

    // The auto-save pipeline is activated by the view so that view-less hosts (tests) keep the
    // manual save/discard semantics; enabling is idempotent and lives as long as the view model.
    private void EnableAutoSaveForDataContext()
    {
        (DataContext as SettingsViewModel)?.EnableAutoSave();
    }

    private void ConfigureExtensionClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id } && DataContext is SettingsViewModel settings &&
            TopLevel.GetTopLevel(this) is Window owner)
        {
            OpenExtensionAsync(settings, id, owner).Observe(nameof(SettingsPage), "configure-extension");
        }
    }

    private static async Task OpenExtensionAsync(SettingsViewModel settings, string id, Window owner)
    {
        using ExtensionSettingsViewModel editor = settings.CreateExtensionEditor();
        await editor.OpenAsync(id);
        ExtensionConfigurationDialog dialog = new(editor);
        await dialog.ShowDialog(owner);
    }
}
