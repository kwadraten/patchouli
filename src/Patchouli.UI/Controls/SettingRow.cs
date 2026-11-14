using Avalonia;
using Avalonia.Controls;

namespace Patchouli.UI.Controls;

/// <summary>Shared label/control row. Labels have a fixed leading column; supplemental text is a
/// tooltip instead of a prose paragraph.</summary>
public class SettingRow : ContentControl
{
    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<SettingRow, string?>(nameof(Label));

    public static readonly StyledProperty<string?> HintProperty =
        AvaloniaProperty.Register<SettingRow, string?>(nameof(Hint));

    public static readonly StyledProperty<double> LabelWidthProperty =
        AvaloniaProperty.Register<SettingRow, double>(nameof(LabelWidth), 180);

    public SettingRow()
    {
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center;
    }

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string? Hint
    {
        get => GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public double LabelWidth
    {
        get => GetValue(LabelWidthProperty);
        set => SetValue(LabelWidthProperty, value);
    }
}
