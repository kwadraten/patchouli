using Avalonia.Controls;
using Avalonia.Platform.Storage;

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
            await _viewModel.ShowInlineFirstRunAsync();
            return;
        }

        _viewModel.Shell.Refresh();
    }

    private async void OnBrowseDatabaseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Choose Patchouli database",
            SuggestedFileName = "patchouli-runtime.sqlite",
            DefaultExtension = "sqlite",
            FileTypeChoices =
            [
                new FilePickerFileType("SQLite database")
                {
                    Patterns = ["*.sqlite", "*.db"],
                    AppleUniformTypeIdentifiers = ["public.database"]
                },
                FilePickerFileTypes.All
            ]
        });

        if (file?.Path.LocalPath is { Length: > 0 } path)
            _viewModel.FirstRun.DatabasePath = path;
    }

    private async void OnBrowseScanRootClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose PDF scan folder",
            AllowMultiple = false
        });

        if (folders.Count > 0 && folders[0].Path.LocalPath is { Length: > 0 } path)
            _viewModel.FirstRun.ScanRoot = path;
    }

    private async void OnOpenMinerUTokenPageClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await Launcher.LaunchUriAsync(new Uri("https://mineru.net/apiManage/token"));
    }
}
