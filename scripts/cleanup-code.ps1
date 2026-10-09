[CmdletBinding()]
param(
    [string[]]$Include = @('src/**/*.cs', 'tests/**/*.cs', 'src/**/*.axaml', 'tests/**/*.axaml', 'src/**/*.fs', 'tests/**/*.fs', 'src/**/*.fsx'),
    [string]$ArtifactsPath = '.tmp/build/cleanup',
    [switch]$UseExistingBuild,
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

$includeValue = $Include -join ';'
$resolvedCachesPath = [IO.Path]::GetFullPath($CachesPath, $root)
$temporaryRoot = [IO.Path]::GetFullPath((Join-Path $root '.tmp')) + [IO.Path]::DirectorySeparatorChar
if (-not $resolvedCachesPath.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Cleanup caches must be inside the repository .tmp directory.'
}
$additionalArguments = @("--caches-home=$resolvedCachesPath")
if ($UseExistingBuild) {
    # Reuse normally built F# metadata; SDK artifacts output can leave InspectCode/CleanupCode
    # unable to resolve cross-language references. The caller must build before using this mode.
    $additionalArguments += '--no-build'
} elseif (-not [string]::IsNullOrWhiteSpace($ArtifactsPath)) {
    $resolvedArtifactsPath = [IO.Path]::GetFullPath($ArtifactsPath, $root)
    $temporaryRoot = [IO.Path]::GetFullPath((Join-Path $root '.tmp')) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedArtifactsPath.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Cleanup build outputs must be inside the repository .tmp directory.'
    }
    $additionalArguments += "--properties=ArtifactsPath=$resolvedArtifactsPath;UseArtifactsOutput=true"
}
& jb cleanupcode (Join-Path $root 'Patchouli.sln') `
    --profile=$profile `
    --include=$includeValue `
    --toolset-path=$msbuildPath `
    --no-updates @additionalArguments

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
