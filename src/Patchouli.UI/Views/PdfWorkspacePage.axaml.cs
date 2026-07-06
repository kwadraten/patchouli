using Avalonia.Controls;

namespace Patchouli.UI.Views;

public sealed partial class PdfWorkspacePage : UserControl
{
    public PdfWorkspacePage()
    {
        InitializeComponent();
    }

    private void OnBBoxPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is Avalonia.Controls.Control control && control.DataContext is PdfBBoxViewModel bbox)
        {
            if (DataContext is MainWindowViewModel viewModels)
            {
                viewModels.PdfWorkspace.SelectedBox = bbox;
            }
        }
    }
}
