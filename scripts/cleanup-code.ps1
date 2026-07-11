[CmdletBinding()]
param(
    [string[]]$Include = @('src/**/*.cs', 'tests/**/*.cs', 'src/**/*.axaml', 'tests/**/*.axaml')
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
& jb cleanupcode (Join-Path $root 'Patchouli.sln') `
    --profile=$profile `
    --include=$includeValue `
    --toolset-path=$msbuildPath `
    --no-updates

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
