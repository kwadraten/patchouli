namespace Patchouli.UI.Themes;

/// <summary>A selectable UI color palette: semantic color key (e.g. "SurfaceColor") → hex value.</summary>
public sealed record UiColorPalette
{
    public string Id { get; init; }
    public string DisplayName { get; init; }
    public IReadOnlyDictionary<string, string> LightColors { get; }
    public IReadOnlyDictionary<string, string> DarkColors { get; }
    public bool HasExplicitLight { get; }
    public bool HasExplicitDark { get; }

    /// <summary>Backward-compatible property returning LightColors.</summary>
    public IReadOnlyDictionary<string, string> Colors => LightColors;

    public UiColorPalette(string id, string displayName, IReadOnlyDictionary<string, string> colors)
        : this(id, displayName, colors, null)
    {
    }

    public UiColorPalette(string id, string displayName,
        IReadOnlyDictionary<string, string>? lightColors,
        IReadOnlyDictionary<string, string>? darkColors)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        if (lightColors is null && darkColors is null)
        {
            throw new ArgumentException("A palette must specify at least light colors or dark colors.");
        }

        Id = id;
        DisplayName = displayName;
        HasExplicitLight = lightColors is not null;
        HasExplicitDark = darkColors is not null;

        if (lightColors is not null && darkColors is not null)
        {
            LightColors = lightColors;
            DarkColors = darkColors;
        }
        else if (lightColors is not null)
        {
            LightColors = lightColors;
            DarkColors = UiColorPalettes.InvertColors(lightColors);
        }
        else
        {
            DarkColors = darkColors!;
            LightColors = UiColorPalettes.InvertColors(darkColors!);
        }
    }

    public IReadOnlyDictionary<string, string> GetColors(bool isDark)
    {
        return isDark ? DarkColors : LightColors;
    }
}

/// <summary>Built-in UI palettes. The default mirrors Themes/RadixViolet.axaml; the rest are
/// composed from Radix Colors scales (see UiColorPalettes.Generated.cs and .agents/palettes/).</summary>
public static partial class UiColorPalettes
{
    public const string DefaultPaletteId = "radix-violet";

    // Neutral tokens that stay constant across palettes. InverseSurface (the dark status bar)
    // is palette-derived instead: the accent scale's deepest step.
    private const string InverseOnSurfaceHex = "#F2F0F0";
    private const string ErrorHex = "#BA1A1A";
    private const string ErrorContainerHex = "#FFDAD6";

    // Semantic keys every palette must provide. Mirrored onto the brushes in
    // RadixViolet.axaml by ThemePaletteApplier ("XxxColor" → "XxxBrush").
    public static readonly IReadOnlyList<string> SemanticColorKeys =
    [
        "SurfaceColor",
        "SurfaceContainerLowestColor",
        "SurfaceContainerLowColor",
        "SurfaceContainerColor",
        "SurfaceContainerHighColor",
        "SurfaceContainerHighestColor",
        "OnSurfaceColor",
        "OnSurfaceVariantColor",
        "PrimaryColor",
        "PrimaryContainerColor",
        "OnPrimaryColor",
        "TertiaryColor",
        "SelectionColor",
        "InverseSurfaceColor",
        "InverseOnSurfaceColor",
        "OutlineVariantColor",
        "SurfaceVariantColor",
        "ErrorColor",
        "ErrorContainerColor",
        "OnErrorContainerColor"
    ];

    // Static constructor: field initializers across partial declarations have no defined order,
    // so BuildAll and RadixViolet must not run from a field/property initializer before ScaleSteps/RadixPalettes are set.
    static UiColorPalettes()
    {
        RadixViolet = CreateRadixViolet();
        All = BuildAll();
    }

    private static readonly UiColorPalette RadixViolet;

    public static IReadOnlyList<UiColorPalette> All { get; }

    /// <summary>Inverts a hex color preserving its alpha channel using R' = 255 - R, G' = 255 - G, B' = 255 - B.</summary>
    public static string InvertHexColor(string hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hex);
        string clean = hex.Trim();
        if (clean.StartsWith('#'))
        {
            clean = clean[1..];
        }

        byte a = 255;
        byte r;
        byte g;
        byte b;

        if (clean.Length == 3) // RGB
        {
            r = Convert.ToByte(new string(clean[0], 2), 16);
            g = Convert.ToByte(new string(clean[1], 2), 16);
            b = Convert.ToByte(new string(clean[2], 2), 16);
        }
        else if (clean.Length == 4) // ARGB
        {
            a = Convert.ToByte(new string(clean[0], 2), 16);
            r = Convert.ToByte(new string(clean[1], 2), 16);
            g = Convert.ToByte(new string(clean[2], 2), 16);
            b = Convert.ToByte(new string(clean[3], 2), 16);
        }
        else if (clean.Length == 6) // RRGGBB
        {
            r = Convert.ToByte(clean[..2], 16);
            g = Convert.ToByte(clean[2..4], 16);
            b = Convert.ToByte(clean[4..6], 16);
        }
        else if (clean.Length == 8) // AARRGGBB
        {
            a = Convert.ToByte(clean[..2], 16);
            r = Convert.ToByte(clean[2..4], 16);
            g = Convert.ToByte(clean[4..6], 16);
            b = Convert.ToByte(clean[6..8], 16);
        }
        else
        {
            return hex;
        }

        byte invR = (byte)(255 - r);
        byte invG = (byte)(255 - g);
        byte invB = (byte)(255 - b);

        return a == 255
            ? $"#{invR:X2}{invG:X2}{invB:X2}"
            : $"#{a:X2}{invR:X2}{invG:X2}{invB:X2}";
    }

    /// <summary>Inverts all entries in a color dictionary using <see cref="InvertHexColor"/>.</summary>
    public static IReadOnlyDictionary<string, string> InvertColors(IReadOnlyDictionary<string, string> colors)
    {
        ArgumentNullException.ThrowIfNull(colors);
        Dictionary<string, string> inverted = new(colors.Count, StringComparer.Ordinal);
        foreach ((string key, string hex) in colors)
        {
            inverted[key] = InvertHexColor(hex);
        }

        return inverted;
    }

    /// <summary>Falls back to the default palette for null or unknown ids.</summary>
    public static string ResolveId(string? paletteId)
    {
        return All.Any(palette => palette.Id == paletteId) ? paletteId! : DefaultPaletteId;
    }

    public static UiColorPalette Resolve(string? paletteId)
    {
        string id = ResolveId(paletteId);
        return All.First(palette => palette.Id == id);
    }

    private static UiColorPalette[] BuildAll()
    {
        List<UiColorPalette> palettes = [RadixViolet];
        foreach ((string id, string name, string gray, string accent, bool darkOnPrimary) in RadixPalettes)
        {
            IReadOnlyDictionary<string, string> light =
                BuildRadixPaletteColors(gray, accent, darkOnPrimary, false);
            IReadOnlyDictionary<string, string> dark =
                BuildRadixPaletteColors(gray, accent, darkOnPrimary, true);
            palettes.Add(new UiColorPalette(id, name, light, dark));
        }

        return palettes.ToArray();
    }

    // The default palette: the original hand-tuned violet design, kept byte-identical to the shipped
    // default except InverseSurface, which uses the Radix violet scale's deepest step (dark status bar).
    private static UiColorPalette CreateRadixViolet()
    {
        return new UiColorPalette(
            DefaultPaletteId,
            "Radix Violet",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SurfaceColor"] = "#FBF9F8",
                ["SurfaceContainerLowestColor"] = "#FFFFFF",
                ["SurfaceContainerLowColor"] = "#F5F3F3",
                ["SurfaceContainerColor"] = "#EFEDED",
                ["SurfaceContainerHighColor"] = "#E9E8E7",
                ["SurfaceContainerHighestColor"] = "#E4E2E2",
                ["OnSurfaceColor"] = "#1B1C1C",
                ["OnSurfaceVariantColor"] = "#484553",
                ["PrimaryColor"] = "#553BB5",
                ["PrimaryContainerColor"] = "#6E56CF",
                ["OnPrimaryColor"] = InverseOnSurfaceHex,
                ["TertiaryColor"] = "#EFE8FF",
                ["SelectionColor"] = "#D4CAFE",
                ["InverseSurfaceColor"] = "#2F265F",
                ["InverseOnSurfaceColor"] = InverseOnSurfaceHex,
                ["OutlineVariantColor"] = "#CAC4D5",
                ["SurfaceVariantColor"] = "#EFEDED",
                ["ErrorColor"] = ErrorHex,
                ["ErrorContainerColor"] = ErrorContainerHex,
                ["OnErrorContainerColor"] = ErrorHex
            },
            BuildRadixPaletteColors("mauve", "violet", false, true));
    }
}
