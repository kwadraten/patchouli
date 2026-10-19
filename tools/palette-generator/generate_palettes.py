"""Generate palette assets from @radix-ui/colors.

Outputs:
  1. .agents/palettes/radix_<scale>/DESIGN.md for every Radix scale (light + dark, 12 steps).
  2. src/Patchouli.Host/Themes/UiColorPalettes.Generated.cs — light/dark scale data and the
     palette registry consumed by Patchouli.UI.Themes.UiColorPalettes.

The npm package is downloaded once into .tmp/radix-colors/ (gitignored) and reused.
Re-run after bumping PACKAGE_VERSION:  python tools/palette-generator/generate_palettes.py
"""

import io
import json
import re
import tarfile
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CACHE = ROOT / ".tmp" / "radix-colors"
OUT_DOCS = ROOT / ".agents" / "palettes"
OUT_CS = ROOT / "src" / "Patchouli.Host" / "Themes" / "UiColorPalettes.Generated.cs"
PACKAGE_NAME = "@radix-ui/colors"
PACKAGE_VERSION = "3.0.0"

SCALES = [
    # Grays
    "gray", "mauve", "slate", "sage", "olive", "sand",
    # Colors
    "tomato", "red", "ruby", "crimson", "pink", "plum", "purple",
    "violet", "iris", "indigo", "blue", "cyan", "teal", "jade",
    "green", "grass", "brown", "orange", "sky", "mint", "lime",
    "yellow", "amber", "gold", "bronze",
]

GRAYS = {"gray", "mauve", "slate", "sage", "olive", "sand"}

# Neutral scale paired with each accent, following the Radix Colors pairing guidance.
GRAY_PAIRING = {
    "gray": "gray", "mauve": "mauve", "slate": "slate", "sage": "sage",
    "olive": "olive", "sand": "sand",
    "tomato": "mauve", "red": "mauve", "ruby": "mauve", "crimson": "mauve",
    "pink": "mauve", "plum": "mauve", "purple": "mauve", "violet": "mauve",
    "iris": "slate", "indigo": "slate", "blue": "slate", "cyan": "slate", "sky": "slate",
    "teal": "sage", "jade": "sage", "green": "sage", "mint": "sage",
    "grass": "olive", "lime": "olive",
    "yellow": "sand", "amber": "sand", "orange": "sand", "brown": "sand",
    "gold": "sand", "bronze": "sand",
}

# Bright scales whose solid steps (9/10) need dark foreground text.
DARK_ON_PRIMARY = {"sky", "mint", "lime", "yellow", "amber"}

# The default palette (UiColorPalettes.cs) is the hand-tuned violet scheme and owns the
# "radix-violet" id, so violet is excluded from the generated selectable list.
DEFAULT_SCALE = "violet"

STEP_USE_CASES = [
    (1, "App background"),
    (2, "Subtle background"),
    (3, "UI element background"),
    (4, "Hovered UI element background"),
    (5, "Active / Selected UI element background"),
    (6, "Subtle borders and separators"),
    (7, "UI element border and focus rings"),
    (8, "Hovered UI element border"),
    (9, "Solid backgrounds"),
    (10, "Hovered solid backgrounds"),
    (11, "Low-contrast text"),
    (12, "High-contrast text"),
]


def ensure_package() -> Path:
    package_dir = CACHE / "package"
    if (package_dir / "gray.css").exists():
        return package_dir

    CACHE.mkdir(parents=True, exist_ok=True)
    meta_url = f"https://registry.npmjs.org/{PACKAGE_NAME}/latest"
    with urllib.request.urlopen(meta_url) as response:
        meta = json.load(response)
    tarball_url = meta["dist"]["tarball"]
    with urllib.request.urlopen(tarball_url) as response:
        payload = response.read()
    with tarfile.open(fileobj=io.BytesIO(payload), mode="r:gz") as archive:
        archive.extractall(CACHE, filter="data")
    return package_dir


def parse_scale(path: Path, name: str) -> dict[int, str]:
    """Extract the 12 sRGB hex steps from a Radix CSS file (first block only)."""
    text = path.read_text(encoding="utf-8")
    # Only the first :root/.light/.dark block carries sRGB hex values;
    # the @supports display-p3 block repeats the same steps in P3.
    steps: dict[int, str] = {}
    for match in re.finditer(rf"--{name}-(\d+):\s*(#[0-9a-fA-F]{{6}})\s*;", text):
        step = int(match.group(1))
        steps.setdefault(step, match.group(2).lower())
    if sorted(steps) != list(range(1, 13)):
        raise ValueError(f"{path.name}: expected steps 1-12, got {sorted(steps)}")
    return steps


def title(scale: str) -> str:
    return scale.replace("_", " ").title().replace(" ", "")


def render_design_doc(scale: str, light: dict[int, str], dark: dict[int, str]) -> str:
    name = f"Radix {title(scale)}"
    lines: list[str] = ["---", f"name: {name}", f"source: '{PACKAGE_NAME} {PACKAGE_VERSION}'",
                        "colors:", "  light:"]
    for i in range(1, 13):
        lines.append(f"    {scale}-{i}: '{light[i]}'")
    lines.append("  dark:")
    for i in range(1, 13):
        lines.append(f"    {scale}-{i}: '{dark[i]}'")
    lines.append("---")
    lines.append("")
    kind = "gray" if scale in GRAYS else "color"
    lines.append(f"## {name}")
    lines.append("")
    lines.append(
        f"A 12-step {kind} scale from [Radix Colors](https://www.radix-ui.com/colors) "
        f"(`{PACKAGE_NAME}` {PACKAGE_VERSION}). Each step targets a specific UI use case, so steps can be "
        "composed without hand-tuning: text on steps 11/12 is guaranteed to meet APCA contrast "
        "targets against the background steps (1-5) of the same scale."
    )
    lines.append("")
    lines.append("### Step semantics")
    lines.append("")
    for step, use in STEP_USE_CASES:
        lines.append(f"- **Step {step}** — {use}")
    lines.append("")
    lines.append("### Dark mode")
    lines.append("")
    lines.append(
        "The `dark` token set is a drop-in replacement for the `light` set: apply the same step "
        "numbers and the scale works unchanged on a dark background. The frontmatter above lists "
        "both sets (sRGB hex; P3 wide-gamut variants are available in the source package for "
        "displays that support them)."
    )
    lines.append("")
    lines.append("### Alpha variants")
    lines.append("")
    lines.append(
        f"Every step also ships as an alpha variant (`{scale}-a1` … `{scale}-a12`) for layering "
        "over colored backgrounds. Alpha values are not duplicated here; see the source package's "
        f"`{scale}-alpha.css` / `{scale}-dark-alpha.css`."
    )
    lines.append("")
    if scale in GRAYS:
        lines.append("### Pairing")
        lines.append("")
        lines.append(
            "This is one of the six Radix grays. Pair it with an accent scale that shares its "
            "undertone: `gray` is pure neutral, `mauve` leans purple, `slate` leans blue, "
            "`sage` leans green, `olive` leans yellow-green, `sand` leans warm/yellow."
        )
        lines.append("")
    return "\n".join(lines)


def write_design_docs(package_dir: Path) -> None:
    for scale in SCALES:
        light = parse_scale(package_dir / f"{scale}.css", scale)
        dark = parse_scale(package_dir / f"{scale}-dark.css", scale)
        out_dir = OUT_DOCS / f"radix_{scale}"
        out_dir.mkdir(parents=True, exist_ok=True)
        (out_dir / "DESIGN.md").write_text(render_design_doc(scale, light, dark), encoding="utf-8")
        print(f"wrote {out_dir / 'DESIGN.md'}")


def write_csharp(package_dir: Path) -> None:
    lines = [
        "// <auto-generated />",
        f"// Generated by tools/palette-generator/generate_palettes.py from {PACKAGE_NAME} {PACKAGE_VERSION}.",
        "// Step values: https://www.radix-ui.com/colors — do not edit by hand.",
        "",
        "#nullable enable",
        "",
        "namespace Patchouli.UI.Themes;",
        "",
        "public static partial class UiColorPalettes",
        "{",
        "    // Light-mode steps 1-12 per Radix scale, keyed by scale name.",
        "    private static IReadOnlyDictionary<string, string[]>? _lightScaleSteps;",
        "    private static IReadOnlyDictionary<string, string[]> LightScaleSteps => _lightScaleSteps ??= InitLightScaleSteps();",
        "    private static IReadOnlyDictionary<string, string[]> InitLightScaleSteps() =>",
        "        new Dictionary<string, string[]>(StringComparer.Ordinal)",
        "        {",
    ]
    for scale in SCALES:
        light = parse_scale(package_dir / f"{scale}.css", scale)
        values = ", ".join(f'"{light[i]}"' for i in range(1, 13))
        lines.append(f'            ["{scale}"] = [{values}],')
    lines += [
        "        };",
        "",
        "    // Dark-mode steps 1-12 per Radix scale, keyed by scale name.",
        "    private static IReadOnlyDictionary<string, string[]>? _darkScaleSteps;",
        "    private static IReadOnlyDictionary<string, string[]> DarkScaleSteps => _darkScaleSteps ??= InitDarkScaleSteps();",
        "    private static IReadOnlyDictionary<string, string[]> InitDarkScaleSteps() =>",
        "        new Dictionary<string, string[]>(StringComparer.Ordinal)",
        "        {",
    ]
    for scale in SCALES:
        dark = parse_scale(package_dir / f"{scale}-dark.css", scale)
        values = ", ".join(f'"{dark[i]}"' for i in range(1, 13))
        lines.append(f'            ["{scale}"] = [{values}],')
    lines += [
        "        };",
        "",
        "    // Backwards-compatibility alias for light steps.",
        "    private static IReadOnlyDictionary<string, string[]> ScaleSteps => LightScaleSteps;",
        "",
        "    // Selectable Radix palettes: id, display name, paired gray scale, accent scale,",
        "    // and whether the solid accent steps take dark foreground text.",
        "    private static (string Id, string Name, string Gray, string Accent, bool DarkOnPrimary)[]? _radixPalettes;",
        "    private static (string Id, string Name, string Gray, string Accent, bool DarkOnPrimary)[] RadixPalettes =>",
        "        _radixPalettes ??= InitRadixPalettes();",
        "    private static (string Id, string Name, string Gray, string Accent, bool DarkOnPrimary)[] InitRadixPalettes() =>",
        "        [",
    ]
    for scale in SCALES:
        if scale == DEFAULT_SCALE:
            continue
        palette_id = f"radix-{scale}"
        dark = "true" if scale in DARK_ON_PRIMARY else "false"
        lines.append(
            f'            ("{palette_id}", "Radix {title(scale)}", "{GRAY_PAIRING[scale]}", "{scale}", {dark}),')
    lines += [
        "        ];",
        "",
        "    // Keep existing foregrounds when readable; correct only insufficient button contrast.",
        "    private static string EnsureReadableButtonText(string foreground, string background)",
        "    {",
        "        if (PaletteContrast(foreground, background) >= 4.5) return foreground;",
        "        return PaletteContrast(\"#000000\", background) >= PaletteContrast(\"#FFFFFF\", background)",
        "            ? \"#000000\" : \"#FFFFFF\";",
        "    }",
        "",
        "    private static double PaletteContrast(string foreground, string background)",
        "    {",
        "        double fg = PaletteLuminance(foreground);",
        "        double bg = PaletteLuminance(background);",
        "        return (Math.Max(fg, bg) + 0.05) / (Math.Min(fg, bg) + 0.05);",
        "    }",
        "",
        "    private static double PaletteLuminance(string hex)",
        "    {",
        "        double Channel(int offset)",
        "        {",
        "            double component = Convert.ToInt32(hex.Substring(offset, 2), 16) / 255.0;",
        "            return component <= 0.04045 ? component / 12.92 : Math.Pow((component + 0.055) / 1.055, 2.4);",
        "        }",
        "        return 0.2126 * Channel(1) + 0.7152 * Channel(3) + 0.0722 * Channel(5);",
        "    }",
        "",
        "    // In dark mode use accent background steps and primary for the status bar; preserve light mode.",
        "    private static IReadOnlyDictionary<string, string> ApplyPrimarySurfaces(",
        "        IReadOnlyDictionary<string, string> colors, string accent, bool isDark)",
        "    {",
        "        if (!isDark) return colors;",
        "        string[] steps = DarkScaleSteps[accent];",
        "        string primary = colors[\"PrimaryColor\"];",
        "        return new Dictionary<string, string>(colors, StringComparer.Ordinal)",
        "        {",
        "            [\"SurfaceColor\"] = steps[0],",
        "            [\"SurfaceContainerLowestColor\"] = steps[0],",
        "            [\"SurfaceContainerLowColor\"] = steps[1],",
        "            [\"SurfaceContainerColor\"] = steps[2],",
        "            [\"SurfaceContainerHighColor\"] = steps[3],",
        "            [\"SurfaceContainerHighestColor\"] = steps[4],",
        "            [\"SurfaceVariantColor\"] = steps[2],",
        "            [\"OutlineVariantColor\"] = steps[5],",
        "            [\"InverseSurfaceColor\"] = primary,",
        "            [\"InverseOnSurfaceColor\"] = EnsureReadableButtonText(colors[\"InverseOnSurfaceColor\"], primary)",
        "        };",
        "    }",
        "",
        "    // Builds a dual-mode semantic palette dictionary from Radix Colors step data.",
        "    private static IReadOnlyDictionary<string, string> BuildRadixPaletteColors(",
        "        string gray, string accent, bool darkOnPrimary, bool isDark)",
        "    {",
        "        string[] g = isDark ? DarkScaleSteps[gray] : LightScaleSteps[gray];",
        "        string[] a = isDark ? DarkScaleSteps[accent] : LightScaleSteps[accent];",
        "        string[] red = isDark ? DarkScaleSteps[\"red\"] : LightScaleSteps[\"red\"];",
        "",
        "        // For bright accent scales (sky, mint, lime, yellow, amber), step 9 is bright in both light and dark modes.",
        "        // It requires dark text: in light mode, gray step 12 (g[11]); in dark mode, gray step 1 (g[0]).",
        "        // For other scales, step 9 is solid colored: in light mode, InverseOnSurfaceHex; in dark mode, light text (g[11]).",
        "        string onPrimary = isDark",
        "            ? (darkOnPrimary ? g[0] : g[11])",
        "            : (darkOnPrimary ? g[11] : InverseOnSurfaceHex);",
        "",
        "        onPrimary = EnsureReadableButtonText(onPrimary, a[8]);",
        "        string inverseSurface = isDark ? a[1] : a[11];",
        "        string inverseOnSurface = isDark ? g[11] : InverseOnSurfaceHex;",
        "        string error = isDark ? red[10] : ErrorHex;",
        "        string errorContainer = isDark ? red[2] : ErrorContainerHex;",
        "        string surfaceContainerLowest = isDark ? g[0] : \"#FFFFFF\";",
        "        string surfaceVariant = g[2];",
        "        string onErrorContainer = error;",
        "",
        "        return ApplyPrimarySurfaces(new Dictionary<string, string>(StringComparer.Ordinal)",
        "        {",
        "            [\"SurfaceColor\"] = g[0],",
        "            [\"SurfaceContainerLowestColor\"] = surfaceContainerLowest,",
        "            [\"SurfaceContainerLowColor\"] = g[1],",
        "            [\"SurfaceContainerColor\"] = g[2],",
        "            [\"SurfaceContainerHighColor\"] = g[3],",
        "            [\"SurfaceContainerHighestColor\"] = g[4],",
        "            [\"OnSurfaceColor\"] = g[11],",
        "            [\"OnSurfaceVariantColor\"] = g[10],",
        "            [\"PrimaryColor\"] = a[10],",
        "            [\"PrimaryContainerColor\"] = a[8],",
        "            [\"OnPrimaryColor\"] = onPrimary,",
        "            [\"TertiaryColor\"] = a[2],",
        "            [\"SelectionColor\"] = a[4],",
        "            [\"InverseSurfaceColor\"] = inverseSurface,",
        "            [\"InverseOnSurfaceColor\"] = inverseOnSurface,",
        "            [\"OutlineVariantColor\"] = g[5],",
        "            [\"SurfaceVariantColor\"] = surfaceVariant,",
        "            [\"ErrorColor\"] = error,",
        "            [\"ErrorContainerColor\"] = errorContainer,",
        "            [\"OnErrorContainerColor\"] = onErrorContainer",
        "        }, accent, isDark);",
        "    }",
        "}",
        "",
    ]
    OUT_CS.write_text("\n".join(lines), encoding="utf-8")
    print(f"wrote {OUT_CS}")


def main() -> None:
    package_dir = ensure_package()
    write_design_docs(package_dir)
    write_csharp(package_dir)


if __name__ == "__main__":
    main()
