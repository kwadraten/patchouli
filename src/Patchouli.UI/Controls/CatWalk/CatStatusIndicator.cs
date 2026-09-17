using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Patchouli.UI.Controls.CatWalk;

namespace Patchouli.UI.Controls;

public class CatStatusIndicator : Control
{
    public static readonly Size DefaultIndicatorSize = new(24.0, 20.0);

    public static readonly StyledProperty<bool> IsBusyProperty =
        AvaloniaProperty.Register<CatStatusIndicator, bool>(nameof(IsBusy));

    public static readonly StyledProperty<bool> IsWindowMinimizedProperty =
        AvaloniaProperty.Register<CatStatusIndicator, bool>(nameof(IsWindowMinimized));

    public static readonly StyledProperty<string?> ActivitySummaryProperty =
        AvaloniaProperty.Register<CatStatusIndicator, string?>(nameof(ActivitySummary));

    public static readonly StyledProperty<string?> SleepReasonProperty =
        AvaloniaProperty.Register<CatStatusIndicator, string?>(nameof(SleepReason));

    public static readonly StyledProperty<string?> StatusTextProperty =
        AvaloniaProperty.Register<CatStatusIndicator, string?>(nameof(StatusText));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<CatStatusIndicator, IBrush?>(nameof(Foreground), inherits: true);

    private readonly CatAnimationStateMachine _stateMachine = new();
    private DispatcherTimer? _timer;
    private bool _isAttached;

    static CatStatusIndicator()
    {
        AffectsRender<CatStatusIndicator>(ForegroundProperty);
    }

    public CatStatusIndicator()
    {
        UpdateAccessibilityAndTooltip();
    }

    public bool IsBusy
    {
        get => GetValue(IsBusyProperty);
        set => SetValue(IsBusyProperty, value);
    }

    public bool IsWindowMinimized
    {
        get => GetValue(IsWindowMinimizedProperty);
        set => SetValue(IsWindowMinimizedProperty, value);
    }

    public string? ActivitySummary
    {
        get => GetValue(ActivitySummaryProperty);
        set => SetValue(ActivitySummaryProperty, value);
    }

    public string? SleepReason
    {
        get => GetValue(SleepReasonProperty);
        set => SetValue(SleepReasonProperty, value);
    }

    public string? StatusText
    {
        get => GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public CatAnimationStateMachine StateMachine => _stateMachine;

    public int CurrentFrameIndex => _stateMachine.CurrentFrameIndex;

    public bool IsTimerRunning => _timer?.IsEnabled == true;

    protected override Size MeasureOverride(Size availableSize)
    {
        return DefaultIndicatorSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        return DefaultIndicatorSize;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        CatGeometrySet geometrySet = CatGeometryProvider.Instance;
        Geometry geometry = _stateMachine.IsBusy
            ? geometrySet.ActiveGeometries[_stateMachine.CurrentFrameIndex % geometrySet.ActiveGeometries.Count]
            : geometrySet.IdleGeometry;

        Rect shared = geometrySet.SharedBounds;
        if (shared.Width <= 0 || shared.Height <= 0)
        {
            return;
        }

        double scale = Math.Min(Bounds.Width / shared.Width, Bounds.Height / shared.Height);
        double drawW = shared.Width * scale;
        double drawH = shared.Height * scale;
        double offsetX = (Bounds.Width - drawW) / 2.0 - shared.X * scale;
        double offsetY = (Bounds.Height - drawH) / 2.0 - shared.Y * scale;

        IBrush brush = Foreground ?? Brushes.Gray;
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offsetX, offsetY)))
        {
            context.DrawGeometry(brush, null, geometry);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        UpdateAnimationState();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _isAttached = false;
        UpdateAnimationState();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsBusyProperty || change.Property == IsWindowMinimizedProperty)
        {
            UpdateAnimationState();
        }

        if (change.Property == IsBusyProperty ||
            change.Property == ActivitySummaryProperty ||
            change.Property == SleepReasonProperty ||
            change.Property == StatusTextProperty)
        {
            UpdateAccessibilityAndTooltip();
        }
    }

    private void UpdateAnimationState()
    {
        _stateMachine.IsBusy = IsBusy;
        _stateMachine.IsWindowMinimized = IsWindowMinimized;
        _stateMachine.IsAttached = _isAttached;

        if (_stateMachine.ShouldAnimate)
        {
            if (_timer is null)
            {
                _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Render, OnTimerTick);
                _timer.Start();
            }
            else if (!_timer.IsEnabled)
            {
                _timer.Start();
            }
        }
        else
        {
            if (_timer is { IsEnabled: true })
            {
                _timer.Stop();
            }
        }

        InvalidateVisual();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (_stateMachine.Step())
        {
            InvalidateVisual();
        }
    }

    public bool StepForTesting()
    {
        bool advanced = _stateMachine.Step();
        if (advanced)
        {
            InvalidateVisual();
        }

        return advanced;
    }

    private void UpdateAccessibilityAndTooltip()
    {
        string text = StatusText ?? FormatStatusText(IsBusy, ActivitySummary, SleepReason);
        ToolTip.SetTip(this, text);
        AutomationProperties.SetName(this, text);
    }

    public static string FormatStatusText(bool isBusy, string? activeSummary, string? sleepReason)
    {
        if (isBusy)
        {
            return string.IsNullOrWhiteSpace(activeSummary) ? "正在运行后台任务" : $"正在运行: {activeSummary}";
        }

        if (!string.IsNullOrWhiteSpace(sleepReason))
        {
            return $"等待中: {sleepReason}";
        }

        return "空闲：无后台活动";
    }
}
