using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Patchouli.UI.Themes;

/// <summary>Applies a <see cref="UiColorPalette" /> to the running app by mutating the shared theme
/// brush instances in place, so both {DynamicResource} and {StaticResource} consumers (which hold the
/// same SolidColorBrush instances) repaint live without a restart. No-ops headless (unit tests).</summary>
public static class ThemePaletteApplier
{
    public static void Apply(string? paletteId)
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        UiColorPalette palette = UiColorPalettes.Resolve(paletteId);
        foreach ((string colorKey, string hex) in palette.Colors)
        {
            Color color = Color.Parse(hex);
            app.Resources[colorKey] = color;
            string brushKey = colorKey.EndsWith("Color", StringComparison.Ordinal)
                ? string.Concat(colorKey.AsSpan(0, colorKey.Length - "Color".Length), "Brush")
                : colorKey + "Brush";
            if (app.TryFindResource(brushKey, out object? resource) && resource is SolidColorBrush brush &&
                brush.Color != color)
            {
                brush.Color = color;
            }
        }
    }
}
