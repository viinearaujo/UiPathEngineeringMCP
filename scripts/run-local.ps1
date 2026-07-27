#Requires -Version 5.1
<#
.SYNOPSIS
    Build, test and run the UiPath Engineering MCP server locally, then verify /health.

.DESCRIPTION
    Restores + builds the solution, (optionally) runs the xUnit test suite, starts the
    ASP.NET MCP server as a background job, polls the /health endpoint until it responds
    "Healthy", prints the local MCP endpoint URL, and then streams the server log until
    you press Ctrl+C (which stops the server cleanly).

.PARAMETER Url
    Base URL the server listens on. Must match appsettings.json ("Urls"). Default http://localhost:5000.

.PARAMETER SkipTests
    Skip 'dotnet test'.

.EXAMPLE
    ./scripts/run-local.ps1
    ./scripts/run-local.ps1 -SkipTests
#>
[CmdletBinding()]
param(
    [string]$Url = "http://localhost:5000",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

# Repo root is the parent of this /scripts folder.
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

# --- 0. Toolchain check ---------------------------------------------------
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The .NET SDK ('dotnet') was not found on PATH. Install .NET 8: https://dotnet.microsoft.com/download/dotnet/8.0"
}
Write-Step "dotnet $(dotnet --version)"

# --- 1. Restore / build / test -------------------------------------------
Write-Step "dotnet restore"
dotnet restore

Write-Step "dotnet build (Debug)"
dotnet build --configuration Debug --nologo

if (-not $SkipTests) {
    Write-Step "dotnet test"
    dotnet test --configuration Debug --nologo
}

# --- 2. Start the server as a background job ------------------------------
$ServerProject = Join-Path $RepoRoot "src/UiPath.Engineering.Mcp.Server"
Write-Step "starting MCP server -> $Url"

$job = Start-Job -ScriptBlock {
    param($proj)
    Set-Location $proj
    dotnet run --configuration Debug
} -ArgumentList $ServerProject

try {
    # --- 3. Poll /health ---------------------------------------------------
    $healthUrl = "$($Url.TrimEnd('/'))/health"
    Write-Step "waiting for $healthUrl"
    $healthy = $false
    for ($i = 1; $i -le 30; $i++) {
        Start-Sleep -Seconds 1
        try {
            $resp = Invoke-WebRequest -Uri $healthUrl -UseBasicParsing -TimeoutSec 3
            if ($resp.StatusCode -eq 200) {
                Write-Host "    Health: $($resp.StatusCode) $($resp.Content)" -ForegroundColor Green
                $healthy = $true
                break
            }
        }
        catch {
            # not up yet; keep polling
        }
    }

    if (-not $healthy) {
        Write-Host "Server did not become healthy in time. Recent output:" -ForegroundColor Yellow
        Receive-Job $job
        throw "Health check failed."
    }

    Write-Host ""
    Write-Host "MCP server is running." -ForegroundColor Green
    Write-Host "  Health endpoint : $healthUrl"
    Write-Host "  MCP endpoint    : $($Url.TrimEnd('/'))/sse   (Streamable HTTP)"
    Write-Host ""
    Write-Host "Next: expose it with  ./scripts/setup-devtunnel.ps1" -ForegroundColor Cyan
    Write-Host "Streaming server log (press Ctrl+C to stop)..." -ForegroundColor DarkGray
    Write-Host ""

    # --- 4. Stream logs until Ctrl+C --------------------------------------
    while ($job.State -eq 'Running') {
        Receive-Job $job
        Start-Sleep -Milliseconds 500
    }
    Receive-Job $job
}
finally {
    Write-Step "stopping server"
    Stop-Job $job -ErrorAction SilentlyContinue | Out-Null
    Remove-Job $job -Force -ErrorAction SilentlyContinue | Out-Null
}
