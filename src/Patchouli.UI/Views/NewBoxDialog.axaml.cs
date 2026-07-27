using Avalonia.Controls;
using Patchouli.UI.ViewModels;

namespace Patchouli.UI.Views;

public sealed partial class NewBoxDialog : Window
{
    public NewBoxDialog()
    {
        InitializeComponent();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is PdfWorkspaceViewModel { IsNewBoxPending: true } viewModel)
        {
            _ = viewModel.CancelPendingBoxCommand.ExecuteAsync();
        }

        base.OnClosed(e);
    }

    private void OnCancelClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is PdfWorkspaceViewModel viewModel)
        {
            _ = viewModel.CancelPendingBoxCommand.ExecuteAsync();
        }

        Close();
    }

    private void OnInsertClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _ = InsertAndCloseAsync();
    }

    private async Task InsertAndCloseAsync()
    {
        if (DataContext is not PdfWorkspaceViewModel viewModel)
        {
            Close();
            return;
        }

        await viewModel.InsertPendingBoxCommand.ExecuteAsync();
        if (!viewModel.IsNewBoxPending)
        {
            Close();
        }
    }
}
