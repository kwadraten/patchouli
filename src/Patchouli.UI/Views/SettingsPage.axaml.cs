using Avalonia.Controls;
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
}
