#requires -Version 7
<#
.SYNOPSIS
    Runs the .NET control plane (Veil.Api) and analytics worker
    (Veil.Analytics.Worker) together on a single host, correctly wired so the
    Phase 12 Tyto RPC (cross-process) and the in-memory message bus (per-process)
    work end to end.

.DESCRIPTION
    Veil's .NET side is two hosts, not one process:
      * Veil.Api               — control plane, RPC server (rules) + client (incidents)
      * Veil.Analytics.Worker  — ingest + AI analysis, RPC server (incidents) + client (rules)

    The two talk over Tyto RPC-over-HTTP, so each must listen on a known URL and
    point at the other. This script launches both with matching ports/config:

      Api    -> $ApiUrl     (RPC client reaches the worker at Intelligence:WorkerUrl)
      Worker -> $WorkerUrl  (RPC client reaches the control plane at Intelligence:ControlPlaneUrl)

    In-memory parts (config-sync events in Api, incident alerting in the worker)
    are process-local by design and need no wiring — they just work once each
    host is up.

    Infrastructure (Postgres/ClickHouse) is expected to be running already
    (podman: veil-pg, veil-ch). The script warns if it can't see them.

.PARAMETER IntelligenceEnabled
    Turns on the worker's AI analysis loop (default true) so the incident feed
    and alerting actually produce data. Set $false for a bare transport check.

.PARAMETER ControlPlaneApiKey
    API key the worker uses to authenticate its rule-application RPC to the
    control plane (X-Api-Key). Leave empty to keep the worker in log-only mode
    (no rules are pushed); the incident feed + alerting still work without it.

.EXAMPLE
    pwsh ./scripts/run-dev.ps1
.EXAMPLE
    pwsh ./scripts/run-dev.ps1 -ControlPlaneApiKey "vk_live_..." -AnthropicApiKey "sk-ant-..."
#>
[CmdletBinding()]
param(
    [string]$ApiUrl = 'http://localhost:5210',
    [string]$WorkerUrl = 'http://localhost:5001',
    [bool]$IntelligenceEnabled = $true,
    [string]$ControlPlaneApiKey = '',
    [string]$AnthropicApiKey = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$apiProj = Join-Path $repoRoot 'src/Apps/Veil.Api'
$workerProj = Join-Path $repoRoot 'src/Apps/Veil.Analytics.Worker'

# --- Infra sanity check (best-effort) -------------------------------------
try {
    $running = & podman ps --format '{{.Names}}' 2>$null
    foreach ($name in 'veil-pg', 'veil-ch') {
        if ($running -notcontains $name) {
            Write-Warning "Infra container '$name' is not running. Start it (podman) before relying on DB/ClickHouse."
        }
    }
}
catch {
    Write-Warning "Could not query podman ('$($_.Exception.Message)'). Make sure Postgres + ClickHouse are up."
}

# Build once, serially, BEFORE launching. Two concurrent `dotnet run` builds
# race on the shared project DLLs (Veil.Shared, *.Contracts) and collide on the
# file lock, so we pre-build and then run with --no-build.
Write-Host '==> building (serial, once)' -ForegroundColor Cyan
& dotnet build (Join-Path $apiProj 'Veil.Api.csproj') -v q --nologo
if ($LASTEXITCODE -ne 0) { throw 'Veil.Api build failed' }
& dotnet build (Join-Path $workerProj 'Veil.Analytics.Worker.csproj') -v q --nologo
if ($LASTEXITCODE -ne 0) { throw 'Veil.Analytics.Worker build failed' }

$procs = @()
function Start-Service([string]$name, [string]$project, [string]$profile, [string]$url, [hashtable]$env) {
    Write-Host "==> starting $name on $url" -ForegroundColor Cyan
    foreach ($k in $env.Keys) { Set-Item "env:$k" $env[$k] }
    # Keep the launch profile (carries VEIL_MASTER_KEY + Development env so the
    # appsettings.Development connection strings load) but override the port via
    # the app's --urls argument (highest-precedence config). --no-build: already
    # built above, and concurrent builds would race on shared DLLs.
    $p = Start-Process dotnet -PassThru -NoNewWindow -WorkingDirectory $project `
        -ArgumentList @('run', '--no-build', '--launch-profile', $profile, '--', '--urls', $url)
    return $p
}

try {
    # Worker first so it is listening when the Api's RPC client warms up.
    $procs += Start-Service 'Veil.Analytics.Worker' $workerProj 'Veil.Analytics.Worker' $WorkerUrl @{
        'Intelligence__Enabled'         = $IntelligenceEnabled.ToString().ToLower()
        'Intelligence__ControlPlaneUrl' = $ApiUrl
        'Intelligence__ControlPlaneApiKey' = $ControlPlaneApiKey
        'Intelligence__AnthropicApiKey' = $AnthropicApiKey
    }

    $procs += Start-Service 'Veil.Api' $apiProj 'http' $ApiUrl @{
        'Intelligence__WorkerUrl' = $WorkerUrl
    }

    Write-Host ''
    Write-Host "Both hosts launching. Api=$ApiUrl  Worker=$WorkerUrl" -ForegroundColor Green
    Write-Host "Incident feed (auth required): GET $ApiUrl/v1/intelligence/incidents" -ForegroundColor Green
    Write-Host 'Press Ctrl-C to stop both.' -ForegroundColor Green
    Write-Host ''

    Wait-Process -Id ($procs.Id)
}
finally {
    Write-Host ''
    Write-Host '==> stopping hosts' -ForegroundColor Yellow
    foreach ($p in $procs) {
        if ($p -and -not $p.HasExited) {
            try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch { }
        }
    }
}
