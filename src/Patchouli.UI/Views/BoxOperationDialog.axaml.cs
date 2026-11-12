using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Patchouli.Core.Layout;
using Patchouli.UI.ViewModels;

namespace Patchouli.UI.Views;

public sealed partial class BoxOperationDialog : Window
{
    private double _splitZoom = 1;
    private bool _splitFitPending = true;
    private bool _isPanning;
    private Point _panStart;
    private Vector _panStartOffset;

    public BoxOperationDialog()
    {
        InitializeComponent();
        Closing += OnDialogClosing;
        DataContextChanged += (_, _) => _splitFitPending = true;
        SplitScrollViewer.AddHandler(PointerWheelChangedEvent, OnSplitPointerWheelChanged,
            RoutingStrategies.Tunnel);
    }

    private void OnSplitCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not PdfWorkspaceViewModel workspace || sender is not Control control ||
            !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Point position = e.GetPosition(control);
        workspace.OnPointerPressed(position.X, position.Y);
        if (workspace.IsDrawing)
        {
            e.Pointer.Capture(control);
            e.Handled = true;
        }
    }

    private void OnSplitCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        if (DataContext is PdfWorkspaceViewModel workspace && sender is Control control)
        {
            Point position = e.GetPosition(control);
            workspace.OnPointerMoved(position.X, position.Y);
        }
    }

    private void OnSplitCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is PdfWorkspaceViewModel workspace)
        {
            workspace.OnPointerReleased(false);
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private void OnSplitScrollPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ScrollViewer scroll || !e.GetCurrentPoint(scroll).Properties.IsMiddleButtonPressed)
        {
            return;
        }

        _panStart = e.GetPosition(scroll);
        _panStartOffset = scroll.Offset;
        _isPanning = true;
        e.Pointer.Capture(scroll);
        e.Handled = true;
    }

    private void OnSplitScrollPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning || sender is not ScrollViewer scroll)
        {
            return;
        }

        Point position = e.GetPosition(scroll);
        scroll.Offset = new Vector(
            _panStartOffset.X - (position.X - _panStart.X),
            _panStartOffset.Y - (position.Y - _panStart.Y));
        e.Handled = true;
    }

    private void OnSplitScrollPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isPanning)
        {
            return;
        }

        _isPanning = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnSplitPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ScrollViewer scroll || !e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }

        e.Handled = true;
        double previousZoom = _splitZoom;
        SetSplitZoom(_splitZoom + (e.Delta.Y > 0 ? 0.1 : -0.1));
        Point position = e.GetPosition(scroll);
        Vector offset = scroll.Offset;
        double scale = _splitZoom / previousZoom;
        double x = (offset.X + position.X) * scale - position.X;
        double y = (offset.Y + position.Y) * scale - position.Y;
        Dispatcher.UIThread.Post(() => scroll.Offset = new Vector(Math.Max(0, x), Math.Max(0, y)),
            DispatcherPriority.Render);
    }

    private void OnSplitZoomIn(object? sender, RoutedEventArgs e)
    {
        SetSplitZoom(_splitZoom + 0.1);
    }

    private void OnSplitZoomOut(object? sender, RoutedEventArgs e)
    {
        SetSplitZoom(_splitZoom - 0.1);
    }

    private void SetSplitZoom(double zoom)
    {
        _splitZoom = Math.Clamp(zoom, 0.1, 4);
        ScaleTransform scale = (ScaleTransform)SplitLayoutTransform.LayoutTransform!;
        scale.ScaleX = _splitZoom;
        scale.ScaleY = _splitZoom;
        SplitZoomText.Text = $"{Math.Round(_splitZoom * 100):0}%";
    }

    private void OnSplitViewportSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_splitFitPending)
        {
            FitAndCenterSplitSource();
        }
    }

    private void OnCenterSplitSource(object? sender, RoutedEventArgs e)
    {
        CenterSplitSource();
    }

    private void FitAndCenterSplitSource()
    {
        if (DataContext is not PdfWorkspaceViewModel { SplitSource: { } source } workspace ||
            workspace.WidthPixels <= 0 || workspace.HeightPixels <= 0 ||
            SplitScrollViewer.Viewport.Width <= 0 || SplitScrollViewer.Viewport.Height <= 0)
        {
            return;
        }

        NormalizedBBox bounds = source.BBox;
        double targetWidth = Math.Max(1, bounds.Width * workspace.WidthPixels * 1.5);
        double targetHeight = Math.Max(1, bounds.Height * workspace.HeightPixels * 1.5);
        double zoom = Math.Min(
            (SplitScrollViewer.Viewport.Width - 48) / targetWidth,
            (SplitScrollViewer.Viewport.Height - 48) / targetHeight);
        SetSplitZoom(zoom);
        _splitFitPending = false;
        Dispatcher.UIThread.Post(CenterSplitSource, DispatcherPriority.Render);
    }

    private void CenterSplitSource()
    {
        if (DataContext is not PdfWorkspaceViewModel { SplitSource: { } source } workspace)
        {
            return;
        }

        NormalizedBBox bounds = source.BBox;
        double centerX = (bounds.X + bounds.Width / 2) * workspace.WidthPixels * _splitZoom + 24;
        double centerY = (bounds.Y + bounds.Height / 2) * workspace.HeightPixels * _splitZoom + 24;
        SplitScrollViewer.Offset = new Vector(
            Math.Max(0, centerX - SplitScrollViewer.Viewport.Width / 2),
            Math.Max(0, centerY - SplitScrollViewer.Viewport.Height / 2));
    }

    private void OnDialogClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is not PdfWorkspaceViewModel workspace)
        {
            return;
        }

        if (workspace.IsSplitPending)
        {
            workspace.CancelSplitCommand.Execute(null);
        }
        else if (workspace.IsMergePending)
        {
            workspace.CancelMergeCommand.Execute(null);
        }
        else if (workspace.IsPickingContinuationSource)
        {
            workspace.CancelContinuationPickCommand.Execute(null);
        }
    }
}
