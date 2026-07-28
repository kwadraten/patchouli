param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [string]$Version = "0.2.3"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root "artifacts\publish\$Runtime"
$installerDir = Join-Path $root "artifacts\installer"
$iscc = @(
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

if (-not $iscc) {
    throw "Inno Setup 6 was not found. Install it or add ISCC.exe to a standard installation directory."
}

New-Item -ItemType Directory -Force -Path $publishDir, $installerDir | Out-Null
dotnet publish (Join-Path $root "src\Patchouli.UI\Patchouli.UI.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:Version=$Version `
    -p:PublishSingleFile=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

$iss = Join-Path $root "packaging\windows\Patchouli.Net.iss"
& $iscc "/DSourceDir=$publishDir" "/DOutputDir=$installerDir" "/DAppVersion=$Version" $iss
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE." }

$installer = Join-Path $installerDir "Patchouli.Net-$Version-$Runtime-setup.exe"
if (-not (Test-Path -LiteralPath $installer)) { throw "Installer was not created at '$installer'." }
$installer
