#Requires -Version 7.0
<#
.SYNOPSIS
    Generate palette assets from @radix-ui/colors.

.DESCRIPTION
    Outputs:
      1. .agents/palettes/radix_<scale>/DESIGN.md for every Radix scale (light + dark, 12 steps).
      2. src/Patchouli.Host/Themes/UiColorPalettes.Generated.cs — light/dark scale data and the
         palette registry consumed by Patchouli.UI.Themes.UiColorPalettes.

    The npm package tarball is downloaded once into .tmp/radix-colors/ (gitignored) and reused.
    Re-run after bumping $packageVersion.

.EXAMPLE
    pwsh scripts/generate-palettes.ps1
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = Split-Path -Parent $PSScriptRoot
$cacheDir = [System.IO.Path]::Combine($root, '.tmp', 'radix-colors')
$outDocs = [System.IO.Path]::Combine($root, '.agents', 'palettes')
$outCs = [System.IO.Path]::Combine($root, 'src', 'Patchouli.Host', 'Themes', 'UiColorPalettes.Generated.cs')
$packageName = '@radix-ui/colors'
$packageVersion = '3.0.0'
$tarballName = "colors-$packageVersion.tgz"
$tarballUrl = "https://registry.npmjs.org/$packageName/-/colors-$packageVersion.tgz"

$scales = @(
    # Grays
    'gray', 'mauve', 'slate', 'sage', 'olive', 'sand',
    # Colors
    'tomato', 'red', 'ruby', 'crimson', 'pink', 'plum', 'purple',
    'violet', 'iris', 'indigo', 'blue', 'cyan', 'teal', 'jade',
    'green', 'grass', 'brown', 'orange', 'sky', 'mint', 'lime',
    'yellow', 'amber', 'gold', 'bronze'
)

$grays = @{
    'gray' = $true; 'mauve' = $true; 'slate' = $true
    'sage' = $true; 'olive' = $true; 'sand' = $true
}

# Neutral scale paired with each accent, following the Radix Colors pairing guidance.
$grayPairing = @{
    'gray' = 'gray'; 'mauve' = 'mauve'; 'slate' = 'slate'; 'sage' = 'sage'
    'olive' = 'olive'; 'sand' = 'sand'
    'tomato' = 'mauve'; 'red' = 'mauve'; 'ruby' = 'mauve'; 'crimson' = 'mauve'
    'pink' = 'mauve'; 'plum' = 'mauve'; 'purple' = 'mauve'; 'violet' = 'mauve'
    'iris' = 'slate'; 'indigo' = 'slate'; 'blue' = 'slate'; 'cyan' = 'slate'; 'sky' = 'slate'
    'teal' = 'sage'; 'jade' = 'sage'; 'green' = 'sage'; 'mint' = 'sage'
    'grass' = 'olive'; 'lime' = 'olive'
    'yellow' = 'sand'; 'amber' = 'sand'; 'orange' = 'sand'; 'brown' = 'sand'
    'gold' = 'sand'; 'bronze' = 'sand'
}

# Bright scales whose solid steps (9/10) need dark foreground text.
$darkOnPrimary = @{ 'sky' = $true; 'mint' = $true; 'lime' = $true; 'yellow' = $true; 'amber' = $true }

# The default palette (UiColorPalettes.cs) is the hand-tuned violet scheme and owns the
# "radix-violet" id, so violet is excluded from the generated selectable list.
$defaultScale = 'violet'

$stepUseCases = @(
    'App background',
    'Subtle background',
    'UI element background',
    'Hovered UI element background',
    'Active / Selected UI element background',
    'Subtle borders and separators',
    'UI element border and focus rings',
    'Hovered UI element border',
    'Solid backgrounds',
    'Hovered solid backgrounds',
    'Low-contrast text',
    'High-contrast text'
)

# Generated assets keep the newline convention of the platform that produces them, matching
# the original Python generator so a Windows run rewrites the checked-in CRLF files unchanged.
$newLine = [System.Environment]::NewLine
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)

function Get-ScaleTitle {
    param([string]$Scale)

    $words = $Scale.Replace('_', ' ').Split(' ')
    $titled = foreach ($word in $words) {
        if ($word.Length -eq 0) { '' } else { $word.Substring(0, 1).ToUpperInvariant() + $word.Substring(1).ToLowerInvariant() }
    }
    return ($titled -join '')
}

function Get-ScaleSteps {
    param([string]$Path, [string]$Name)

    $text = [System.IO.File]::ReadAllText($Path)
    # Only the first :root/.light/.dark block carries sRGB hex values;
    # the @supports display-p3 block repeats the same steps in P3.
    $pattern = '--' + $Name + '-(\d+):\s*(#[0-9a-fA-F]{6})\s*;'
    $steps = [System.Collections.Generic.Dictionary[int, string]]::new()
    foreach ($match in [regex]::Matches($text, $pattern)) {
        $step = [int]$match.Groups[1].Value
        if (-not $steps.ContainsKey($step)) {
            $steps[$step] = $match.Groups[2].Value.ToLowerInvariant()
        }
    }

    $sorted = @($steps.Keys | Sort-Object)
    if ($sorted.Count -ne 12 -or ($sorted -join ',') -ne ((1..12) -join ',')) {
        throw "$(Split-Path -Leaf $Path): expected steps 1-12, got $($sorted -join ', ')"
    }

    return $steps
}

function Write-LinesFile {
    param([string]$Path, [string[]]$Lines)

    [System.IO.File]::WriteAllText($Path, ($Lines -join $newLine), $utf8NoBom)
}

function Resolve-RadixPackage {
    $packageDir = [System.IO.Path]::Combine($cacheDir, 'package')
    if (-not (Test-Path -LiteralPath ([System.IO.Path]::Combine($packageDir, 'gray.css')))) {
        New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null
        $tarballPath = [System.IO.Path]::Combine($cacheDir, $tarballName)
        if (-not (Test-Path -LiteralPath $tarballPath)) {
            Write-Host "Downloading $packageName@$packageVersion..."
            Invoke-WebRequest -Uri $tarballUrl -OutFile $tarballPath
        }

        & tar -xzf $tarballPath -C $cacheDir
        if ($LASTEXITCODE -ne 0) {
            throw "tar failed to extract '$tarballPath' (exit code $LASTEXITCODE)."
        }
    }

    if (-not (Test-Path -LiteralPath ([System.IO.Path]::Combine($packageDir, 'gray.css')))) {
        throw "Radix package was not found at '$packageDir'."
    }

    return $packageDir
}

function New-DesignDocLines {
    param(
        [string]$Scale,
        [System.Collections.Generic.Dictionary[int, string]]$Light,
        [System.Collections.Generic.Dictionary[int, string]]$Dark)

    $name = 'Radix ' + (Get-ScaleTitle $Scale)
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('---')
    $lines.Add("name: $name")
    $lines.Add("source: '$packageName $packageVersion'")
    $lines.Add('colors:')
    $lines.Add('  light:')
    for ($i = 1; $i -le 12; $i++) {
        $lines.Add("    ${Scale}-${i}: '$($Light[$i])'")
    }

    $lines.Add('  dark:')
    for ($i = 1; $i -le 12; $i++) {
        $lines.Add("    ${Scale}-${i}: '$($Dark[$i])'")
    }

    $lines.Add('---')
    $lines.Add('')
    $kind = if ($grays.ContainsKey($Scale)) { 'gray' } else { 'color' }
    $lines.Add("## $name")
    $lines.Add('')
    $description = 'A 12-step {0} scale from [Radix Colors](https://www.radix-ui.com/colors) ' +
                   '(`{1}` {2}). Each step targets a specific UI use case, so steps can be ' +
                   'composed without hand-tuning: text on steps 11/12 is guaranteed to meet APCA contrast ' +
                   'targets against the background steps (1-5) of the same scale.'
    $lines.Add(($description -f $kind, $packageName, $packageVersion))
    $lines.Add('')
    $lines.Add('### Step semantics')
    $lines.Add('')
    for ($i = 0; $i -lt $stepUseCases.Count; $i++) {
        $step = $i + 1
        $lines.Add("- **Step $step** — $($stepUseCases[$i])")
    }

    $lines.Add('')
    $lines.Add('### Dark mode')
    $lines.Add('')
    $lines.Add(
        'The `dark` token set is a drop-in replacement for the `light` set: apply the same step ' +
        'numbers and the scale works unchanged on a dark background. The frontmatter above lists ' +
        'both sets (sRGB hex; P3 wide-gamut variants are available in the source package for ' +
        'displays that support them).')
    $lines.Add('')
    $lines.Add('### Alpha variants')
    $lines.Add('')
    $alpha = 'Every step also ships as an alpha variant (`{0}-a1` … `{0}-a12`) for layering ' +
             'over colored backgrounds. Alpha values are not duplicated here; see the source package''s ' +
             '`{0}-alpha.css` / `{0}-dark-alpha.css`.'
    $lines.Add(($alpha -f $Scale))
    $lines.Add('')
    if ($grays.ContainsKey($Scale)) {
        $lines.Add('### Pairing')
        $lines.Add('')
        $lines.Add(
            'This is one of the six Radix grays. Pair it with an accent scale that shares its ' +
            'undertone: `gray` is pure neutral, `mauve` leans purple, `slate` leans blue, ' +
            '`sage` leans green, `olive` leans yellow-green, `sand` leans warm/yellow.')
        $lines.Add('')
    }

    return $lines.ToArray()
}

function Write-DesignDocs {
    param([string]$PackageDir)

    foreach ($scale in $scales) {
        $light = Get-ScaleSteps -Path ([System.IO.Path]::Combine($PackageDir, "$scale.css")) -Name $scale
        $dark = Get-ScaleSteps -Path ([System.IO.Path]::Combine($PackageDir, "$scale-dark.css")) -Name $scale
        $outDir = [System.IO.Path]::Combine($outDocs, "radix_$scale")
        New-Item -ItemType Directory -Path $outDir -Force | Out-Null
        $designPath = [System.IO.Path]::Combine($outDir, 'DESIGN.md')
        Write-LinesFile -Path $designPath -Lines (New-DesignDocLines -Scale $scale -Light $light -Dark $dark)
        Write-Output "wrote $designPath"
    }
}

function New-CsharpLines {
    param([string]$PackageDir)

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.AddRange([string[]]@(
            '// <auto-generated />',
            ('// Generated by scripts/generate-palettes.ps1 from {0} {1}.' -f $packageName, $packageVersion),
            '// Step values: https://www.radix-ui.com/colors — do not edit by hand.',
            '',
            '#nullable enable',
            '',
            'namespace Patchouli.UI.Themes;',
            '',
            'public static partial class UiColorPalettes',
            '{',
            '    // Light-mode steps 1-12 per Radix scale, keyed by scale name.',
            '    private static IReadOnlyDictionary<string, string[]>? _lightScaleSteps;',
            '    private static IReadOnlyDictionary<string, string[]> LightScaleSteps => _lightScaleSteps ??= InitLightScaleSteps();',
            '    private static IReadOnlyDictionary<string, string[]> InitLightScaleSteps() =>',
            '        new Dictionary<string, string[]>(StringComparer.Ordinal)',
            '        {'
        ))

    foreach ($scale in $scales) {
        $light = Get-ScaleSteps -Path ([System.IO.Path]::Combine($PackageDir, "$scale.css")) -Name $scale
        $values = (1..12 | ForEach-Object { '"' + $light[$_] + '"' }) -join ', '
        $lines.Add('            ["' + $scale + '"] = [' + $values + '],')
    }

    $lines.AddRange([string[]]@(
            '        };',
            '',
            '    // Dark-mode steps 1-12 per Radix scale, keyed by scale name.',
            '    private static IReadOnlyDictionary<string, string[]>? _darkScaleSteps;',
            '    private static IReadOnlyDictionary<string, string[]> DarkScaleSteps => _darkScaleSteps ??= InitDarkScaleSteps();',
            '    private static IReadOnlyDictionary<string, string[]> InitDarkScaleSteps() =>',
            '        new Dictionary<string, string[]>(StringComparer.Ordinal)',
            '        {'
        ))

    foreach ($scale in $scales) {
        $dark = Get-ScaleSteps -Path ([System.IO.Path]::Combine($PackageDir, "$scale-dark.css")) -Name $scale
        $values = (1..12 | ForEach-Object { '"' + $dark[$_] + '"' }) -join ', '
        $lines.Add('            ["' + $scale + '"] = [' + $values + '],')
    }

    $lines.AddRange([string[]]@(
            '        };',
            '',
            '    // Backwards-compatibility alias for light steps.',
            '    private static IReadOnlyDictionary<string, string[]> ScaleSteps => LightScaleSteps;',
            '',
            '    // Selectable Radix palettes: id, display name, paired gray scale, accent scale,',
            '    // and whether the solid accent steps take dark foreground text.',
            '    private static (string Id, string Name, string Gray, string Accent, bool DarkOnPrimary)[]? _radixPalettes;',
            '    private static (string Id, string Name, string Gray, string Accent, bool DarkOnPrimary)[] RadixPalettes =>',
            '        _radixPalettes ??= InitRadixPalettes();',
            '    private static (string Id, string Name, string Gray, string Accent, bool DarkOnPrimary)[] InitRadixPalettes() =>',
            '        ['
        ))

    foreach ($scale in $scales) {
        if ($scale -eq $defaultScale) {
            continue
        }

        $paletteId = "radix-$scale"
        $title = Get-ScaleTitle $scale
        $gray = $grayPairing[$scale]
        $dark = if ($darkOnPrimary.ContainsKey($scale)) { 'true' } else { 'false' }
        $lines.Add('            ("' + $paletteId + '", "Radix ' + $title + '", "' + $gray + '", "' + $scale + '", ' + $dark + '),')
    }

    $lines.AddRange([string[]]@(
            '        ];',
            '',
            '    // Keep existing foregrounds when readable; correct only insufficient button contrast.',
            '    private static string EnsureReadableButtonText(string foreground, string background)',
            '    {',
            '        if (PaletteContrast(foreground, background) >= 4.5) return foreground;',
            '        return PaletteContrast("#000000", background) >= PaletteContrast("#FFFFFF", background)',
            '            ? "#000000" : "#FFFFFF";',
            '    }',
            '',
            '    private static double PaletteContrast(string foreground, string background)',
            '    {',
            '        double fg = PaletteLuminance(foreground);',
            '        double bg = PaletteLuminance(background);',
            '        return (Math.Max(fg, bg) + 0.05) / (Math.Min(fg, bg) + 0.05);',
            '    }',
            '',
            '    private static double PaletteLuminance(string hex)',
            '    {',
            '        double Channel(int offset)',
            '        {',
            '            double component = Convert.ToInt32(hex.Substring(offset, 2), 16) / 255.0;',
            '            return component <= 0.04045 ? component / 12.92 : Math.Pow((component + 0.055) / 1.055, 2.4);',
            '        }',
            '        return 0.2126 * Channel(1) + 0.7152 * Channel(3) + 0.0722 * Channel(5);',
            '    }',
            '',
            '    // In dark mode use accent background steps and primary for the status bar; preserve light mode.',
            '    private static IReadOnlyDictionary<string, string> ApplyPrimarySurfaces(',
            '        IReadOnlyDictionary<string, string> colors, string accent, bool isDark)',
            '    {',
            '        if (!isDark) return colors;',
            '        string[] steps = DarkScaleSteps[accent];',
            '        string primary = colors["PrimaryColor"];',
            '        return new Dictionary<string, string>(colors, StringComparer.Ordinal)',
            '        {',
            '            ["SurfaceColor"] = steps[0],',
            '            ["SurfaceContainerLowestColor"] = steps[0],',
            '            ["SurfaceContainerLowColor"] = steps[1],',
            '            ["SurfaceContainerColor"] = steps[2],',
            '            ["SurfaceContainerHighColor"] = steps[3],',
            '            ["SurfaceContainerHighestColor"] = steps[4],',
            '            ["SurfaceVariantColor"] = steps[2],',
            '            ["OutlineVariantColor"] = steps[5],',
            '            ["InverseSurfaceColor"] = primary,',
            '            ["InverseOnSurfaceColor"] = EnsureReadableButtonText(colors["InverseOnSurfaceColor"], primary)',
            '        };',
            '    }',
            '',
            '    // Builds a dual-mode semantic palette dictionary from Radix Colors step data.',
            '    private static IReadOnlyDictionary<string, string> BuildRadixPaletteColors(',
            '        string gray, string accent, bool darkOnPrimary, bool isDark)',
            '    {',
            '        string[] g = isDark ? DarkScaleSteps[gray] : LightScaleSteps[gray];',
            '        string[] a = isDark ? DarkScaleSteps[accent] : LightScaleSteps[accent];',
            '        string[] red = isDark ? DarkScaleSteps["red"] : LightScaleSteps["red"];',
            '',
            '        // For bright accent scales (sky, mint, lime, yellow, amber), step 9 is bright in both light and dark modes.',
            '        // It requires dark text: in light mode, gray step 12 (g[11]); in dark mode, gray step 1 (g[0]).',
            '        // For other scales, step 9 is solid colored: in light mode, InverseOnSurfaceHex; in dark mode, light text (g[11]).',
            '        string onPrimary = isDark',
            '            ? (darkOnPrimary ? g[0] : g[11])',
            '            : (darkOnPrimary ? g[11] : InverseOnSurfaceHex);',
            '',
            '        onPrimary = EnsureReadableButtonText(onPrimary, a[8]);',
            '        string inverseSurface = isDark ? a[1] : a[11];',
            '        string inverseOnSurface = isDark ? g[11] : InverseOnSurfaceHex;',
            '        string error = isDark ? red[10] : ErrorHex;',
            '        string errorContainer = isDark ? red[2] : ErrorContainerHex;',
            '        string surfaceContainerLowest = isDark ? g[0] : "#FFFFFF";',
            '        string surfaceVariant = g[2];',
            '        string onErrorContainer = error;',
            '',
            '        return ApplyPrimarySurfaces(new Dictionary<string, string>(StringComparer.Ordinal)',
            '        {',
            '            ["SurfaceColor"] = g[0],',
            '            ["SurfaceContainerLowestColor"] = surfaceContainerLowest,',
            '            ["SurfaceContainerLowColor"] = g[1],',
            '            ["SurfaceContainerColor"] = g[2],',
            '            ["SurfaceContainerHighColor"] = g[3],',
            '            ["SurfaceContainerHighestColor"] = g[4],',
            '            ["OnSurfaceColor"] = g[11],',
            '            ["OnSurfaceVariantColor"] = g[10],',
            '            ["PrimaryColor"] = a[10],',
            '            ["PrimaryContainerColor"] = a[8],',
            '            ["OnPrimaryColor"] = onPrimary,',
            '            ["TertiaryColor"] = a[2],',
            '            ["SelectionColor"] = a[4],',
            '            ["InverseSurfaceColor"] = inverseSurface,',
            '            ["InverseOnSurfaceColor"] = inverseOnSurface,',
            '            ["OutlineVariantColor"] = g[5],',
            '            ["SurfaceVariantColor"] = surfaceVariant,',
            '            ["ErrorColor"] = error,',
            '            ["ErrorContainerColor"] = errorContainer,',
            '            ["OnErrorContainerColor"] = onErrorContainer',
            '        }, accent, isDark);',
            '    }',
            '}',
            ''
        ))

    return $lines.ToArray()
}

function Write-CsharpFile {
    param([string]$PackageDir)

    Write-LinesFile -Path $outCs -Lines (New-CsharpLines -PackageDir $PackageDir)
    Write-Output "wrote $outCs"
}

$packageDir = Resolve-RadixPackage
Write-DesignDocs -PackageDir $packageDir
Write-CsharpFile -PackageDir $packageDir
