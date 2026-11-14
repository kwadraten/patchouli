using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Patchouli.UI.Controls;

/// <summary>Shared settings card: title (with optional hint) on the left; status text and card
/// actions on the right; body content below. The <see cref="AnchorId"/> lets the settings search
/// bring a card into view and highlight it.</summary>
public class SettingsCard : ContentControl, ISettingsAnchor
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<SettingsCard, string?>(nameof(Title));

    public static readonly StyledProperty<string?> AnchorIdProperty =
        AvaloniaProperty.Register<SettingsCard, string?>(nameof(AnchorId));

    public static readonly StyledProperty<string?> HintProperty =
        AvaloniaProperty.Register<SettingsCard, string?>(nameof(Hint));

    public static readonly StyledProperty<string?> StatusProperty =
        AvaloniaProperty.Register<SettingsCard, string?>(nameof(Status));

    public static readonly StyledProperty<IBrush?> StatusForegroundProperty =
        AvaloniaProperty.Register<SettingsCard, IBrush?>(nameof(StatusForeground));

    public static readonly StyledProperty<object?> ActionsProperty =
        AvaloniaProperty.Register<SettingsCard, object?>(nameof(Actions));

    public static readonly StyledProperty<bool> IsHighlightedProperty =
        AvaloniaProperty.Register<SettingsCard, bool>(nameof(IsHighlighted));

    public SettingsCard()
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

    public string? Hint
    {
        get => GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public string? Status
    {
        get => GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
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
