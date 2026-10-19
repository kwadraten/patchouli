using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Patchouli.UI.Themes;

/// <summary>Applies a <see cref="UiColorPalette" /> and <see cref="ThemeVariant"/> to the running app by mutating
/// the shared theme brush instances in place, so both {DynamicResource} and {StaticResource} consumers repaint live
/// without a restart. No-ops headless (unit tests).</summary>
public static class ThemePaletteApplier
{
    private static readonly object Gate = new();
    private static Application? _watchedApp;
    private static EventHandler? _variantChangedHandler;

    public static string CurrentPaletteId { get; private set; } = UiColorPalettes.DefaultPaletteId;
    public static string CurrentThemeMode { get; private set; } = UiPreferences.ThemeModeSystem;
    public static bool CurrentEffectiveIsDark { get; private set; }

    public static void Apply(string? paletteId)
    {
        Apply(paletteId, CurrentThemeMode);
    }

    public static void Apply(string? paletteId, string? themeMode)
    {
        string resolvedPaletteId = UiColorPalettes.ResolveId(paletteId ?? CurrentPaletteId);
        string normalizedMode = UiPreferences.NormalizeThemeMode(themeMode ?? CurrentThemeMode);

        CurrentPaletteId = resolvedPaletteId;
        CurrentThemeMode = normalizedMode;

        if (Application.Current is not { } app)
        {
            return;
        }

        AttachWatcher(app);

        ThemeVariant requestedVariant = normalizedMode switch
        {
            UiPreferences.ThemeModeLight => ThemeVariant.Light,
            UiPreferences.ThemeModeDark => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };

        if (app.RequestedThemeVariant != requestedVariant)
        {
            app.RequestedThemeVariant = requestedVariant;
        }

        bool isDark = ResolveEffectiveIsDark(app, normalizedMode);
        CurrentEffectiveIsDark = isDark;

        UiColorPalette palette = UiColorPalettes.Resolve(resolvedPaletteId);
        IReadOnlyDictionary<string, string> colors = palette.GetColors(isDark);

        foreach ((string colorKey, string hex) in colors)
        {
            Color color = Color.Parse(hex);
            app.Resources[colorKey] = color;
            string brushKey = colorKey.EndsWith("Color", StringComparison.Ordinal)
                ? string.Concat(colorKey.AsSpan(0, colorKey.Length - "Color".Length), "Brush")
                : colorKey + "Brush";

            if (app.TryFindResource(brushKey, out object? resource) && resource is SolidColorBrush brush)
            {
                if (brush.Color != color)
                {
                    brush.Color = color;
                }

                app.Resources[brushKey] = brush;
            }
            else
            {
                app.Resources[brushKey] = new SolidColorBrush(color);
            }
        }
    }

    public static bool ResolveEffectiveIsDark(Application? app, string? themeMode)
    {
        string normalized = UiPreferences.NormalizeThemeMode(themeMode);
        if (normalized == UiPreferences.ThemeModeLight)
        {
            return false;
        }

        if (normalized == UiPreferences.ThemeModeDark)
        {
            return true;
        }

        return app?.ActualThemeVariant == ThemeVariant.Dark;
    }

    /// <summary>Attaches a theme variant change watcher to the given application instance.</summary>
    public static void AttachWatcher(Application app)
    {
        ArgumentNullException.ThrowIfNull(app);
        lock (Gate)
        {
            if (ReferenceEquals(_watchedApp, app))
            {
                return;
            }

            DetachWatcher();

            _watchedApp = app;
            _variantChangedHandler = OnActualThemeVariantChanged;
            app.ActualThemeVariantChanged += _variantChangedHandler;
        }
    }

    /// <summary>Detaches the watcher and unregisters the event handler from the watched application.</summary>
    public static void DetachWatcher()
    {
        lock (Gate)
        {
            if (_watchedApp is not null && _variantChangedHandler is not null)
            {
                _watchedApp.ActualThemeVariantChanged -= _variantChangedHandler;
            }

            _watchedApp = null;
            _variantChangedHandler = null;
        }
    }

    private static void OnActualThemeVariantChanged(object? sender, EventArgs e)
    {
        // When user explicitly selected light or dark, system theme variant changes must not override the preference.
        if (string.Equals(CurrentThemeMode, UiPreferences.ThemeModeSystem, StringComparison.OrdinalIgnoreCase))
        {
            Apply(CurrentPaletteId, CurrentThemeMode);
        }
    }
}
