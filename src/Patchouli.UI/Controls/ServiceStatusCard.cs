using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Patchouli.UI.Controls;

/// <summary>Shared skeleton for local services (MCP HTTP and CAYW): status, endpoint, lifecycle
/// actions and an optional save/restart banner. The service-specific settings remain body content.</summary>
public class ServiceStatusCard : ContentControl, ISettingsAnchor
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<ServiceStatusCard, string?>(nameof(Title), "服务状态");

    public static readonly StyledProperty<string?> AnchorIdProperty =
        AvaloniaProperty.Register<ServiceStatusCard, string?>(nameof(AnchorId));

    public static readonly StyledProperty<string?> StatusTextProperty =
        AvaloniaProperty.Register<ServiceStatusCard, string?>(nameof(StatusText));

    public static readonly StyledProperty<string?> EndpointProperty =
        AvaloniaProperty.Register<ServiceStatusCard, string?>(nameof(Endpoint));

    public static readonly StyledProperty<IBrush?> StatusForegroundProperty =
        AvaloniaProperty.Register<ServiceStatusCard, IBrush?>(nameof(StatusForeground));

    public static readonly StyledProperty<object?> ActionsProperty =
        AvaloniaProperty.Register<ServiceStatusCard, object?>(nameof(Actions));

    public static readonly StyledProperty<object?> EndpointActionsProperty =
        AvaloniaProperty.Register<ServiceStatusCard, object?>(nameof(EndpointActions));

    public static readonly StyledProperty<object?> BannerProperty =
        AvaloniaProperty.Register<ServiceStatusCard, object?>(nameof(Banner));

    public static readonly StyledProperty<bool> IsHighlightedProperty =
        AvaloniaProperty.Register<ServiceStatusCard, bool>(nameof(IsHighlighted));

    public ServiceStatusCard()
    {
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? AnchorId
    {
        get => GetValue(AnchorIdProperty);
        set => SetValue(AnchorIdProperty, value);
    }

    public string? StatusText
    {
        get => GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    public string? Endpoint
    {
        get => GetValue(EndpointProperty);
        set => SetValue(EndpointProperty, value);
    }

    public IBrush? StatusForeground
    {
        get => GetValue(StatusForegroundProperty);
        set => SetValue(StatusForegroundProperty, value);
    }

    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    public object? EndpointActions
    {
        get => GetValue(EndpointActionsProperty);
        set => SetValue(EndpointActionsProperty, value);
    }

    public object? Banner
    {
        get => GetValue(BannerProperty);
        set => SetValue(BannerProperty, value);
    }

    public bool IsHighlighted
    {
        get => GetValue(IsHighlightedProperty);
        set => SetValue(IsHighlightedProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsHighlightedProperty)
        {
            Classes.Set("highlighted", change.GetNewValue<bool>());
        }
    }
}
