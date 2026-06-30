using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Patchouli.UI;

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

    private async void OnBrowseDatabaseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Choose runtime database",
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
            _viewModel.DatabasePath = path;
    }

    private async void OnBrowseScanRootClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose PDF scan folder",
            AllowMultiple = false
        });

        if (folders.Count > 0 && folders[0].Path.LocalPath is { Length: > 0 } path)
            _viewModel.ScanRoot = path;
    }

    private async void OnOpenMinerUTokenPageClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await Launcher.LaunchUriAsync(new Uri("https://mineru.net/apiManage/token"));
    }
}
