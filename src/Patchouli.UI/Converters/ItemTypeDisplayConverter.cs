using System.Globalization;
using Avalonia.Data.Converters;
using Patchouli.Core.Bibliography;

namespace Patchouli.UI.Converters;

/// <summary>Displays English CSL item-type keys as their Chinese display names;
/// unknown keys fall back to the raw key.</summary>
public sealed class ItemTypeDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return CslItemTypeDisplayNames.For(value as string);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
