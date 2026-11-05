using Avalonia.Controls;
using Avalonia.Interactivity;
using Patchouli.UI.ViewModels.Settings;

namespace Patchouli.UI.Views.Settings;

public partial class OcrComponentCatalogView : UserControl
{
    public OcrComponentCatalogView()
    {
        InitializeComponent();
    }

    private void AddExtensionClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is OcrComponentCatalogSettingsViewModel catalog &&
            TopLevel.GetTopLevel(this) is Window owner)
        {
            catalog.AddAsync(owner).Observe(nameof(OcrComponentCatalogView), "add-extension");
        }
    }
}
