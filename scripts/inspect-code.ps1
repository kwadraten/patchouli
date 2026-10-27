[CmdletBinding()]
param(
    [string]$ArtifactsPath = '.tmp/build/inspection',
    # The JetBrains CLI host has no F# source model (see Directory.Build.targets); exclude
    # F# sources explicitly so the documented inspection scope is C# and Avalonia XAML.
    # .fs sources are gated by the F# compiler (TreatWarningsAsErrors in each .fsproj) and
    # F# unit tests; .fsx scripts are gated by the production real-FCS policy checks and
    # script execution tests.
    [string[]]$Exclude = @('src/**/*.fs', 'tests/**/*.fs', 'src/**/*.fsx', 'tests/**/*.fsx'),
    [switch]$UseExistingBuild,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [string]$CachesPath = '.tmp/build/inspection-caches'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$expectedVersion = '2026.1.4'
$artifactDirectory = Join-Path $root '.tmp'
$reportPath = Join-Path $artifactDirectory 'inspectcode.sarif'
$resolvedArtifactsPath = [IO.Path]::GetFullPath((Join-Path $root $ArtifactsPath))
if (-not $resolvedArtifactsPath.StartsWith($artifactDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Inspection build outputs must be inside the repository .tmp directory.'
}
$sdkVersion = (& dotnet --version).Trim()
$msbuildPath = Join-Path $env:ProgramFiles "dotnet\sdk\$sdkVersion\MSBuild.dll"

if (-not (Test-Path -LiteralPath $msbuildPath)) {
    throw "Could not find the MSBuild.dll for .NET SDK $sdkVersion at '$msbuildPath'."
}

$actualVersion = (& jb inspectcode --version | Select-String '^Version:' | ForEach-Object { $_.Line.Split(':', 2)[1].Trim() })
if ($actualVersion -ne $expectedVersion) {
    throw "JetBrains Inspect Code $expectedVersion is required; found '$actualVersion'."
}

New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null
$resolvedCachesPath = [IO.Path]::GetFullPath((Join-Path $root $CachesPath))
if (-not $resolvedCachesPath.StartsWith($artifactDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Inspection caches must be inside the repository .tmp directory.'
}
$excludeItems = @($Exclude | ForEach-Object { $_ -split '[,;]' })
$excludeValue = ($excludeItems | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_.Trim().Replace('/', [IO.Path]::DirectorySeparatorChar) }) -join ';'
$additionalArguments = @("--caches-home=$resolvedCachesPath")
if (-not [string]::IsNullOrWhiteSpace($excludeValue)) {
    $additionalArguments += "--exclude=$excludeValue"
}
if ($UseExistingBuild) {
    # InspectCode cannot resolve F# project metadata reliably in SDK artifacts output. After a
    # normal build, use its resolved assemblies without triggering another solution build.
    $additionalArguments += '--no-build'
    $additionalArguments += "--properties=Configuration=$Configuration;PatchouliUseBuiltExtensionAssembly=true"
} else {
    $additionalArguments += "--properties=Configuration=$Configuration;ArtifactsPath=$resolvedArtifactsPath;UseArtifactsOutput=true"
}
& jb inspectcode (Join-Path $root 'Patchouli.sln') `
    --toolset-path=$msbuildPath `
    --output=$reportPath `
    --format=Sarif `
    --no-updates @additionalArguments

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$blockingRuleIds = @(
    '.XAMLErrors',
    'Xaml.PossibleNullReferenceException',
    'AccessToDisposedClosure',
    'AccessToModifiedClosure',
    'AsyncVoidMethod',
    'AsyncVoidLambda',
    'EmptyGeneralCatchClause',
    'PossibleMultipleEnumeration',
    'ObjectDisposed'
)
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
$results = @($report.runs | ForEach-Object { $_.results })
$blockingResults = @($results | Where-Object {
    $_.level -eq 'error' -or
    $_.ruleId -like 'CSharpWarnings::*' -or
    $_.ruleId -in $blockingRuleIds
})

if ($blockingResults.Count -eq 0) {
    Write-Host "InspectCode passed. Report: $reportPath"
    exit 0
}

Write-Host "InspectCode found $($blockingResults.Count) blocking issue(s). Report: $reportPath"
foreach ($result in $blockingResults) {
    $location = $result.locations[0].physicalLocation
    Write-Host "$($result.ruleId): $($location.artifactLocation.uri):$($location.region.startLine): $($result.message.text)"
}
throw 'InspectCode blocking issues found.'
