[CmdletBinding()]
param(
    [ValidateSet('smoke', 'full')]
    [string]$Profile = 'smoke',
    [switch]$Check,
    [switch]$FullBudgetCheck,
    [switch]$Ui,
    [switch]$UiBudgetCheck,
    [int]$Iterations = 0,
    [string]$Baseline,
    [string]$OutputDirectory = '',
    [int]$Runs = 3,
    [int]$Items = 0,
    [int]$PagesPerItem = 0,
    [int]$BoxesPerPage = 0
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $root 'artifacts\perf'
}
if ($Iterations -le 0) {
    $Iterations = if ($Profile -eq 'full') { 10 } else { 5 }
}

function Invoke-PerfRun {
    param([string]$Suffix)
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $runArgs = @('--profile', $Profile, '--output', (Join-Path $OutputDirectory "$Profile-$Suffix.json"), '--quiet', '--iterations', $Iterations)
    if ($Items -gt 0) {
        $runArgs += @('--items', $Items)
    }
    if ($PagesPerItem -gt 0) {
        $runArgs += @('--pages-per-item', $PagesPerItem)
    }
    if ($BoxesPerPage -gt 0) {
        $runArgs += @('--boxes-per-page', $BoxesPerPage)
    }
    if ($Ui) {
        $runArgs += '--ui'
    }
    if ($UiBudgetCheck) {
        $runArgs += '--enforce-ui-budgets'
    }
    if ($Check) {
        $baselinePath = if ($Baseline) { $Baseline } else { Join-Path $root ".agents\perf\baseline.$Profile.json" }
        $runArgs += @('--check', '--baseline', $baselinePath)
    }
    & dotnet run --project (Join-Path $root 'tests\Patchouli.Performance') -- $runArgs
    return $LASTEXITCODE
}

if ($FullBudgetCheck -or $UiBudgetCheck) {
    $results = @()
    for ($index = 1; $index -le $Runs; $index++) {
        Write-Host "[run-perf] budget check run $index of $Runs ($Profile) ui=$Ui uiBudget=$UiBudgetCheck"
        $results += Invoke-PerfRun -Suffix "budget$index"
    }
    $passed = @($results | Where-Object { $_ -eq 0 }).Count
    if ($passed -eq 0) {
        Write-Host "[run-perf] All $Runs consecutive runs exceeded the performance budget. Build/release check FAILED."
        exit 1
    }

    Write-Host "[run-perf] $passed of $Runs runs were within budget (a flaky overshoot does not fail the gate)."
    exit 0
}

$exitCode = Invoke-PerfRun -Suffix 'run'
exit $exitCode
