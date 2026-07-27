using Avalonia.Controls;
using Patchouli.UI.ViewModels;

namespace Patchouli.UI.Views;

public sealed partial class BoxEditorDialog : Window
{
    public BoxEditorDialog()
    {
        InitializeComponent();
    }

    private void OnCloseClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }

    private void OnSaveClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is PdfBBoxViewModel viewModel)
        {
            _ = viewModel.SaveTextCommand.ExecuteAsync();
        }

        Close();
    }

    private void OnAcceptClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is PdfBBoxViewModel viewModel)
        {
            _ = AcceptAndCloseAsync(viewModel);
        }
    }

    private async Task AcceptAndCloseAsync(PdfBBoxViewModel viewModel)
    {
        await viewModel.Workspace.AcceptLocalOcrCommand.ExecuteAsync();
        if (!viewModel.Workspace.HasCandidate)
        {
            Close();
        }
    }
}
