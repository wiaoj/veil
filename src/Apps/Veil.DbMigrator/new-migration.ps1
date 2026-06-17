#!/usr/bin/env pwsh
# Generates a new EF Core migration (design-time) for every module DbContext.
# This is the "create migration" half — applying them to the DB is what the
# Veil.DbMigrator console app does at runtime (dotnet run --project this folder).
#
# Usage:  ./new-migration.ps1 -Name AddSomething
#         ./new-migration.ps1 -Name Init -Module Auth   # single module only
param(
    [Parameter(Mandatory = $true)][string]$Name,
    [ValidateSet('Auth', 'Zones', 'Certificates', 'EdgeNodes', 'All')]
    [string]$Module = 'All'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path "$PSScriptRoot/../../.."
$startup = Join-Path $repoRoot 'src/Apps/Veil.Api'

# context name -> owning module project (relative to repo root)
$modules = [ordered]@{
    Security     = @{ Project = 'src/Veil.Infrastructure.Security';         Context = 'SecurityDbContext' }    
    Auth         = @{ Project = 'src/Veil.Auth';         Context = 'AuthDbContext' }
    Zones        = @{ Project = 'src/Veil.Zones';        Context = 'ZonesDbContext' }
    Certificates = @{ Project = 'src/Veil.Certificates'; Context = 'CertificatesDbContext' }
    EdgeNodes    = @{ Project = 'src/Veil.EdgeNodes';    Context = 'EdgeNodesDbContext' }
}

$targets = if ($Module -eq 'All') { $modules.Keys } else { @($Module) }

foreach ($key in $targets) {
    $m = $modules[$key]
    $project = Join-Path $repoRoot $m.Project
    Write-Host "==> $key : adding migration '$Name'" -ForegroundColor Cyan
    dotnet ef migrations add $Name `
        --project $project `
        --startup-project $startup `
        --context $m.Context `
        --output-dir 'Infrastructure/Persistence/Migrations'
    if ($LASTEXITCODE -ne 0) { throw "migration failed for $key" }
}

Write-Host "Done. Apply with: dotnet run --project $PSScriptRoot" -ForegroundColor Green
