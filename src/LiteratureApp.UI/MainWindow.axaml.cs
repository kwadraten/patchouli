using Avalonia.Controls;

namespace LiteratureApp.UI;

public sealed partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public MainWindow()
    {
        _viewModel = new MainWindowViewModel();
        DataContext = _viewModel;
        InitializeComponent();
    }

    public async Task ShowFirstRunIfNeededAsync()
    {
        var services = await _viewModel.ServicesAsync();
        var library = await services.Library.GetCurrentLibraryAsync();
        if (library.IsFailure)
        {
            var firstRunVm = new FirstRunViewModel(
                services.FirstRunWorkflow, services.PdfDiscovery);
            var window = new FirstRunWindow(firstRunVm);
            await window.ShowDialog(this);
            _viewModel.Shell.Refresh();
        }
    }
}
