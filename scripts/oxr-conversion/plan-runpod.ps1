[CmdletBinding()]
param(
    [switch]$CheckAccount,
    [ValidateSet('Plan', 'Create', 'Get', 'Stop')][string]$Action = 'Plan',
    [string]$PodId,
    [string]$PublicKeyFile
)

$ErrorActionPreference = 'Stop'
if ($CheckAccount -or $Action -ne 'Plan') {
    $repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $dotenv = Join-Path $repository '.env'
    if (-not (Test-Path -LiteralPath $dotenv)) {
        throw 'RUNPOD_API_KEY is not available: the repository .env file is missing.'
    }

    $entry = [IO.File]::ReadAllLines($dotenv) |
        Where-Object { $_ -match '^\s*RUNPOD_API_KEY\s*=' } |
        Select-Object -Last 1
    if (-not $entry) {
        throw 'RUNPOD_API_KEY is not configured in .env.'
    }

    $key = ($entry -split '=', 2)[1].Trim().Trim('"', "'")
    if (-not $key) {
        throw 'RUNPOD_API_KEY is empty in .env.'
    }

    if ($CheckAccount) {
        try {
            $null = Invoke-RestMethod -Method Get -Uri 'https://api.runpod.io/v2/pods' `
                -Headers @{ Authorization = "Bearer $key" } -TimeoutSec 30
            Write-Output 'RunPod read-only API authentication succeeded.'
        }
        catch {
            $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 'unavailable' }
            throw "RunPod read-only API authentication failed (HTTP $status)."
        }
    }
}

if ($Action -in @('Get', 'Stop')) {
    if ($PodId -notmatch '^[a-z0-9]{8,32}$') {
        throw 'Specify a valid RunPod Pod ID.'
    }
    $endpoint = "https://api.runpod.io/v2/pods/$PodId"
    try {
        if ($Action -eq 'Stop') {
            $endpoint += '/action'
            $podState = Invoke-RestMethod -Method Post -Uri $endpoint `
                -Headers @{ Authorization = "Bearer $key" } -ContentType 'application/json' `
                -Body '{"action":"stop"}' -TimeoutSec 60
        }
        else {
            $podState = Invoke-RestMethod -Method Get -Uri $endpoint `
                -Headers @{ Authorization = "Bearer $key" } -TimeoutSec 30
        }
        $podState | Select-Object id, status, cost, cudaVersion,
            @{ Name = 'SshDirect'; Expression = { $_.ssh.direct } },
            @{ Name = 'TcpPorts'; Expression = { $_.runtime.ports | Where-Object { $_.type -eq 'tcp' } | Select-Object ip, public, private } }
        return
    }
    catch {
        $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 'unavailable' }
        throw "RunPod Pod $Action request failed (HTTP $status)."
    }
    finally {
        $key = $null
    }
}

$pod = [ordered]@{
    name          = 'oxr-onnx-export'
    image         = 'runpod/pytorch:1.4.0-cu1300-torch2120-ubuntu2404@sha256:93396b43c615b051e3951adbed2f958acff7d645936caa4029a58d0c1d309abd'
    cloud         = 'SECURE'
    gpu           = @{ id = 'NVIDIA A100-SXM4-80GB'; count = 1; minCudaVersion = '13.0' }
    disk          = 80
    mounts        = @{ persistent = @{ size = 100; path = '/workspace' } }
    ports         = @('22/tcp')
    startSsh      = $true
    env           = @{ HF_HOME = '/workspace/hf-cache'; HF_HUB_DISABLE_TELEMETRY = '1' }
}

if ($Action -eq 'Create') {
    if (-not (Test-Path -LiteralPath $PublicKeyFile -PathType Leaf)) {
        throw 'A local SSH public key is required to create the conversion Pod.'
    }
    $publicKey = [IO.File]::ReadAllText([IO.Path]::GetFullPath($PublicKeyFile)).Trim()
    if ($publicKey -notmatch '^ssh-ed25519 [A-Za-z0-9+/=]+(?:\s+.*)?$') {
        throw 'The conversion Pod requires an Ed25519 SSH public key.'
    }
    $pod.env.PUBLIC_KEY = $publicKey
    $pod.env.SSH_PUBLIC_KEY = $publicKey
    try {
        $existing = Invoke-RestMethod -Method Get -Uri 'https://api.runpod.io/v2/pods' `
            -Headers @{ Authorization = "Bearer $key" } -TimeoutSec 30
        if (@($existing.pods | Where-Object { $_.name -eq $pod.name }).Count -ne 0) {
            throw 'A conversion Pod with this name already exists; inspect it before creating another.'
        }
        $created = Invoke-RestMethod -Method Post -Uri 'https://api.runpod.io/v2/pods' `
            -Headers @{ Authorization = "Bearer $key" } -ContentType 'application/json' `
            -Body ($pod | ConvertTo-Json -Depth 6) -TimeoutSec 90
        $created | Select-Object id, status, cost, cudaVersion
        return
    }
    catch {
        $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 'unavailable' }
        throw "RunPod Pod creation failed (HTTP $status)."
    }
    finally {
        $key = $null
    }
}

Write-Output 'RunPod Pod request plan (dry-run only, no resource was created):'
Write-Output 'POST https://api.runpod.io/v2/pods'
Write-Output ($pod | ConvertTo-Json -Depth 6)
