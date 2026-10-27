[CmdletBinding()]
param(
    # The JetBrains CLI host has no F# source model (see Directory.Build.targets), so the
    # default scope is C# and Avalonia XAML only. .fs sources are gated by the F# compiler
    # (TreatWarningsAsErrors in each .fsproj) and F# unit tests; .fsx scripts are gated by
    # the production real-FCS policy checks and script execution tests. Fantomas is a
    # formatting tool only, never a correctness gate.
    [string[]]$Include = @('src/**/*.cs', 'tests/**/*.cs', 'scripts/**/*.cs', 'src/**/*.axaml', 'tests/**/*.axaml'),
    [string]$ArtifactsPath = '.tmp/build/cleanup',
    [switch]$UseExistingBuild,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [string]$CachesPath = '.tmp/build/cleanup-caches'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$expectedVersion = '2026.1.4'
$profile = 'Built-in: Reformat & Apply Syntax Style'
$sdkVersion = (& dotnet --version).Trim()
$msbuildPath = Join-Path $env:ProgramFiles "dotnet\sdk\$sdkVersion\MSBuild.dll"

if (-not (Test-Path -LiteralPath $msbuildPath)) {
    throw "Could not find the MSBuild.dll for .NET SDK $sdkVersion at '$msbuildPath'."
}

$actualVersion = (& jb cleanupcode --version | Select-String '^Version:' | ForEach-Object { $_.Line.Split(':', 2)[1].Trim() })
if ($actualVersion -ne $expectedVersion) {
    throw "JetBrains Cleanup Code $expectedVersion is required; found '$actualVersion'."
}

$includeItems = @($Include | ForEach-Object { $_ -split '[,;]' })
$includeValue = ($includeItems | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_.Trim().Replace('/', [IO.Path]::DirectorySeparatorChar) }) -join ';'
$resolvedCachesPath = [IO.Path]::GetFullPath((Join-Path $root $CachesPath))
$temporaryRoot = [IO.Path]::GetFullPath((Join-Path $root '.tmp')) + [IO.Path]::DirectorySeparatorChar
if (-not $resolvedCachesPath.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Cleanup caches must be inside the repository .tmp directory.'
}
$additionalArguments = @("--caches-home=$resolvedCachesPath")
if ($UseExistingBuild) {
    # Reuse normally built F# metadata; SDK artifacts output can leave InspectCode/CleanupCode
    # unable to resolve cross-language references. The caller must build before using this mode.
    $additionalArguments += '--no-build'
    $additionalArguments += "--properties=Configuration=$Configuration;PatchouliUseBuiltExtensionAssembly=true"
} elseif (-not [string]::IsNullOrWhiteSpace($ArtifactsPath)) {
    $resolvedArtifactsPath = [IO.Path]::GetFullPath((Join-Path $root $ArtifactsPath))
    $temporaryRoot = [IO.Path]::GetFullPath((Join-Path $root '.tmp')) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedArtifactsPath.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Cleanup build outputs must be inside the repository .tmp directory.'
    }
    $additionalArguments += "--properties=Configuration=$Configuration;ArtifactsPath=$resolvedArtifactsPath;UseArtifactsOutput=true"
}
& jb cleanupcode (Join-Path $root 'Patchouli.sln') `
    --profile=$profile `
    --include=$includeValue `
    --toolset-path=$msbuildPath `
    --no-updates @additionalArguments

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
