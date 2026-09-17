using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Patchouli.UI.Controls;
using Patchouli.UI.ViewModels;
using Patchouli.UI.Diagnostics;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI;

public sealed partial class MainWindow : Window
{
    private readonly bool _closeToTray;
    private readonly MainWindowViewModel _viewModel;
    private bool _exitConfirmed;
    private bool _exitRequested;
    private bool _startupScheduled;
    private CatStatusIndicator? _catIndicator;

    private CatStatusIndicator? CatIndicatorControl =>
        _catIndicator ??= this.FindControl<CatStatusIndicator>("CatIndicator");

    public MainWindow()
        : this(new MainWindowViewModel(autoStartMcpServer: true, enforceRuntimeHostOwnership: true), true)
    {
    }

    public MainWindow(MainWindowViewModel viewModel)
        : this(viewModel, false)
    {
    }

    internal MainWindow(MainWindowViewModel viewModel, bool closeToTray)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _closeToTray = closeToTray;
        _viewModel = viewModel;
        DataContext = _viewModel;
        InitializeComponent();
        if (CatIndicatorControl is { } cat)
        {
            cat.IsWindowMinimized = WindowState == WindowState.Minimized;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            if (CatIndicatorControl is { } cat)
            {
                cat.IsWindowMinimized = WindowState == WindowState.Minimized;
            }
        }
    }

    public async Task ShowFirstRunIfNeededAsync(bool startMcpServer = true)
    {
        await _viewModel.RunStartupAsync(startMcpServer);
    }

    public void StartStartupAfterFirstFrame()
    {
        if (_startupScheduled)
        {
            return;
        }

        _startupScheduled = true;
        if (IsVisible)
        {
            ScheduleStartup();
            return;
        }

        Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        ScheduleStartup();
    }

    private void ScheduleStartup()
    {
        // Opened precedes the first render. Background priority lets the initial lightweight
        // shell and loading overlay render before database composition and Library hydration.
        Dispatcher.UIThread.Post(
            () => RunStartupAfterFirstFrameAsync().Observe("application-initialization", "startup-after-first-frame"),
            DispatcherPriority.Background);
    }

    private async Task RunStartupAfterFirstFrameAsync()
    {
        try
        {
            await _viewModel.RunStartupAsync(false);
            _viewModel.StartMcpServerInBackground();
        }
        catch (Exception exception)
        {
            UnexpectedExceptions.Sink.Report(exception, "application-initialization", "startup-after-first-frame");
            _viewModel.ReportError($"启动失败：{exception.Message}");
        }
    }

    public void StartMcpServerInBackground()
    {
        _viewModel.StartMcpServerInBackground();
    }

    private void OnToolbarSearchKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key != Avalonia.Input.Key.Enter)
        {
            return;
        }

        e.Handled = true;
        _viewModel.RunToolbarSearchCommand.Execute(null);
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
        if (_closeToTray && !_exitRequested &&
            e.CloseReason is not WindowCloseReason.ApplicationShutdown and not WindowCloseReason.OSShutdown)
        {
            e.Cancel = true;
            Hide();
            base.OnClosing(e);
            return;
        }

        if (_exitConfirmed || !_viewModel.HasDirtySettings)
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
            bool saved = await _viewModel.SaveDirtySettingsAsync();
            if (!saved)
            {
                _exitRequested = false;
                return;
            }
        }
        else if (choice != ConfirmDialogResult.Discard)
        {
            _exitRequested = false;
            return;
        }

        _exitConfirmed = true;
        Close();
    }

    internal void RequestExit()
    {
        _exitRequested = true;
        Close();
    }

    protected override async void OnClosed(EventArgs e)
    {
        try
        {
            await _viewModel.ShutdownAsync();
        }
        catch (Exception exception)
        {
            UnexpectedExceptions.Sink.Report(exception, "window-shutdown", "shutdown-view-model");
        }
        finally
        {
            base.OnClosed(e);
        }
    }
}
