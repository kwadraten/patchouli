using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
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
    private DispatcherTimer? _geometryClampDebounce;

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
        Opened += OnFirstOpened;
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

            if (WindowState == WindowState.Normal
                && change.GetOldValue<WindowState>() == WindowState.Maximized)
            {
                Dispatcher.UIThread.Post(
                    () => ClampToWorkingArea(false),
                    DispatcherPriority.Background);
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

    private void OnFirstOpened(object? sender, EventArgs e)
    {
        Opened -= OnFirstOpened;
        Screens.Changed += OnScreensChanged;
        PositionChanged += OnPositionChanged;
        Resized += OnWindowResized;
        // Position/ScreenFromWindow are not final inside Opened itself (the OS applies
        // cascade placement afterwards), so clamp once the placement has settled.
        Dispatcher.UIThread.Post(
            () => ClampToWorkingArea(true),
            DispatcherPriority.Loaded);
    }

    private void OnScreensChanged(object? sender, EventArgs e)
    {
        ClampToWorkingArea(false);
    }

    private void OnPositionChanged(object? sender, PixelPointEventArgs e)
    {
        ScheduleGeometryClamp();
    }

    private void OnWindowResized(object? sender, WindowResizedEventArgs e)
    {
        ScheduleGeometryClamp();
    }

    private void ScheduleGeometryClamp()
    {
        if (WindowState != WindowState.Normal)
        {
            return;
        }

        _geometryClampDebounce ??= new DispatcherTimer(
            TimeSpan.FromMilliseconds(600),
            DispatcherPriority.Background,
            OnGeometryClampDebounceTick);
        _geometryClampDebounce.Stop();
        _geometryClampDebounce.Start();
    }

    private void OnGeometryClampDebounceTick(object? sender, EventArgs e)
    {
        _geometryClampDebounce?.Stop();
        ClampToWorkingArea(false);
    }

    private void ClampToWorkingArea(bool recenter)
    {
        // The default Width/Height exceed small screens (e.g. 820 DIP on a 672 DIP
        // working area), pushing the fixed 28 px status bar row behind the taskbar.
        // WorkingArea/Position are physical pixels while Width/Height are DIPs, so
        // convert via Scaling.
        if (WindowState != WindowState.Normal)
        {
            return;
        }

        // Posted/deferred invocations can land after the window is closed (headless
        // tests dispose the platform impl right after Opened); there is nothing to
        // clamp then.
        Screen? screen;
        try
        {
            screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (screen is null)
        {
            return;
        }

        const double margin = 40;
        double scaling = screen.Scaling > 0 ? screen.Scaling : 1;
        PixelRect workingArea = screen.WorkingArea;
        double maxWidth = Math.Max(margin * 2, workingArea.Width / scaling - margin * 2);
        double maxHeight = Math.Max(margin * 2, workingArea.Height / scaling - margin * 2);
        double targetWidth = Math.Min(Width, maxWidth);
        double targetHeight = Math.Min(Height, maxHeight);

        if (targetWidth < MinWidth)
        {
            MinWidth = targetWidth;
        }

        if (targetHeight < MinHeight)
        {
            MinHeight = targetHeight;
        }

        Width = targetWidth;
        Height = targetHeight;

        // Position works in physical pixels of the whole frame, which is larger than
        // the client area by the border/title bar (e.g. ~14x45 DIP on Windows).
        Size frameExtra = FrameSize is { } frame
            ? new Size(Math.Max(0, frame.Width - Width), Math.Max(0, frame.Height - Height))
            : new Size(14, 45);
        int windowWidth = (int)Math.Ceiling((targetWidth + frameExtra.Width) * scaling);
        int windowHeight = (int)Math.Ceiling((targetHeight + frameExtra.Height) * scaling);
        int maxX = workingArea.X + Math.Max(0, workingArea.Width - windowWidth);
        int maxY = workingArea.Y + Math.Max(0, workingArea.Height - windowHeight);
        int x = recenter
            ? workingArea.X + Math.Max(0, (workingArea.Width - windowWidth) / 2)
            : Math.Clamp(Position.X, workingArea.X, Math.Max(workingArea.X, maxX));
        int y = recenter
            ? workingArea.Y + Math.Max(0, (workingArea.Height - windowHeight) / 2)
            : Math.Clamp(Position.Y, workingArea.Y, Math.Max(workingArea.Y, maxY));
        if (x != Position.X || y != Position.Y)
        {
            Position = new PixelPoint(x, y);
        }
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

    private void OnOcrMenuOpened(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, e.Source))
        {
            return;
        }

        UnexpectedExceptionBoundary.RunAsync(_viewModel.RefreshOcrEngineMenuAsync, "ocr-engine-menu-refresh")
            .Observe("ui-event", "ocr-engine-menu-refresh");
    }

    private async void OnWorkflowMenuPointerEntered(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        // Menus are opened far more often than workflows change: rebuilding the entries here keeps the
        // list current (and picks up the current Library selection) without watching the store.
        await UnexpectedExceptionBoundary.RunAsync(_viewModel.RefreshWorkflowMenuAsync, "workflow-menu-refresh");
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
