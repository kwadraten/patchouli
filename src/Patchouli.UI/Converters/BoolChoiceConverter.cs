using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Patchouli.UI.Converters;

/// <summary>
/// Converts a boolean to one of two choices provided in the parameter as "trueChoice|falseChoice".
/// When the target type is <see cref="IBrush"/>, choices are resolved as application resources or
/// fall back to transparent.
/// </summary>
public sealed class BoolChoiceConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (parameter is not string choices)
        {
            return AvaloniaProperty.UnsetValue;
        }

        string[] parts = choices.Split('|');
        string selected = value is true && parts.Length > 0
            ? parts[0]
            : parts.Length > 1
                ? parts[1]
                : string.Empty;

        if (targetType == typeof(IBrush) || targetType == typeof(Brush))
        {
            if (App.Current?.Resources.TryGetValue(selected, out object? resource) == true && resource is IBrush brush)
            {
                return brush;
            }

            return Brushes.Transparent;
        }

        return selected;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return AvaloniaProperty.UnsetValue;
    }
}
