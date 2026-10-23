using Avalonia.Controls;
using Avalonia.Input;
using Patchouli.Host.CitationInterop;
using Patchouli.UI.ViewModels.Citations;

namespace Patchouli.UI.Views;

public partial class CitationPickerWindow : Window
{
    public CitationPickerWindow()
    {
        InitializeComponent();
        DialogCloseBinding.Bind<CitationPickerViewModel, CitationPickBridgeResult>(
            this, static (vm, close) => vm.RequestClose = close);
        Opened += async (_, _) =>
        {
            SearchBox.Focus();
            if (DataContext is CitationPickerViewModel viewModel)
            {
                await viewModel.InitializeAsync();
            }
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && DataContext is CitationPickerViewModel viewModel)
            {
                viewModel.CancelCommand.Execute(null);
                e.Handled = true;
            }
        };
    }
}
