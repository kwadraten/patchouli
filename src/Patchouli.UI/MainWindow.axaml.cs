using Avalonia.Controls;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.UI.ViewModels;
using Patchouli.UI.Diagnostics;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI;

public sealed partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private bool _exitConfirmed;

    public MainWindow()
    {
        _viewModel = new MainWindowViewModel(autoStartMcpServer: true);
        DataContext = _viewModel;
        InitializeComponent();
    }

    public async Task ShowFirstRunIfNeededAsync()
    {
        AppServices services = await _viewModel.ServicesAsync();
        Result<LibraryMetadata> library = await services.Library.GetCurrentLibraryAsync();
        if (library.IsFailure)
        {
            await _viewModel.ShowInlineFirstRunAsync();
            return;
        }

        await _viewModel.Shell.RefreshAsync();
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

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (_exitConfirmed || !_viewModel.Settings.HasDirtySections)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        base.OnClosing(e);
        ConfirmDialogResult? choice = await _viewModel.Dialogs.ShowDialogAsync<ConfirmDialogResult>(
            new ConfirmDialogViewModel(
                "退出前保存设置？",
                "设置中有未保存的更改。",
                "保存并退出",
                "放弃并退出"));
        if (choice == ConfirmDialogResult.Confirm)
        {
            bool saved = await _viewModel.Settings.SaveAllDirtySectionsAsync();
            if (!saved)
            {
                return;
            }
        }
        else if (choice != ConfirmDialogResult.Discard)
        {
            return;
        }

        _exitConfirmed = true;
        Close();
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
