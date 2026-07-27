#Requires -Version 5.1

<#
.SYNOPSIS
    Deletes Microsoft Dev Tunnels for the current logged-in devtunnel user.

.DESCRIPTION
    Deletes either:
      - One specific tunnel by name/id
      - All existing tunnels owned by the current user

.PARAMETER TunnelName
    Specific tunnel name/id to delete.

.PARAMETER All
    Delete all tunnels owned by the current user.

.EXAMPLE
    .\remove.ps1 -TunnelName uipath-mcp

.EXAMPLE
    .\remove.ps1 -All

.EXAMPLE
    .\remove.ps1 -All -WhatIf
#>

[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$TunnelName,

    [switch]$All
)

$ErrorActionPreference = "Stop"

function Write-Step($msg) {
    Write-Host "==> $msg" -ForegroundColor Cyan
}

# --- 0. Validate arguments ------------------------------------------------
if (-not $TunnelName -and -not $All) {
    throw "Specify either -TunnelName '<name-or-id>' or -All."
}

if ($TunnelName -and $All) {
    throw "Use either -TunnelName or -All, not both."
}

# --- 1. Ensure devtunnel CLI exists --------------------------------------
if (-not (Get-Command devtunnel -ErrorAction SilentlyContinue)) {
    throw "devtunnel CLI not found. Install it first or ensure it is available in PATH."
}

Write-Step "devtunnel $(devtunnel --version 2>$null)"

# --- 2. Ensure user is logged in -----------------------------------------
Write-Step "ensuring devtunnel login"

try {
    devtunnel user show 1>$null 2>$null

    if ($LASTEXITCODE -ne 0) {
        devtunnel user login
    }
}
catch {
    devtunnel user login
}

# --- 3. Delete specific tunnel -------------------------------------------
if ($TunnelName) {
    Write-Step "Deleting tunnel '$TunnelName'"

    if ($PSCmdlet.ShouldProcess($TunnelName, "Delete Dev Tunnel")) {
        devtunnel delete $TunnelName --force
    }

    Write-Host "Tunnel '$TunnelName' deleted." -ForegroundColor Green
    return
}

# --- 4. Delete all tunnels ------------------------------------------------
if ($All) {
    Write-Step "Deleting all dev tunnels for current user"

    if ($PSCmdlet.ShouldProcess("all dev tunnels", "Delete all Dev Tunnels")) {
        devtunnel delete-all --force
    }

    Write-Host "All dev tunnels deleted." -ForegroundColor Green
    return
}