using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Patchouli.UI.Views;

public sealed partial class FirstRunPage : UserControl
{
    public FirstRunPage()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private async void OnBrowseScanRootClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null || ViewModel is null)
        {
            return;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose PDF scan folder",
            AllowMultiple = false
        });

        if (folders.Count > 0 && folders[0].Path.LocalPath is { Length: > 0 } path)
        {
            ViewModel.FirstRun.ScanRoot = path;
        }
    }

    private async void OnOpenMinerUTokenPageClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var launcher = TopLevel.GetTopLevel(this)?.Launcher;
        if (launcher is not null)
        {
            await launcher.LaunchUriAsync(new Uri("https://mineru.net/apiManage/token"));
        }
    }

    private async void OnBrowseExistingDatabaseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null || ViewModel is null)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select existing Patchouli database",
            FileTypeFilter =
            [
                new FilePickerFileType("SQLite database") { Patterns = ["*.sqlite", "*.db"] },
                FilePickerFileTypes.All
            ]
        });

        if (files.Count > 0 && files[0].Path.LocalPath is { Length: > 0 } path)
        {
            ViewModel.FirstRun.DatabasePath = path;
            await ViewModel.FirstRun.OpenDatabaseCommand.ExecuteAsync();
        }
    }

    private async void OnCreateNewDatabaseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null || ViewModel is null)
        {
            return;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Create Patchouli database",
            SuggestedFileName = "patchouli-runtime.sqlite",
            DefaultExtension = "sqlite",
            FileTypeChoices =
            [
                new FilePickerFileType("SQLite database") { Patterns = ["*.sqlite", "*.db"] },
                FilePickerFileTypes.All
            ]
        });

        if (file?.Path.LocalPath is { Length: > 0 } path)
        {
            ViewModel.FirstRun.DatabasePath = path;
            await ViewModel.FirstRun.OpenDatabaseCommand.ExecuteAsync();
        }
    }
}
