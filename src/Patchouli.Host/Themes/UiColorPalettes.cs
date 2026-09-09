namespace Patchouli.UI.Themes;

/// <summary>A selectable UI color palette: semantic color key (e.g. "SurfaceColor") → hex value.</summary>
public sealed record UiColorPalette(string Id, string DisplayName, IReadOnlyDictionary<string, string> Colors);

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
        "ErrorColor",
        "ErrorContainerColor"
    ];

    // Static constructor: field initializers across partial declarations have no defined order,
    // so BuildAll must not run from a property initializer before ScaleSteps/RadixPalettes are set.
    static UiColorPalettes()
    {
        All = BuildAll();
    }

    public static IReadOnlyList<UiColorPalette> All { get; }

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
            palettes.Add(new UiColorPalette(id, name,
                FromRadixSteps(ScaleSteps[gray], ScaleSteps[accent], darkOnPrimary)));
        }

        return palettes.ToArray();
    }

    // Radix step semantics: gray 1-5 are background tiers, 6 is a subtle border, 11/12 are text;
    // accent 3 is a soft tint, 5 is the list-selection tint (mid-light, keeps dark text readable),
    // 9 is the solid action color, 11 is the accent text color, and 12 is the deep accent used
    // for the dark status bar.
    private static IReadOnlyDictionary<string, string> FromRadixSteps(string[] gray, string[] accent,
        bool darkOnPrimary)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SurfaceColor"] = gray[0],
            ["SurfaceContainerLowColor"] = gray[1],
            ["SurfaceContainerColor"] = gray[2],
            ["SurfaceContainerHighColor"] = gray[3],
            ["SurfaceContainerHighestColor"] = gray[4],
            ["OnSurfaceColor"] = gray[11],
            ["OnSurfaceVariantColor"] = gray[10],
            ["PrimaryColor"] = accent[10],
            ["PrimaryContainerColor"] = accent[8],
            ["OnPrimaryColor"] = darkOnPrimary ? gray[11] : InverseOnSurfaceHex,
            ["TertiaryColor"] = accent[2],
            ["SelectionColor"] = accent[4],
            ["InverseSurfaceColor"] = accent[11],
            ["InverseOnSurfaceColor"] = InverseOnSurfaceHex,
            ["OutlineVariantColor"] = gray[5],
            ["ErrorColor"] = ErrorHex,
            ["ErrorContainerColor"] = ErrorContainerHex
        };
    }

    // The default palette: the original hand-tuned violet design, kept byte-identical to the shipped
    // default except InverseSurface, which uses the Radix violet scale's deepest step (dark status bar).
    private static readonly UiColorPalette RadixViolet = new(
        DefaultPaletteId,
        "Radix Violet",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SurfaceColor"] = "#FBF9F8",
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
            ["ErrorColor"] = ErrorHex,
            ["ErrorContainerColor"] = ErrorContainerHex
        });
}
