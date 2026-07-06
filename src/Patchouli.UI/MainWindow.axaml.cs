using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Patchouli.UI;

public sealed partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public MainWindow()
    {
        _viewModel = new MainWindowViewModel(autoStartMcpServer: true);
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

    private async void OnBrowseExistingDatabaseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
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
            _viewModel.FirstRun.DatabasePath = path;
            await _viewModel.FirstRun.OpenDatabaseCommand.ExecuteAsync();
        }
    }

    private async void OnCreateNewDatabaseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
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
            _viewModel.FirstRun.DatabasePath = path;
            await _viewModel.FirstRun.OpenDatabaseCommand.ExecuteAsync();
        }
    }

    private void OnListBoxDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (_viewModel.Shell.SelectedItem is not null)
        {
            _viewModel.Shell.SwitchToReadingModeCommand.Execute(null);
        }
    }

    private async void OnCopyMcpAddressClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            await _viewModel.Clipboard.SetTextAsync(_viewModel.McpEndpoint);
            _viewModel.Report("MCP 服务地址已复制到剪贴板。");
        }
        catch (Exception ex)
        {
            _viewModel.Report($"复制失败: {ex.Message}");
        }
    }

    private async void OnExportEvidenceMarkdownClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await ExportEvidenceMarkdownWithPickerAsync();
    }

    private async void OnExportSearchUnitEvidenceMarkdownClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: SearchMatchedUnitViewModel unit } ||
            string.IsNullOrWhiteSpace(unit.EvidenceRef))
        {
            _viewModel.Report("请选择一个可导出的 EvidenceRef。");
            return;
        }

        _viewModel.SearchEvidence.EvidenceRef = unit.EvidenceRef;
        await ExportEvidenceMarkdownWithPickerAsync();
    }

    private async Task ExportEvidenceMarkdownWithPickerAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Evidence Markdown",
            SuggestedFileName = "evidence.md",
            DefaultExtension = "md",
            FileTypeChoices =
            [
                new FilePickerFileType("Markdown") { Patterns = ["*.md"] },
                FilePickerFileTypes.All
            ]
        });

        if (file?.Path.LocalPath is { Length: > 0 } path)
        {
            await _viewModel.ExportEvidenceMarkdownToFileAsync(path);
        }
    }

    protected override async void OnClosed(EventArgs e)
    {
        await _viewModel.StopMcpServerAsync();
        base.OnClosed(e);
    }
}
