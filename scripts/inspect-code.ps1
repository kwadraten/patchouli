[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$expectedVersion = '2026.1.4'
$artifactDirectory = Join-Path $root 'artifacts'
$reportPath = Join-Path $artifactDirectory 'inspectcode.sarif'
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
& jb inspectcode (Join-Path $root 'Patchouli.sln') `
    --toolset-path=$msbuildPath `
    --output=$reportPath `
    --format=Sarif `
    --no-updates

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
