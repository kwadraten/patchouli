[CmdletBinding()]
param(
    # Project under test, relative to the repo root or absolute. The script
    # always builds this project explicitly — never assume the solution covers it.
    [string]$Project = 'tests/Patchouli.Tests/Patchouli.Tests.csproj',

    # Suite bucket. 'All' runs one invocation with no implicit filter. 'Full'
    # runs correctness batches sequentially. 'Fast' excludes explicit performance
    # workloads. 'Performance' runs those workloads only. 'Diagnostic' runs Full's
    # batches with blame-hang mini dumps enabled by default.
    [ValidateSet('All', 'Full', 'Fast', 'Performance', 'Diagnostic')]
    [string]$Suite = 'All',

    [string]$Configuration = 'Debug',

    # Optional RID forwarded to restore/build/test (-r). Changes the bin layout.
    [string]$Rid = '',

    # VSTest --filter expression, e.g. 'FullyQualifiedName~DesktopInstanceActivation'.
    [string]$Filter = '',

    # Optional file listing the expected case multiset (one 'project::name' or
    # bare 'name' per line; '#' comments allowed). Compared after the run —
    # missing cases fail the run even when the runner exits 0.
    [string]$ExpectedCasesFile = '',

    # Unique run identifier; defaults to timestamp + short GUID.
    [string]$RunId = '',

    # Root for run outputs. Must stay under the repo .tmp directory.
    [string]$ResultsRoot = '.tmp/test-runs',

    # Bucket manifest: trait values, batch filters, required integration cases
    # and the skip allowlist. Required for every suite except 'All'.
    [string]$SuiteManifest = 'tests/test-suites.json',

    [switch]$SkipRestore,
    [switch]$NoBuild,

    # Optional VSTest hang watchdog, e.g. '30m'. Enables --blame-hang so a stuck
    # testhost produces a bounded dump instead of an endless wait. A manifest
    # batch may specify a more appropriate watchdog for its bounded workload.
    [string]$BlameHangTimeout = '',

    # Full dumps are opt-in when diagnosing async waits that mini dumps cannot describe.
    [ValidateSet('mini', 'full')]
    [string]$BlameHangDumpType = 'mini'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# --- suite manifest / batch plan ----------------------------------------------
$suiteManifestData = $null
$suiteManifestPath = if ([IO.Path]::IsPathRooted($SuiteManifest)) { $SuiteManifest } else { Join-Path $root $SuiteManifest }
if (Test-Path -LiteralPath $suiteManifestPath) {
    $suiteManifestData = Get-Content -LiteralPath $suiteManifestPath -Raw | ConvertFrom-Json
}

if ($Suite -ne 'All' -and -not $suiteManifestData) {
    throw "Suite manifest not found: $suiteManifestPath — required for -Suite $Suite."
}

$suiteDef = $null
$batches = @()
if ($Suite -eq 'All') {
    $batches += [ordered]@{ name = 'all'; filter = ''; trx = 'results.trx' }
} else {
    $suiteDef = $suiteManifestData.suites.$Suite
    if (-not $suiteDef) {
        throw "Suite '$Suite' is not defined in $suiteManifestPath."
    }

    if ($suiteDef.blame_hang_timeout -and -not $BlameHangTimeout) {
        $BlameHangTimeout = [string]$suiteDef.blame_hang_timeout
    }

    if ($suiteDef.mode -eq 'batches') {
        foreach ($batchName in @($suiteDef.batches)) {
            $definition = @($suiteManifestData.batches | Where-Object { $_.name -eq $batchName })
            if (-not $definition) {
                throw "Batch '$batchName' referenced by suite '$Suite' is not defined in $suiteManifestPath."
            }

            $batches += [ordered]@{
                name   = [string]$batchName
                filter = [string]$definition[0].filter
                trx    = "results-$batchName.trx"
                blame_hang_timeout = [string]$definition[0].blame_hang_timeout
            }
        }
    } else {
        $batches += [ordered]@{
            name   = $Suite.ToLowerInvariant()
            filter = [string]$suiteDef.filter
            trx    = 'results.trx'
        }
    }
}

if ($batches.Count -eq 0) {
    throw "Suite '$Suite' resolved to no test batches."
}

# --- resolve project ---------------------------------------------------------
$projectPath = if ([IO.Path]::IsPathRooted($Project)) { $Project } else { Join-Path $root $Project }
if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "Test project not found: $projectPath"
}
$projectPath = (Resolve-Path -LiteralPath $projectPath).Path
$projectDir = Split-Path -Parent $projectPath
$projectName = [IO.Path]::GetFileNameWithoutExtension($projectPath)

$csproj = [xml](Get-Content -LiteralPath $projectPath -Raw)
$assemblyName = ($csproj.Project.PropertyGroup.AssemblyName | Where-Object { $_ } | Select-Object -First 1)
if (-not $assemblyName) { $assemblyName = $projectName }
$targetFramework = ($csproj.Project.PropertyGroup.TargetFramework | Where-Object { $_ } | Select-Object -First 1)
if (-not $targetFramework) {
    $rootProps = [xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw)
    $targetFramework = ($rootProps.Project.PropertyGroup.TargetFramework | Where-Object { $_ } | Select-Object -First 1)
}
if (-not $targetFramework) { $targetFramework = 'net10.0' }

# --- run identity / output directory -----------------------------------------
if (-not $RunId) {
    $RunId = '{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), ([guid]::NewGuid().ToString('N').Substring(0, 6))
}
$resolvedResultsRoot = [IO.Path]::GetFullPath((Join-Path $root $ResultsRoot))
$tmpRoot = [IO.Path]::GetFullPath((Join-Path $root '.tmp')) + [IO.Path]::DirectorySeparatorChar
if (-not $resolvedResultsRoot.StartsWith($tmpRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Test run outputs must live inside the repository .tmp directory.'
}
$runDir = Join-Path $resolvedResultsRoot $RunId
New-Item -ItemType Directory -Path $runDir -Force | Out-Null
$trxPath = Join-Path $runDir 'results.trx'
$consoleLog = Join-Path $runDir 'console.log'
$manifestPath = Join-Path $runDir 'run.json'
$summaryPath = Join-Path $runDir 'summary.json'

if ($ExpectedCasesFile) {
    $ExpectedCasesFile = if ([IO.Path]::IsPathRooted($ExpectedCasesFile)) { $ExpectedCasesFile } else { Join-Path $root $ExpectedCasesFile }
    if (-not (Test-Path -LiteralPath $ExpectedCasesFile)) {
        throw "Expected cases file not found: $ExpectedCasesFile"
    }
}

# --- evidence snapshot --------------------------------------------------------
function Get-TestProcessSnapshot {
    @(Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^(testhost|vstest|datacollector)' } |
        ForEach-Object { $_.Id })
}

# git writes benign notices (e.g. CRLF warnings) to stderr; with
# $ErrorActionPreference='Stop' a native stderr write becomes a terminating
# NativeCommandError even when redirected to $null. A function-local 'Continue'
# keeps those notices out of the evidence values without hiding real failures.
function Invoke-Git {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$GitArgs)
    $ErrorActionPreference = 'Continue'
    & git -C $root @GitArgs 2>$null
}

function Get-FileSha256 {
    param([string]$Path)
    # Get-FileHash is not available in every PS 5.1 image; use .NET directly.
    $sha = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($Path)
    try {
        return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
    } finally {
        $stream.Dispose()
    }
}

function Get-WorkspaceFingerprint {
    $status = (Invoke-Git status --porcelain) -join "`n"
    $diff = (Invoke-Git diff HEAD --binary) -join "`n"
    $bytes = [Text.Encoding]::UTF8.GetBytes("$status`n$diff")
    $sha = [Security.Cryptography.SHA256]::Create()
    return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
}

$gitHead = (Invoke-Git rev-parse HEAD)
$dirtyCount = @(Invoke-Git status --porcelain).Count
$sdkVersion = (& dotnet --version 2>$null)
$availableRamBytes = $null
try {
    $availableRamBytes = [int64](Get-CimInstance Win32_OperatingSystem -ErrorAction Stop).FreePhysicalMemory * 1KB
} catch { }

$manifest = [ordered]@{
    run_id            = $RunId
    project           = $Project
    project_path      = $projectPath
    assembly          = $assemblyName
    target_framework  = $targetFramework
    suite             = $Suite
    suite_manifest    = $suiteManifestPath
    batches           = @($batches | ForEach-Object { [ordered]@{ name = $_.name; filter = $_.filter } })
    configuration     = $Configuration
    rid               = $Rid
    filter            = $Filter
    expected_cases    = $ExpectedCasesFile
    blame_hang        = $BlameHangTimeout
    blame_hang_dump_type = $BlameHangDumpType
    git               = [ordered]@{
        head            = $gitHead
        dirty_files     = $dirtyCount
        workspace_fingerprint = (Get-WorkspaceFingerprint)
    }
    environment       = [ordered]@{
        machine              = $env:COMPUTERNAME
        os                   = [Runtime.InteropServices.RuntimeInformation]::OSDescription
        processor_count      = [Environment]::ProcessorCount
        dotnet_sdk           = $sdkVersion
        available_ram_bytes  = $availableRamBytes
        powershell           = $PSVersionTable.PSVersion.ToString()
    }
    run_dir           = $runDir
    trx_path          = $trxPath
    phases            = [ordered]@{}
    test_processes    = [ordered]@{
        before = @(Get-TestProcessSnapshot)
        after  = @()
    }
    exit_code         = $null
    status            = 'running'
    status_reason     = $null
    started_at        = (Get-Date).ToUniversalTime().ToString('o')
    finished_at       = $null
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8

function Save-Manifest {
    $manifest.finished_at = (Get-Date).ToUniversalTime().ToString('o')
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8
}

function Invoke-Phase {
    param([string]$Name, [scriptblock]$Body)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $code = $null
    try {
        # Native tools may write progress/warnings to stderr; keep those as
        # console output here and judge the phase by its exit code instead.
        $ErrorActionPreference = 'Continue'
        # Out-Host keeps phase output streaming live to the console without
        # polluting this function's return value (the tee inside $Body still
        # captures it to console.log).
        # Dot-source the phase so the native LASTEXITCODE is read in the same scope.
        # A child script-block scope otherwise leaves this function's value stale/null.
        . $Body | Out-Host
        $code = $LASTEXITCODE
    } finally {
        $sw.Stop()
        $manifest.phases[$Name] = [ordered]@{
            seconds   = [math]::Round($sw.Elapsed.TotalSeconds, 3)
            exit_code = $code
        }
        Save-Manifest
    }
    return $code
}

# Multiset diff against the expected case list, mirroring
# summarize-test-run.py's compare_multiset: entries containing '::' match
# full 'project::name' keys, entries without it match the tail after the
# first '::'. Each actual case is claimed at most once.
function Compare-CaseMultiset {
    param([string[]]$Expected, [string[]]$Actual)
    $expFull = [System.Collections.Generic.Dictionary[string, int]]::new([StringComparer]::Ordinal)
    $expTail = [System.Collections.Generic.Dictionary[string, int]]::new([StringComparer]::Ordinal)
    foreach ($entry in $Expected) {
        if ($entry.Contains('::')) { $expFull[$entry] = ($expFull[$entry] + 1) } else { $expTail[$entry] = ($expTail[$entry] + 1) }
    }

    $actualCounts = [System.Collections.Generic.Dictionary[string, int]]::new([StringComparer]::Ordinal)
    foreach ($key in $Actual) { $actualCounts[$key] = ($actualCounts[$key] + 1) }

    $consumed = [System.Collections.Generic.Dictionary[string, int]]::new([StringComparer]::Ordinal)
    $missing = [System.Collections.Generic.List[string]]::new()
    foreach ($key in @($expFull.Keys)) {
        $claimed = [Math]::Min($expFull[$key], ($actualCounts[$key] + 0))
        $consumed[$key] = $claimed
        for ($i = $claimed; $i -lt $expFull[$key]; $i++) { $missing.Add($key) }
    }

    foreach ($key in @($expTail.Keys)) {
        $remaining = $expTail[$key]
        foreach ($actualKey in @($actualCounts.Keys)) {
            $idx = $actualKey.IndexOf('::')
            $tail = if ($idx -ge 0) { $actualKey.Substring($idx + 2) } else { $actualKey }
            if (-not [StringComparer]::Ordinal.Equals($tail, $key)) { continue }
            $claim = [Math]::Min($remaining, $actualCounts[$actualKey] - ($consumed[$actualKey] + 0))
            $consumed[$actualKey] = ($consumed[$actualKey] + 0) + $claim
            $remaining -= $claim
            if ($remaining -le 0) { break }
        }

        for ($i = 0; $i -lt $remaining; $i++) { $missing.Add($key) }
    }

    $extra = [System.Collections.Generic.List[string]]::new()
    foreach ($key in @($actualCounts.Keys)) {
        $left = $actualCounts[$key] - ($consumed[$key] + 0)
        for ($i = 0; $i -lt $left; $i++) { $extra.Add($key) }
    }

    return @{ missing = $missing; extra = $extra }
}

function Get-RequiredCases {
    param($Manifest, [string]$SuiteName)
    if (-not $Manifest -or -not $Manifest.suite_required_cases) { return @() }
    $entry = $Manifest.suite_required_cases.$SuiteName
    if (-not $entry) { return @() }
    if ($entry -is [string]) { return @($Manifest.required_integration_cases) }
    return @($entry)
}

function Test-BuiltOutputs {
    $candidateDirs = @(
        (Join-Path $projectDir (Join-Path 'bin' (Join-Path $Configuration (Join-Path $targetFramework $Rid)))),
        (Join-Path $projectDir (Join-Path 'bin' (Join-Path $Configuration $targetFramework)))
    ) | Where-Object { $_ } | Select-Object -Unique
    foreach ($dir in $candidateDirs) {
        $dll = Join-Path $dir "$assemblyName.dll"
        $runtimeConfig = Join-Path $dir "$assemblyName.runtimeconfig.json"
        if ((Test-Path -LiteralPath $dll) -and (Test-Path -LiteralPath $runtimeConfig)) {
            return $dir
        }
    }
    return $null
}

$interrupted = $false
$runDirBatches = @()
try {
    Write-Host "[run-tests] run $RunId -> $runDir"
    Write-Host "[run-tests] project=$projectName config=$Configuration rid=$(if ($Rid) { $Rid } else { '<none>' }) suite=$Suite batches=$($batches.Count) filter=$(if ($Filter) { $Filter } else { '<none>' })"

    if (-not $SkipRestore) {
        $restoreArgs = @('restore', $projectPath)
        if ($Rid) { $restoreArgs += @('-r', $Rid) }
        $code = Invoke-Phase 'restore' { & dotnet @restoreArgs 2>&1 | Tee-Object -FilePath $consoleLog -Append }
        if ($code -ne 0) { throw "dotnet restore failed with exit code $code" }
    }

    if ($NoBuild) {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $builtDir = Test-BuiltOutputs
        $sw.Stop()
        $manifest.phases['precheck'] = [ordered]@{
            seconds   = [math]::Round($sw.Elapsed.TotalSeconds, 3)
            exit_code = $(if ($builtDir) { 0 } else { 1 })
            built_dir = $builtDir
        }
        if (-not $builtDir) {
            throw "NoBuild requested but $assemblyName.dll/.runtimeconfig.json not found under bin/$Configuration/$targetFramework — refusing 'no-build' run against stale or missing binaries."
        }
        $manifest.binary_sha256 = Get-FileSha256 (Join-Path $builtDir "$assemblyName.dll")
    } else {
        $buildArgs = @('build', $projectPath, '-c', $Configuration, '--no-restore')
        if ($Rid) { $buildArgs += @('-r', $Rid) }
        $code = Invoke-Phase 'build' { & dotnet @buildArgs 2>&1 | Tee-Object -FilePath $consoleLog -Append }
        if ($code -ne 0) { throw "dotnet build failed with exit code $code" }
        $builtDir = Test-BuiltOutputs
        if ($builtDir) {
            $manifest.binary_sha256 = Get-FileSha256 (Join-Path $builtDir "$assemblyName.dll")
        }
    }

    # Batches run sequentially in manifest order as separate dotnet test
    # invocations; two batches of the same run never overlap on this machine.
    foreach ($batch in $batches) {
        $batchFilter = [string]$batch.filter
        if ($Filter) {
            $batchFilter = if ($batchFilter) { "($batchFilter)&($Filter)" } else { $Filter }
        }

        $phaseName = if ($batches.Count -eq 1) { 'test' } else { "test-$($batch.name)" }
        $testArgs = @('test', $projectPath, '-c', $Configuration)
        if ($Rid) { $testArgs += @('-r', $Rid) }
        if ($NoBuild) { $testArgs += '--no-build' }
        $testArgs += '--no-restore'
        if ($batchFilter) { $testArgs += @('--filter', $batchFilter) }
        $testArgs += @('--logger', "trx;LogFileName=$($batch.trx)", '--results-directory', $runDir)
        $batchHangTimeout = if ($batch.blame_hang_timeout) { [string]$batch.blame_hang_timeout } else { $BlameHangTimeout }
        if ($BlameHangTimeout) {
            $testArgs += @('--blame-hang', '--blame-hang-timeout', $batchHangTimeout,
                '--blame-hang-dump-type', $BlameHangDumpType)
        }
        Write-Host "[run-tests] batch '$($batch.name)' filter=$(if ($batchFilter) { $batchFilter } else { '<none>' })"
        Write-Host "[run-tests] dotnet $($testArgs -join ' ')"

        $batchExit = Invoke-Phase $phaseName {
            & dotnet @testArgs 2>&1 | Tee-Object -FilePath $consoleLog -Append
        }
        $runDirBatches += [ordered]@{
            name      = $batch.name
            filter    = $batchFilter
            trx       = (Join-Path $runDir $batch.trx)
            exit_code = $batchExit
            blame_hang_timeout = $(if ($BlameHangTimeout) { $batchHangTimeout } else { $null })
            blame_hang_dump_type = $(if ($batchHangTimeout) { $BlameHangDumpType } else { $null })
        }
        if ($batchExit -ne 0 -and $null -eq $manifest.exit_code) {
            $manifest.exit_code = $batchExit
        }
    }
    $manifest.batches_run = $runDirBatches
    if ($null -eq $manifest.exit_code) { $manifest.exit_code = 0 }
} catch {
    $isInterrupt = $_.Exception -is [System.Management.Automation.PipelineStoppedException] -or
        $_.Exception -is [System.Management.Automation.HaltCommandException] -or
        $_.Exception -is [System.OperationCanceledException]
    $interrupted = $true
    $manifest.status = if ($isInterrupt) { 'incomplete' } else { 'failed' }
    $manifest.status_reason = $_.Exception.Message
    Write-Warning "[run-tests] $($manifest.status): $($_.Exception.Message)"
} finally {
    $manifest.test_processes.after = @(Get-TestProcessSnapshot)
    Save-Manifest
}

# --- post-run validation: never trust exit code 0 alone ----------------------
# Every batch TRX is parsed; the union of batch cases is what multiset,
# required-case and skip checks run against, so a half-empty bucket can never
# silently shrink coverage.
$validationFailed = @()
if (-not $interrupted) {
    $allCases = [System.Collections.Generic.List[object]]::new()
    $totals = [ordered]@{ total = 0; passed = 0; failed = 0; skipped = 0; outcome = 'Completed'; incomplete_signals = @() }

    $python = Get-Command python -ErrorAction SilentlyContinue
    $pythonArgs = @()
    if (-not $python) {
        $python = Get-Command py -ErrorAction SilentlyContinue
        if ($python) { $pythonArgs += '-3' }
    }
    if (-not $python) { $python = Get-Command python3 -ErrorAction SilentlyContinue }
    if (-not $python) {
        $validationFailed += 'python not found — cannot validate TRX contents'
    }

    foreach ($batch in $runDirBatches) {
        $batchTrx = [string]$batch.trx
        if (-not (Test-Path -LiteralPath $batchTrx)) {
            $validationFailed += "batch '$($batch.name)': TRX results file missing"
            continue
        }
        if ((Get-Item -LiteralPath $batchTrx).Length -eq 0) {
            $validationFailed += "batch '$($batch.name)': TRX results file is empty"
            continue
        }
        if (-not $python) { continue }

        $batchSummary = Join-Path $runDir (
            $(if ($runDirBatches.Count -eq 1) { 'summary.json' } else { "summary-$($batch.name).json" }))
        $summarizeArgs = $pythonArgs + @(
            (Join-Path $PSScriptRoot 'summarize-test-run.py'),
            '--trx', $batchTrx,
            '--project', $projectName,
            '--out', $batchSummary,
            '--log', $consoleLog
        )
        $summarizeJson = (& $python.Source @summarizeArgs | Out-String).Trim()
        $summarizeExit = $LASTEXITCODE
        $manifest.phases["summarize-$($batch.name)"] = [ordered]@{ exit_code = $summarizeExit }
        try { $summary = $summarizeJson | ConvertFrom-Json } catch { $summary = $null }
        if (-not $summary) {
            $validationFailed += "batch '$($batch.name)': summarize-test-run.py produced no summary (exit $summarizeExit)"
            continue
        }
        if ($summary.results -eq 0) { $validationFailed += "batch '$($batch.name)': TRX contains zero test results" }

        $batch['results'] = $summary.results
        $batch['passed'] = $summary.passed
        $batch['failed'] = $summary.failed
        $batch['skipped'] = $summary.skipped
        $batch['summary_path'] = $batchSummary
        $totals.total += $summary.results
        $totals.passed += $summary.passed
        $totals.failed += $summary.failed
        $totals.skipped += $summary.skipped
        if ($summary.outcome -eq 'Failed') { $totals.outcome = 'Failed' }
        foreach ($signal in @($summary.incomplete_signals)) {
            $totals.incomplete_signals += "$($batch.name): $signal"
        }

        try {
            $detail = Get-Content -LiteralPath $batchSummary -Raw | ConvertFrom-Json
            foreach ($case in @($detail.cases)) { $allCases.Add($case) }
        } catch {
            $validationFailed += "batch '$($batch.name)': could not read $batchSummary for the union case set"
        }
    }
    $manifest.batches_run = $runDirBatches

    # A name may legitimately occur more than once within one batch (Theory cases),
    # but the same case key must never be owned by two different resource batches.
    $caseOwners = [System.Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($batch in $runDirBatches) {
        if (-not $batch.summary_path -or -not (Test-Path -LiteralPath $batch.summary_path)) { continue }
        $detail = Get-Content -LiteralPath $batch.summary_path -Raw | ConvertFrom-Json
        foreach ($case in @($detail.cases)) {
            $key = [string]$case.key
            if ($caseOwners.ContainsKey($key) -and $caseOwners[$key] -ne $batch.name) {
                $validationFailed += "case appears in more than one resource batch: $key"
            } else {
                $caseOwners[$key] = $batch.name
            }
        }
    }

    $actualKeys = @($allCases | ForEach-Object { [string]$_.key })
    $actualKeys | Set-Content -LiteralPath (Join-Path $runDir 'cases.txt') -Encoding utf8

    if ($ExpectedCasesFile) {
        $expected = @(Get-Content -LiteralPath $ExpectedCasesFile |
            ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') })
        $diff = Compare-CaseMultiset -Expected $expected -Actual $actualKeys
        if ($diff.missing.Count -gt 0 -or $diff.extra.Count -gt 0) {
            $validationFailed += "case multiset mismatch: missing=$($diff.missing.Count) extra=$($diff.extra.Count) (see cases.txt/case-diff.json)"
            [ordered]@{ missing = @($diff.missing); extra = @($diff.extra) } |
                ConvertTo-Json -Depth 4 |
                Set-Content -LiteralPath (Join-Path $runDir 'case-diff.json') -Encoding utf8
        }
    }

    if ($suiteDef) {
        foreach ($requiredCase in @(Get-RequiredCases -Manifest $suiteManifestData -SuiteName $Suite)) {
            $hit = $false
            foreach ($caseKey in $actualKeys) {
                $idx = $caseKey.IndexOf('::')
                $tail = if ($idx -ge 0) { $caseKey.Substring($idx + 2) } else { $caseKey }
                if ($tail.StartsWith([string]$requiredCase, [StringComparison]::Ordinal)) { $hit = $true; break }
            }
            if (-not $hit) { $validationFailed += "required integration case missing from the union: $requiredCase" }
        }

        $skipAllow = @()
        if ($suiteManifestData.skip_allowlist -and $suiteManifestData.skip_allowlist.cases) {
            $skipAllow = @($suiteManifestData.skip_allowlist.cases | ForEach-Object { [string]$_ })
        }
        $unexpectedSkips = [System.Collections.Generic.List[string]]::new()
        foreach ($case in $allCases) {
            if ($case.outcome -ne 'NotExecuted') { continue }
            $key = [string]$case.key
            $idx = $key.IndexOf('::')
            $tail = if ($idx -ge 0) { $key.Substring($idx + 2) } else { $key }
            $allowed = $false
            foreach ($entry in $skipAllow) {
                if ($tail.StartsWith($entry, [StringComparison]::Ordinal)) { $allowed = $true; break }
            }
            if (-not $allowed) { $unexpectedSkips.Add($key) }
        }
        if ($unexpectedSkips.Count -gt 0) {
            $validationFailed += "skipped cases outside the manifest allowlist: $($unexpectedSkips -join '; ')"
        }
    }

    $manifest.results = $totals

    if ($validationFailed.Count -gt 0) {
        $manifest.status = 'failed'
        $manifest.status_reason = ($validationFailed -join '; ')
        Write-Warning "[run-tests] validation failed despite runner exit: $($manifest.status_reason)"
    } elseif ($manifest.results -and @($manifest.results.incomplete_signals).Count -gt 0) {
        # Runner finished but the TRX itself reports truncation/cancellation.
        $manifest.status = 'incomplete'
        $manifest.status_reason = ($manifest.results.incomplete_signals -join '; ')
    } elseif ($manifest.exit_code -ne 0) {
        $manifest.status = 'failed'
        $manifest.status_reason = "dotnet test exited $($manifest.exit_code)"
    } elseif ($totals.failed -gt 0 -or $totals.outcome -eq 'Failed') {
        $manifest.status = 'failed'
        $manifest.status_reason = 'test results contain failures despite a successful runner exit'
    } else {
        $manifest.status = 'passed'
    }
    Save-Manifest
}

Write-Host "[run-tests] status=$($manifest.status) exit=$($manifest.exit_code) run_dir=$runDir"
if ($manifest.status -eq 'passed') { exit 0 }
exit 1
