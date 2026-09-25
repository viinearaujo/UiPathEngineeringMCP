#Requires -Version 5.1
<#
.SYNOPSIS
    Nightly smoke against a real `uip` CLI (self-hosted runner).

.DESCRIPTION
    Runs `uip --version`. When SMOKE_PROJECT_PATH is set and exists, runs
    `uip rpa validate --project-dir <path> --output json` and exits with uip's
    exit code. Missing `uip` or unset/missing SMOKE_PROJECT_PATH exits 0 with a
    SKIP line. Not invoked from pull_request CI.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

function Write-Skip([string]$Reason) {
    Write-Host "SKIP: $Reason"
    exit 0
}

$uip = Get-Command uip -ErrorAction SilentlyContinue
if (-not $uip) {
    Write-Skip "uip not on PATH"
}

Write-Host "==> uip --version"
& uip --version
if ($LASTEXITCODE -ne 0) {
    Write-Error "uip --version failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

$project = $env:SMOKE_PROJECT_PATH
if ([string]::IsNullOrWhiteSpace($project) -or -not (Test-Path -LiteralPath $project)) {
    Write-Skip "SMOKE_PROJECT_PATH unset or path does not exist"
}

Write-Host "==> uip rpa validate --project-dir $project"
& uip rpa validate --project-dir $project --output json
exit $LASTEXITCODE
