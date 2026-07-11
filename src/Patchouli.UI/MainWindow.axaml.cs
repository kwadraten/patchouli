using Avalonia.Controls;
using Patchouli.UI.ViewModels;
using Patchouli.UI.Diagnostics;

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
            UnexpectedExceptions.Sink.Report(ex, "ui-event", "copy-mcp-address");
            _viewModel.Report($"复制失败: {ex.Message}");
        }
    }

    protected override async void OnClosed(EventArgs e)
    {
        try
        {
            await _viewModel.StopMcpServerAsync();
        }
        catch (Exception exception)
        {
            UnexpectedExceptions.Sink.Report(exception, "window-shutdown", "stop-mcp-server");
        }
        finally
        {
            base.OnClosed(e);
        }
    }
}
