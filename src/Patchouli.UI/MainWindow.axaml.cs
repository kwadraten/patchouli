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
