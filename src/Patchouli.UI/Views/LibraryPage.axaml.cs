using Avalonia.Controls;
using Patchouli.UI.ViewModels;

namespace Patchouli.UI.Views;

public sealed partial class LibraryPage : UserControl
{
    public LibraryPage()
    {
        InitializeComponent();
    }

    private async void OnDataGridDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (DataContext is LibraryShellViewModel { SelectedItem: not null } shell)
        {
            await shell.ViewPdfForItemAsync(shell.SelectedItem);
        }
    }
}
