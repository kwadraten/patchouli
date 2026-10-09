[CmdletBinding()]
param(
    [string]$ArtifactsPath = '.tmp/build/inspection',
    [switch]$UseExistingBuild,
    [string]$CachesPath = '.tmp/build/inspection-caches'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$expectedVersion = '2026.1.4'
$artifactDirectory = Join-Path $root '.tmp'
$reportPath = Join-Path $artifactDirectory 'inspectcode.sarif'
$resolvedArtifactsPath = [IO.Path]::GetFullPath($ArtifactsPath, $root)
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
$resolvedCachesPath = [IO.Path]::GetFullPath($CachesPath, $root)
if (-not $resolvedCachesPath.StartsWith($artifactDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Inspection caches must be inside the repository .tmp directory.'
}
$additionalArguments = @("--caches-home=$resolvedCachesPath")
if ($UseExistingBuild) {
    # InspectCode cannot resolve F# project metadata reliably in SDK artifacts output. After a
    # normal build, use its resolved assemblies without triggering another solution build.
    $additionalArguments += '--no-build'
} else {
    $additionalArguments += "--properties=ArtifactsPath=$resolvedArtifactsPath;UseArtifactsOutput=true"
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
