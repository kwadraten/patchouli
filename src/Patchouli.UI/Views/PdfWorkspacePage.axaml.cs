using Avalonia;
using Avalonia.Controls;
using Patchouli.UI.ViewModels;

namespace Patchouli.UI.Views;

public sealed partial class PdfWorkspacePage : UserControl
{
    public PdfWorkspacePage()
    {
        InitializeComponent();
    }

    private void OnBBoxPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is Control control && control.DataContext is PdfBBoxViewModel bbox)
        {
            if (DataContext is PdfWorkspaceViewModel pdf)
            {
                pdf.SelectedBox = bbox;
            }
        }
    }

    private void OnCanvasPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (DataContext is PdfWorkspaceViewModel pdf && sender is Control control)
        {
            Point p = e.GetPosition(control);
            pdf.OnPointerPressed(p.X, p.Y);
        }
    }

    private void OnCanvasPointerMoved(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (DataContext is PdfWorkspaceViewModel pdf && sender is Control control)
        {
            Point p = e.GetPosition(control);
            pdf.OnPointerMoved(p.X, p.Y);
        }
    }

    private void OnCanvasPointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (DataContext is PdfWorkspaceViewModel pdf)
        {
            pdf.OnPointerReleased();
        }
    }
}
