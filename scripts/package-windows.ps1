param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [string]$Version = "0.3.2"
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

if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
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

$migrationsDir = Join-Path $publishDir "migrations"
if (-not (Test-Path -LiteralPath $migrationsDir)) {
    throw "Published migrations directory was not found at '$migrationsDir'."
}
$legacyMigrationMarkers = @(
    "005_create_pages_and_layout.sql",
    "014_add_table_cell_metadata.sql",
    "024_add_layout_revision_source_basis.sql"
)
$legacyPresent = Get-ChildItem -LiteralPath $migrationsDir -File |
    Where-Object { $legacyMigrationMarkers -contains $_.Name -or $_.Name -match 'layout' } |
    Select-Object -ExpandProperty Name
if ($legacyPresent) {
    throw "Published migrations still contain legacy layout schema files: $($legacyPresent -join ', ')"
}

$cliDir = Join-Path $publishDir "cli"
dotnet publish (Join-Path $root "src\Patchouli.Cli\Patchouli.Cli.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:Version=$Version `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $cliDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (CLI) failed with exit code $LASTEXITCODE." }
$cliExe = Join-Path $cliDir "patchouli-cli.exe"
if (-not (Test-Path -LiteralPath $cliExe)) {
    throw "CLI was not published to '$cliExe'."
}

$helperName = "biblatex-helper.exe"
$helperSource = Join-Path $root "tools\biblatex-helper\target\release\$helperName"
if (-not (Test-Path -LiteralPath $helperSource)) {
    Write-Host "Building biblatex-helper..."
    cargo build --release --manifest-path (Join-Path $root "tools\biblatex-helper\Cargo.toml")
    if ($LASTEXITCODE -ne 0) { throw "cargo build failed with exit code $LASTEXITCODE." }
}
if (-not (Test-Path -LiteralPath $helperSource)) {
    throw "biblatex-helper was not found at '$helperSource'."
}
Copy-Item -LiteralPath $helperSource -Destination (Join-Path $publishDir $helperName) -Force

$iss = Join-Path $root "packaging\windows\Patchouli.Net.iss"
& $iscc "/DSourceDir=$publishDir" "/DOutputDir=$installerDir" "/DAppVersion=$Version" $iss
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE." }

$installer = Join-Path $installerDir "Patchouli.Net-$Version-$Runtime-setup.exe"
if (-not (Test-Path -LiteralPath $installer)) { throw "Installer was not created at '$installer'." }
$installer
