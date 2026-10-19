#Requires -Version 7.0
<#
.SYNOPSIS
    Regenerate src/Patchouli.Core/Agents/AgentCatalog.g.cs from the ACP registry and the curated TUI table.

.DESCRIPTION
    The application never reads the online Agent Client Protocol registry at run time; the catalog is
    compiled in. This script is the only way to produce it:

      1. Download https://cdn.agentclientprotocol.com/registry/v1/latest/registry.json (or reuse the
         cached copy in .tmp/acp-registry/, which is ignored by Git). -Refresh forces a download. When
         the download fails but a cache exists, the cached copy is used with a warning.
      2. Combine the registry with the curated TUI adapter table below: native command name, launch
         arguments (interactive TUI only, never ACP server arguments), per-OS absolute candidate paths,
         version-probe arguments, minimum version and the default marker.
      3. Emit src/Patchouli.Core/Agents/AgentCatalog.g.cs in the same shape as the original hand-written
         file (C# 12 collection expressions). Registry distribution data (binary download templates +
         sha256, npx/uvx packages) is written into per-entry comments for the future installer; no
         runtime download logic is generated.

    Output is deterministic: there are no timestamps or other volatile fields, so two consecutive runs
    produce byte-identical files. Curated ids missing from the registry are an error; registry entries
    without a curated TUI adapter are skipped (they cannot be launched as a TUI).

    Icons are deliberately not downloaded or embedded in this iteration, so every entry leaves
    IconResourceName at its null default. Future extension point: download the icon URL that is printed
    in each entry's generated comment into an embedded resource, add a resource-name field to the
    curated table, and emit IconResourceName in New-CatalogEntryLines.

.PARAMETER Refresh
    Force a fresh registry download even if .tmp/acp-registry/registry.json already exists.

.PARAMETER Check
    Do not modify the catalog. Generate into .tmp/agent-catalog/AgentCatalog.g.cs.check, print a line
    diff against the target file, and exit 1 when it differs. Intended for CI. The comparison uses the
    cached registry snapshot (downloaded when missing), so an upstream registry update intentionally
    fails the check until the catalog is regenerated.

.PARAMETER OutputPath
    Write the generated catalog to this path instead of src/Patchouli.Core/Agents/AgentCatalog.g.cs.
    Relative paths resolve against the repository root. With -Check, this is the file to compare.

.EXAMPLE
    pwsh scripts/update-agent-catalog.ps1

.EXAMPLE
    pwsh scripts/update-agent-catalog.ps1 -Refresh

.EXAMPLE
    pwsh scripts/update-agent-catalog.ps1 -Check
#>

[CmdletBinding()]
param(
    [switch]$Refresh,
    [switch]$Check,
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$scriptName = Split-Path -Leaf $PSCommandPath
$root = Split-Path -Parent $PSScriptRoot
$registryUrl = 'https://cdn.agentclientprotocol.com/registry/v1/latest/registry.json'
$cacheDir = [System.IO.Path]::Combine($root, '.tmp', 'acp-registry')
$cachePath = [System.IO.Path]::Combine($cacheDir, 'registry.json')
$defaultOutputPath = [System.IO.Path]::Combine($root, 'src', 'Patchouli.Core', 'Agents', 'AgentCatalog.g.cs')
$checkPath = [System.IO.Path]::Combine($root, '.tmp', 'agent-catalog', 'AgentCatalog.g.cs.check')
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
# The checked-in file and .editorconfig use LF, so the script always writes LF regardless of host OS.
$newLine = "`n"

# ---------------------------------------------------------------------------------------------------
# Curated TUI adapter table
# ---------------------------------------------------------------------------------------------------
# One entry per agent that can run as an interactive TUI in the embedded terminal. Launch data here was
# verified against the vendor install scripts (see .tmp/agent-install-research/FINDINGS.md); registry
# "acp" server arguments are intentionally dropped for terminal use.
#
# Fields mirror AgentCatalogEntry:
#   Id               Registry id (must exist in the registry).
#   DisplayName      User-facing name; empty inherits the registry name.
#   Description      Short settings description; empty inherits the registry description.
#   Kind             NativeTui or AcpOnly.
#   CommandName      Executable probed on PATH.
#   LaunchArguments  Arguments that start the interactive TUI; ACP flags are rejected.
#   WindowsPaths     Absolute candidates after %VAR% expansion.
#   MacPaths         Absolute candidates after ~ expansion.
#   LinuxPaths       Absolute candidates after ~ expansion.
#   VersionArguments Arguments that print the version.
#   MinimumVersion   Optional minimum supported version; null when not enforced.
#   IsDefault        True for the catalog default (OpenCode).
#
# The array order is the generated display order. To add an agent: append an entry, run this script,
# and commit the regenerated AgentCatalog.g.cs.
$curated = @(
    [pscustomobject][ordered]@{
        Id = 'opencode'
        DisplayName = 'OpenCode'
        Description = 'The open source coding agent.'
        Kind = 'NativeTui'
        CommandName = 'opencode'
        LaunchArguments = @()
        WindowsPaths = @(
            '%USERPROFILE%\.opencode\bin\opencode.exe'
            '%APPDATA%\npm\opencode.cmd'
        )
        MacPaths = @('~/.opencode/bin/opencode', '/opt/homebrew/bin/opencode', '/usr/local/bin/opencode')
        LinuxPaths = @('~/.opencode/bin/opencode', '/usr/local/bin/opencode')
        VersionArguments = @('--version')
        MinimumVersion = $null
        IsDefault = $true
    },
    [pscustomobject][ordered]@{
        Id = 'claude-acp'
        DisplayName = 'Claude Code'
        Description = 'Anthropic''s Claude coding agent.'
        Kind = 'NativeTui'
        CommandName = 'claude'
        LaunchArguments = @()
        WindowsPaths = @(
            '%USERPROFILE%\.local\bin\claude.exe'
            '%APPDATA%\npm\claude.cmd'
        )
        MacPaths = @('~/.local/bin/claude', '/opt/homebrew/bin/claude', '/usr/local/bin/claude')
        LinuxPaths = @('~/.local/bin/claude', '/usr/local/bin/claude')
        VersionArguments = @('--version')
        MinimumVersion = $null
        IsDefault = $false
    },
    [pscustomobject][ordered]@{
        Id = 'codex-acp'
        DisplayName = 'Codex'
        Description = 'OpenAI''s coding agent.'
        Kind = 'NativeTui'
        CommandName = 'codex'
        LaunchArguments = @()
        WindowsPaths = @(
            '%LOCALAPPDATA%\Programs\OpenAI\Codex\bin\codex.exe'
            '%APPDATA%\npm\codex.cmd'
        )
        MacPaths = @('~/.local/bin/codex', '/opt/homebrew/bin/codex')
        LinuxPaths = @('~/.local/bin/codex', '/usr/local/bin/codex')
        VersionArguments = @('--version')
        MinimumVersion = $null
        IsDefault = $false
    },
    [pscustomobject][ordered]@{
        Id = 'kimi'
        DisplayName = 'Kimi Code'
        Description = 'Moonshot AI''s coding agent.'
        Kind = 'NativeTui'
        CommandName = 'kimi'
        LaunchArguments = @()
        WindowsPaths = @(
            '%USERPROFILE%\.kimi-code\bin\kimi.exe'
            '%USERPROFILE%\.local\bin\kimi.exe'
        )
        MacPaths = @('~/.kimi-code/bin/kimi', '~/.local/bin/kimi', '/opt/homebrew/bin/kimi')
        LinuxPaths = @('~/.kimi-code/bin/kimi', '~/.local/bin/kimi', '/usr/local/bin/kimi')
        VersionArguments = @('--version')
        MinimumVersion = $null
        IsDefault = $false
    },
    [pscustomobject][ordered]@{
        Id = 'antigravity-acp'
        DisplayName = 'Google Antigravity'
        Description = 'Google''s Antigravity agent CLI.'
        Kind = 'NativeTui'
        CommandName = 'agy'
        LaunchArguments = @()
        WindowsPaths = @('%LOCALAPPDATA%\agy\bin\agy.exe')
        MacPaths = @('~/.local/bin/agy', '/Applications/Antigravity.app/Contents/Resources/bin/agy')
        LinuxPaths = @('~/.local/bin/agy')
        VersionArguments = @('--version')
        MinimumVersion = $null
        IsDefault = $false
    },
    [pscustomobject][ordered]@{
        Id = 'gemini'
        DisplayName = 'Gemini CLI'
        Description = 'Google''s Gemini coding agent.'
        Kind = 'NativeTui'
        CommandName = 'gemini'
        LaunchArguments = @()
        WindowsPaths = @('%APPDATA%\npm\gemini.cmd')
        MacPaths = @('/opt/homebrew/bin/gemini', '/usr/local/bin/gemini', '~/.local/bin/gemini')
        LinuxPaths = @('/usr/local/bin/gemini', '~/.local/bin/gemini')
        VersionArguments = @('--version')
        MinimumVersion = $null
        IsDefault = $false
    },
    [pscustomobject][ordered]@{
        Id = 'github-copilot-cli'
        DisplayName = 'GitHub Copilot CLI'
        Description = 'GitHub''s Copilot command line agent.'
        Kind = 'NativeTui'
        CommandName = 'copilot'
        LaunchArguments = @()
        WindowsPaths = @('%APPDATA%\npm\copilot.cmd')
        MacPaths = @('/opt/homebrew/bin/copilot', '/usr/local/bin/copilot')
        LinuxPaths = @('/usr/local/bin/copilot')
        VersionArguments = @('--version')
        MinimumVersion = $null
        IsDefault = $false
    },
    [pscustomobject][ordered]@{
        Id = 'qwen-code'
        DisplayName = 'Qwen Code'
        Description = 'Alibaba''s Qwen coding assistant.'
        Kind = 'NativeTui'
        CommandName = 'qwen'
        LaunchArguments = @()
        WindowsPaths = @('%APPDATA%\npm\qwen.cmd')
        MacPaths = @('/opt/homebrew/bin/qwen', '/usr/local/bin/qwen')
        LinuxPaths = @('/usr/local/bin/qwen')
        VersionArguments = @('--version')
        MinimumVersion = $null
        IsDefault = $false
    },
    [pscustomobject][ordered]@{
        Id = 'cline'
        DisplayName = 'Cline'
        Description = 'Cline''s autonomous coding agent.'
        Kind = 'NativeTui'
        CommandName = 'cline'
        LaunchArguments = @()
        WindowsPaths = @('%APPDATA%\npm\cline.cmd')
        MacPaths = @('/opt/homebrew/bin/cline', '/usr/local/bin/cline')
        LinuxPaths = @('/usr/local/bin/cline')
        VersionArguments = @('--version')
        MinimumVersion = $null
        IsDefault = $false
    }
)

# ---------------------------------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------------------------------

function Get-JsonProperty {
    param($Object, [string]$Name)

    if ($null -eq $Object) {
        return $null
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function ConvertTo-CommentText {
    param([string]$Value)

    return $Value.Replace("`r", ' ').Replace("`n", ' ').Trim()
}

function ConvertTo-CSharpStringLiteral {
    param([string]$Value)

    if ($Value -match "[\r\n]") {
        throw "Cannot emit a C# string literal containing a line break: '$Value'."
    }

    if ($Value.Contains('\')) {
        return '@"' + $Value.Replace('"', '""') + '"'
    }

    return '"' + $Value.Replace('"', '\"') + '"'
}

function New-CSharpArrayLines {
    param([string]$Name, $Values, [int]$InlineLimit = 3)

    $items = @()
    if ($null -ne $Values) {
        $items = @($Values)
    }

    if ($items.Count -eq 0) {
        return @("${Name}: [],")
    }

    $literals = @($items | ForEach-Object { ConvertTo-CSharpStringLiteral -Value ([string]$_) })
    if ($items.Count -le $InlineLimit) {
        return @("${Name}: [" + ($literals -join ', ') + '],')
    }

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("${Name}:")
    $lines.Add('[')
    for ($i = 0; $i -lt $literals.Count; $i++) {
        $comma = if ($i -lt $literals.Count - 1) { ',' } else { '' }
        $lines.Add('    ' + $literals[$i] + $comma)
    }

    $lines.Add('],')
    return $lines.ToArray()
}

function Format-JsonArguments {
    param($Value)

    if ($null -eq $Value) {
        return ''
    }

    $items = @($Value)
    if ($items.Count -eq 0) {
        return ''
    }

    return (@($items | ForEach-Object { [string]$_ }) -join ' ')
}

function Format-JsonEnvironment {
    param($Value)

    if ($null -eq $Value) {
        return ''
    }

    $pairs = @($Value.PSObject.Properties |
            Sort-Object -Property Name |
            ForEach-Object { "$($_.Name)=$($_.Value)" })
    if ($pairs.Count -eq 0) {
        return ''
    }

    return ($pairs -join ', ')
}

function Format-PackageDistribution {
    param($Value)

    $package = [string](Get-JsonProperty -Object $Value -Name 'package')
    if ([string]::IsNullOrWhiteSpace($package)) {
        $package = '(package missing)'
    }

    $parts = [System.Collections.Generic.List[string]]::new()
    $arguments = Format-JsonArguments -Value (Get-JsonProperty -Object $Value -Name 'args')
    if ($arguments) {
        $parts.Add("args $arguments")
    }

    $environment = Format-JsonEnvironment -Value (Get-JsonProperty -Object $Value -Name 'env')
    if ($environment) {
        $parts.Add("env $environment")
    }

    if ($parts.Count -gt 0) {
        return $package + ' (' + ($parts -join '; ') + ')'
    }

    return $package
}

function New-BinaryCommentLines {
    param($Value)

    $lines = [System.Collections.Generic.List[string]]::new()
    # Sort platform keys so a reordered registry document still produces identical output.
    foreach ($platform in @($Value.PSObject.Properties | Sort-Object -Property Name)) {
        $item = $platform.Value
        $archive = [string](Get-JsonProperty -Object $item -Name 'archive')
        if ([string]::IsNullOrWhiteSpace($archive)) {
            $archive = '(archive missing)'
        }

        $lines.Add("//   binary $($platform.Name): $archive")

        $parts = [System.Collections.Generic.List[string]]::new()
        $sha256 = [string](Get-JsonProperty -Object $item -Name 'sha256')
        if ($sha256) {
            $parts.Add("sha256 $sha256")
        }

        $command = [string](Get-JsonProperty -Object $item -Name 'cmd')
        if ($command) {
            $parts.Add("cmd $command")
        }

        $arguments = Format-JsonArguments -Value (Get-JsonProperty -Object $item -Name 'args')
        if ($arguments) {
            $parts.Add("args $arguments")
        }

        $environment = Format-JsonEnvironment -Value (Get-JsonProperty -Object $item -Name 'env')
        if ($environment) {
            $parts.Add("env $environment")
        }

        if ($parts.Count -gt 0) {
            $lines.Add('//     ' + ($parts -join '; '))
        }
    }

    return $lines.ToArray()
}

function New-RegistryCommentLines {
    param($Agent)

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('// Registry distribution (ACP server form, not TUI launch args); never read at run time.')

    $version = [string](Get-JsonProperty -Object $Agent -Name 'version')
    $repository = [string](Get-JsonProperty -Object $Agent -Name 'repository')
    $summary = if ($version) { "agent version $version" } else { 'agent version unknown' }
    if ($repository) {
        $summary += ' - ' + $repository
    }

    $lines.Add('//   ' + (ConvertTo-CommentText -Value $summary))

    $icon = [string](Get-JsonProperty -Object $Agent -Name 'icon')
    if ($icon) {
        $lines.Add('//   icon: ' + (ConvertTo-CommentText -Value $icon) + ' (not embedded yet)')
    } else {
        $lines.Add('//   icon: not published')
    }

    $distribution = Get-JsonProperty -Object $Agent -Name 'distribution'
    if ($null -eq $distribution) {
        $lines.Add('//   distribution: not published')
        return $lines.ToArray()
    }

    $npx = Get-JsonProperty -Object $distribution -Name 'npx'
    if ($null -eq $npx) {
        $lines.Add('//   npx: not published')
    } else {
        $lines.Add('//   npx: ' + (Format-PackageDistribution -Value $npx))
    }

    $uvx = Get-JsonProperty -Object $distribution -Name 'uvx'
    if ($null -eq $uvx) {
        $lines.Add('//   uvx: not published')
    } else {
        $lines.Add('//   uvx: ' + (Format-PackageDistribution -Value $uvx))
    }

    $binary = Get-JsonProperty -Object $distribution -Name 'binary'
    if ($null -eq $binary) {
        $lines.Add('//   binary: not published')
    } else {
        foreach ($line in (New-BinaryCommentLines -Value $binary)) {
            $lines.Add($line)
        }
    }

    return $lines.ToArray()
}

function New-CatalogEntryLines {
    param($Entry, $RegistryAgent)

    $lines = [System.Collections.Generic.List[string]]::new()
    foreach ($comment in (New-RegistryCommentLines -Agent $RegistryAgent)) {
        $lines.Add('        ' + $comment)
    }

    $displayName = [string]$Entry.DisplayName
    if ([string]::IsNullOrWhiteSpace($displayName)) {
        $displayName = [string](Get-JsonProperty -Object $RegistryAgent -Name 'name')
    }

    if ([string]::IsNullOrWhiteSpace($displayName)) {
        $displayName = [string]$Entry.Id
    }

    $description = [string]$Entry.Description
    if ([string]::IsNullOrWhiteSpace($description)) {
        $description = [string](Get-JsonProperty -Object $RegistryAgent -Name 'description')
    }

    $lines.Add('        new(')
    $lines.Add('            Id: ' + (ConvertTo-CSharpStringLiteral -Value ([string]$Entry.Id)) + ',')
    $lines.Add('            DisplayName: ' + (ConvertTo-CSharpStringLiteral -Value $displayName) + ',')
    $lines.Add('            Description: ' + (ConvertTo-CSharpStringLiteral -Value $description) + ',')
    $lines.Add('            Kind: AgentLaunchKind.' + [string]$Entry.Kind + ',')

    $propertyLines = [System.Collections.Generic.List[string]]::new()
    $propertyLines.Add('            CommandName: ' + (ConvertTo-CSharpStringLiteral -Value ([string]$Entry.CommandName)) + ',')

    foreach ($line in (New-CSharpArrayLines -Name 'LaunchArguments' -Values $Entry.LaunchArguments)) {
        $propertyLines.Add('            ' + $line)
    }

    # Windows paths are one-per-line when there are several candidates; Unix paths stay inline, matching
    # the formatting of the original checked-in catalog.
    foreach ($line in (New-CSharpArrayLines -Name 'WindowsPaths' -Values $Entry.WindowsPaths -InlineLimit 1)) {
        $propertyLines.Add('            ' + $line)
    }

    foreach ($line in (New-CSharpArrayLines -Name 'MacPaths' -Values $Entry.MacPaths -InlineLimit 3)) {
        $propertyLines.Add('            ' + $line)
    }

    foreach ($line in (New-CSharpArrayLines -Name 'LinuxPaths' -Values $Entry.LinuxPaths -InlineLimit 3)) {
        $propertyLines.Add('            ' + $line)
    }

    foreach ($line in (New-CSharpArrayLines -Name 'VersionArguments' -Values $Entry.VersionArguments)) {
        $propertyLines.Add('            ' + $line)
    }

    if ($null -ne $Entry.MinimumVersion -and [string]$Entry.MinimumVersion -ne '') {
        $propertyLines.Add('            MinimumVersion: ' +
            (ConvertTo-CSharpStringLiteral -Value ([string]$Entry.MinimumVersion)) + ',')
    }

    # IconResourceName is omitted on purpose: icons are not embedded yet, so it keeps its null default.
    if ([bool]$Entry.IsDefault) {
        $propertyLines.Add('            IsDefault: true,')
    }

    $lastIndex = $propertyLines.Count - 1
    $propertyLines[$lastIndex] = $propertyLines[$lastIndex].TrimEnd(',')
    foreach ($line in $propertyLines) {
        $lines.Add($line)
    }

    return $lines.ToArray()
}

function New-CatalogFileLines {
    param($Entries, $RegistryById, [string]$RegistryVersion)

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('// <auto-generated>')
    $lines.Add('// Generated from the Agent Client Protocol registry')
    $lines.Add("// ($registryUrl, registry version $RegistryVersion)")
    $lines.Add('// plus the curated TUI adapter table in scripts/update-agent-catalog.ps1.')
    $lines.Add('// Regenerate with: pwsh scripts/update-agent-catalog.ps1 (add -Refresh to re-download the registry).')
    $lines.Add('// Do not edit by hand. Icons are not embedded yet, so IconResourceName keeps its null default.')
    $lines.Add('// </auto-generated>')
    $lines.Add('')
    $lines.Add('namespace Patchouli.Core.Agents;')
    $lines.Add('')
    $lines.Add('public static partial class AgentCatalog')
    $lines.Add('{')
    $lines.Add('    /// <summary>Every catalogued agent, in display order.</summary>')
    $lines.Add('    public static readonly AgentCatalogEntry[] All =')
    $lines.Add('    [')

    for ($i = 0; $i -lt $Entries.Count; $i++) {
        $entry = $Entries[$i]
        $registryAgent = $RegistryById[[string]$entry.Id]
        $entryLines = New-CatalogEntryLines -Entry $entry -RegistryAgent $registryAgent
        $lastIndex = $entryLines.Count - 1
        $closing = if ($i -lt $Entries.Count - 1) { '),' } else { ')' }
        $entryLines[$lastIndex] = $entryLines[$lastIndex] + $closing
        foreach ($line in $entryLines) {
            $lines.Add($line)
        }
    }

    $lines.Add('    ];')
    $lines.Add('}')
    $lines.Add('')
    return $lines.ToArray()
}

function New-LineDiff {
    param([string[]]$Expected, [string[]]$Actual, [int]$MaxLines = 40)

    # Minimal LCS-based edit script, so an inserted header line does not make every following line
    # look changed. The catalog is small enough (a few hundred lines) for the O(n*m) table.
    $actualCount = $Actual.Count
    $expectedCount = $Expected.Count
    $lcs = [int[,]]::new($actualCount + 1, $expectedCount + 1)
    for ($i = $actualCount - 1; $i -ge 0; $i--) {
        for ($j = $expectedCount - 1; $j -ge 0; $j--) {
            if ($Actual[$i] -ceq $Expected[$j]) {
                $lcs[($i), ($j)] = $lcs[($i + 1), ($j + 1)] + 1
            } else {
                $lcs[($i), ($j)] = [Math]::Max($lcs[($i + 1), ($j)], $lcs[($i), ($j + 1)])
            }
        }
    }

    $output = [System.Collections.Generic.List[string]]::new()
    $i = 0
    $j = 0
    while ($i -lt $actualCount -and $j -lt $expectedCount) {
        if ($Actual[$i] -ceq $Expected[$j]) {
            $i++
            $j++
        } elseif ($lcs[($i + 1), ($j)] -ge $lcs[($i), ($j + 1)]) {
            $output.Add('  - ' + $Actual[$i])
            $i++
        } else {
            $output.Add('  + ' + $Expected[$j])
            $j++
        }
    }

    while ($i -lt $actualCount) {
        $output.Add('  - ' + $Actual[$i])
        $i++
    }

    while ($j -lt $expectedCount) {
        $output.Add('  + ' + $Expected[$j])
        $j++
    }

    if ($output.Count -gt $MaxLines) {
        $remaining = $output.Count - $MaxLines
        $output = [System.Collections.Generic.List[string]]::new($output.GetRange(0, $MaxLines))
        $output.Add("  ... ($remaining more differed lines)")
    }

    return $output.ToArray()
}

function Read-RegistryDocument {
    param([string]$Path)

    $text = [System.IO.File]::ReadAllText($Path)
    try {
        $document = $text | ConvertFrom-Json
    } catch {
        throw "ACP registry at '$Path' is not valid JSON: $($_.Exception.Message)"
    }

    if ($null -eq (Get-JsonProperty -Object $document -Name 'version')) {
        throw "ACP registry at '$Path' has no 'version' field."
    }

    $agents = Get-JsonProperty -Object $document -Name 'agents'
    if ($null -eq $agents -or @($agents).Count -eq 0) {
        throw "ACP registry at '$Path' has no agents."
    }

    return $document
}

function Save-RegistryCache {
    param([switch]$Force)

    $cacheExists = Test-Path -LiteralPath $cachePath -PathType Leaf
    if ($cacheExists -and -not $Force) {
        Write-Host "Using cached ACP registry: $cachePath"
        return
    }

    New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null
    $downloadPath = "$cachePath.download"
    try {
        Write-Host "Downloading ACP registry: $registryUrl"
        Invoke-WebRequest -Uri $registryUrl -OutFile $downloadPath -TimeoutSec 60
        # Validate before replacing a known-good cache, so a truncated download cannot poison it.
        $null = Read-RegistryDocument -Path $downloadPath
        Move-Item -LiteralPath $downloadPath -Destination $cachePath -Force
    } catch {
        Remove-Item -LiteralPath $downloadPath -Force -ErrorAction SilentlyContinue
        if ($cacheExists) {
            Write-Warning ("ACP registry download failed; reusing the cached copy at '$cachePath'. " +
                $_.Exception.Message)
        } else {
            throw ("ACP registry download failed and no cached copy exists at '$cachePath'. " +
                $_.Exception.Message)
        }
    }
}

# ---------------------------------------------------------------------------------------------------
# Validate the curated table
# ---------------------------------------------------------------------------------------------------

$validKinds = @('NativeTui', 'AcpOnly')
$requiredFields = @(
    'Id', 'DisplayName', 'Description', 'Kind', 'CommandName', 'LaunchArguments',
    'WindowsPaths', 'MacPaths', 'LinuxPaths', 'VersionArguments', 'MinimumVersion', 'IsDefault'
)
$curatedIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)

foreach ($entry in $curated) {
    $entryId = if ($null -ne $entry.PSObject.Properties['Id']) { [string]$entry.Id } else { '<missing id>' }
    foreach ($field in $requiredFields) {
        if ($null -eq $entry.PSObject.Properties[$field]) {
            throw "Curated agent '$entryId' is missing the '$field' field."
        }
    }

    if ([string]$entry.Id -notmatch '^[a-z0-9][a-z0-9._-]*$') {
        throw "Curated agent id '$([string]$entry.Id)' must use lowercase letters, digits, '.', '_' or '-'."
    }

    if (-not $curatedIds.Add([string]$entry.Id)) {
        throw "Duplicate curated agent id '$([string]$entry.Id)'."
    }

    if ($validKinds -notcontains [string]$entry.Kind) {
        throw "Curated agent '$entryId' has kind '$($entry.Kind)'; expected one of $($validKinds -join ', ')."
    }

    $commandName = [string]$entry.CommandName
    if ([string]::IsNullOrWhiteSpace($commandName)) {
        if ([string]$entry.Kind -eq 'NativeTui') {
            throw "Native TUI agent '$entryId' needs a non-empty CommandName."
        }
    } elseif ($commandName -notmatch '^[A-Za-z0-9._+-]+$') {
        throw "Curated agent '$entryId' has an invalid CommandName '$commandName'."
    }

    $launchArguments = @()
    if ($null -ne $entry.LaunchArguments) {
        $launchArguments = @($entry.LaunchArguments)
    }

    foreach ($argument in $launchArguments) {
        $argumentText = [string]$argument
        if ($argumentText -match '^(?:--?)?acp(?:$|=)') {
            throw ("Curated agent '$entryId' lists the ACP server argument '$argumentText' in LaunchArguments. " +
                'LaunchArguments must start the interactive TUI and never the ACP server.')
        }

        if ($argumentText -match "[\r\n]") {
            throw "Curated agent '$entryId' has a line break in LaunchArguments."
        }
    }

    $platformPathSets = @($entry.WindowsPaths, $entry.MacPaths, $entry.LinuxPaths)
    $pathCount = 0
    foreach ($pathSet in $platformPathSets) {
        if ($null -eq $pathSet) {
            continue
        }

        foreach ($path in @($pathSet)) {
            $pathText = [string]$path
            if ([string]::IsNullOrWhiteSpace($pathText)) {
                throw "Curated agent '$entryId' has an empty path candidate."
            }

            if ($pathText -match "[\r\n]") {
                throw "Curated agent '$entryId' has a line break in its path candidates."
            }

            $pathCount++
        }
    }

    if ([string]$entry.Kind -eq 'NativeTui' -and $pathCount -eq 0) {
        throw "Native TUI agent '$entryId' needs at least one per-OS path candidate."
    }
}

$defaultEntries = @($curated | Where-Object { [bool]$_.IsDefault })
if ($defaultEntries.Count -gt 1) {
    throw "Only one curated agent may set IsDefault; found $($defaultEntries.Count)."
}

if ($defaultEntries.Count -eq 0) {
    Write-Warning 'No curated agent sets IsDefault; AgentCatalog.Default will fall back to the first native TUI entry.'
}

# ---------------------------------------------------------------------------------------------------
# Load the registry and cross-check the curated ids
# ---------------------------------------------------------------------------------------------------

Save-RegistryCache -Force:$Refresh
$registry = Read-RegistryDocument -Path $cachePath
$registryVersion = [string](Get-JsonProperty -Object $registry -Name 'version')
$registryById = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::Ordinal)

foreach ($agent in @(Get-JsonProperty -Object $registry -Name 'agents')) {
    $agentId = [string](Get-JsonProperty -Object $agent -Name 'id')
    if ([string]::IsNullOrWhiteSpace($agentId)) {
        throw "ACP registry version $registryVersion contains an agent without an id."
    }

    if ($registryById.ContainsKey($agentId)) {
        throw "ACP registry version $registryVersion contains duplicate agent id '$agentId'."
    }

    $registryById[$agentId] = $agent
}

$missingIds = @($curated |
        ForEach-Object { [string]$_.Id } |
        Where-Object { -not $registryById.ContainsKey($_) })
if ($missingIds.Count -gt 0) {
    $message = ("Curated agent ids are missing from ACP registry version {0}: {1}. " +
        'Remove or rename them in the curated table in {2}.')
    $message = $message -f $registryVersion, ($missingIds -join ', '), $scriptName
    throw $message
}

$skippedIds = @($registryById.Keys |
        Where-Object { -not $curatedIds.Contains($_) } |
        Sort-Object)

# ---------------------------------------------------------------------------------------------------
# Generate and write
# ---------------------------------------------------------------------------------------------------

$catalogLines = New-CatalogFileLines -Entries $curated -RegistryById $registryById -RegistryVersion $registryVersion
$content = $catalogLines -join $newLine

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $targetPath = $defaultOutputPath
} elseif ([System.IO.Path]::IsPathRooted($OutputPath)) {
    $targetPath = [System.IO.Path]::GetFullPath($OutputPath)
} else {
    $targetPath = [System.IO.Path]::GetFullPath($OutputPath, $root)
}

if ($Check) {
    $checkDir = Split-Path -Parent $checkPath
    New-Item -ItemType Directory -Path $checkDir -Force | Out-Null
    [System.IO.File]::WriteAllText($checkPath, $content, $utf8NoBom)

    if (-not (Test-Path -LiteralPath $targetPath -PathType Leaf)) {
        Write-Host "Agent catalog is missing: $targetPath"
        Write-Host "Generated copy: $checkPath"
        exit 1
    }

    $expectedBytes = $utf8NoBom.GetBytes($content)
    $actualBytes = [System.IO.File]::ReadAllBytes($targetPath)
    $identical = $actualBytes.Length -eq $expectedBytes.Length
    if ($identical) {
        for ($i = 0; $i -lt $expectedBytes.Length; $i++) {
            if ($actualBytes[$i] -ne $expectedBytes[$i]) {
                $identical = $false
                break
            }
        }
    }

    if ($identical) {
        Write-Host "Agent catalog is up to date: $targetPath"
        exit 0
    }

    Write-Host "Agent catalog is stale: $targetPath"
    Write-Host "Generated copy: $checkPath"
    Write-Host 'Regenerate with: pwsh scripts/update-agent-catalog.ps1'
    $actualLines = [System.IO.File]::ReadAllLines($targetPath)
    $expectedLines = @($content -split $newLine)
    if ($expectedLines.Count -gt 1 -and $expectedLines[-1] -eq '') {
        $expectedLines = @($expectedLines[0..($expectedLines.Count - 2)])
    }

    $diffLines = @(New-LineDiff -Expected $expectedLines -Actual $actualLines)
    if ($diffLines.Count -eq 0) {
        Write-Host '  (differences are limited to line endings or the trailing newline)'
    } else {
        foreach ($line in $diffLines) {
            Write-Host $line
        }
    }

    exit 1
}

$targetDir = Split-Path -Parent $targetPath
if (-not (Test-Path -LiteralPath $targetDir -PathType Container)) {
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
}

[System.IO.File]::WriteAllText($targetPath, $content, $utf8NoBom)
Write-Host "wrote $targetPath"
$summary = 'curated entries: {0}; ACP registry version: {1}; skipped registry entries without a TUI adapter: {2}'
$summary = $summary -f $curated.Count, $registryVersion, $skippedIds.Count
Write-Host $summary
if ($skippedIds.Count -gt 0) {
    Write-Verbose ('skipped registry ids: ' + ($skippedIds -join ', '))
}
