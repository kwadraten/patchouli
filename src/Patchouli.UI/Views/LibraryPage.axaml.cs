using Avalonia.Controls;

namespace Patchouli.UI.Views;

public sealed partial class LibraryPage : UserControl
{
    public LibraryPage()
    {
        InitializeComponent();
    }

    private async void OnListBoxDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (DataContext is MainWindowViewModel { Shell.SelectedItem: not null } viewModel)
        {
            await viewModel.ShowReadingCommand.ExecuteAsync();
        }
    }
}
