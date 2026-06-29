using Avalonia.Controls;

namespace LiteratureApp.UI;

public sealed partial class FirstRunWindow : Window
{
    private readonly FirstRunViewModel _viewModel;

    public FirstRunWindow()
    {
        _viewModel = null!;
        InitializeComponent();
    }

    public FirstRunWindow(FirstRunViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    private void OnCompleteClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close(true);
    }
}
