#Requires -Version 5.1

<#
.SYNOPSIS
    Creates and hosts a Microsoft Dev Tunnel that exposes the local MCP server.

.DESCRIPTION
    Ensures the devtunnel CLI is installed, authenticates the user when required,
    creates the tunnel and port mapping only when they do not already exist,
    optionally enables anonymous access, displays tunnel details, and hosts the
    tunnel in the foreground.

    Run this script after the local MCP server is running with HTTP auth enabled
    (McpServer:HttpAuth:Enabled and a non-empty ApiKey). GET /health stays anonymous.
    Do not tunnel a Development host that still has auth disabled.

.PARAMETER TunnelName
    Dev Tunnel ID/name. Default: 'uipath-mcp'.

.PARAMETER Port
    Local port on which the MCP server listens. Default: 5000.

.PARAMETER Anonymous
    Enables anonymous access at the Dev Tunnel layer. This does not disable MCP
    HTTP auth. Do not use -Anonymous against a server with HttpAuth off.

.EXAMPLE
    .\scripts\setup-devtunnel.ps1

.EXAMPLE
    .\scripts\setup-devtunnel.ps1 `
        -TunnelName "uipath-mcp" `
        -Port 5000
#>

[CmdletBinding()]
param(
    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$TunnelName = "uipath-mcp",

    [Parameter()]
    [ValidateRange(1, 65535)]
    [int]$Port = 5000,

    [Parameter()]
    [switch]$Anonymous
)

$ErrorActionPreference = "Stop"

function Write-Step {
    param(
        [Parameter(Mandatory)]
        [string]$Message
    )

    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Invoke-DevTunnel {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [Parameter()]
        [switch]$SuppressOutput,

        [Parameter()]
        [switch]$IgnoreExitCode
    )

    # Windows PowerShell 5.1 may convert native stderr into a terminating
    # NativeCommandError when ErrorActionPreference is set to Stop.
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"

    try {
        if ($SuppressOutput) {
            & devtunnel @Arguments 1>$null 2>$null
        }
        else {
            & devtunnel @Arguments
        }

        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    if (($exitCode -ne 0) -and (-not $IgnoreExitCode)) {
        $command = "devtunnel " + ($Arguments -join " ")
        throw "Command failed with exit code ${exitCode}: $command"
    }

    return $exitCode
}

# -------------------------------------------------------------------------
# 0. Ensure the Dev Tunnel CLI is available
# -------------------------------------------------------------------------

if (-not (Get-Command "devtunnel" -ErrorAction SilentlyContinue)) {
    Write-Host(
        "devtunnel CLI not found. Attempting installation via winget..."
    ) -ForegroundColor Yellow

    if (-not (Get-Command "winget" -ErrorAction SilentlyContinue)) {
        throw @"
devtunnel is not installed and winget is unavailable.

Install the Dev Tunnel CLI manually:
https://learn.microsoft.com/azure/developer/dev-tunnels/get-started
"@
    }

    & winget install `
        --id "Microsoft.devtunnel" `
        --exact `
        --accept-source-agreements `
        --accept-package-agreements

    if ($LASTEXITCODE -ne 0) {
        throw "Failed to install the devtunnel CLI through winget."
    }

    # Refresh command discovery after installation.
    $devTunnelCommand = Get-Command "devtunnel" -ErrorAction SilentlyContinue

    if (-not $devTunnelCommand) {
        throw @"
The devtunnel CLI was installed, but it is not available in the current
PowerShell session.

Close and reopen PowerShell, then run this script again.
"@
    }
}

Write-Step "devtunnel CLI detected"
& devtunnel --version

if ($LASTEXITCODE -ne 0) {
    throw "Unable to execute the devtunnel CLI."
}

# -------------------------------------------------------------------------
# 1. Ensure the user is logged in
# -------------------------------------------------------------------------

Write-Step "checking devtunnel login"

$loginExitCode = Invoke-DevTunnel `
    -Arguments @("user", "show") `
    -SuppressOutput `
    -IgnoreExitCode

if ($loginExitCode -ne 0) {
    Write-Step "login required"

    Invoke-DevTunnel -Arguments @("user", "login")
}
else {
    Write-Step "already logged in"
}

# -------------------------------------------------------------------------
# 2. Create the tunnel when it does not exist
# -------------------------------------------------------------------------

Write-Step "checking tunnel '$TunnelName'"

$tunnelShowOutput = & devtunnel show $TunnelName 2>&1
$tunnelExists = ($LASTEXITCODE -eq 0)

if ($tunnelExists) {
    Write-Step "tunnel '$TunnelName' already exists"
}
else {
    Write-Step "creating tunnel '$TunnelName'"

    $createArguments = @(
        "create",
        $TunnelName
    )

    if ($Anonymous) {
        $createArguments += "--allow-anonymous"
    }

    Invoke-DevTunnel -Arguments $createArguments

    # Retrieve the tunnel again after creation.
    $tunnelShowOutput = & devtunnel show $TunnelName 2>&1

    if ($LASTEXITCODE -ne 0) {
        throw "Tunnel '$TunnelName' was created but could not be retrieved."
    }
}

# -------------------------------------------------------------------------
# 3. Ensure anonymous access when requested
# -------------------------------------------------------------------------

if ($Anonymous) {
    Write-Step "checking anonymous access"

    $tunnelDetailsText = $tunnelShowOutput | Out-String
    $anonymousAccessExists = (
        $tunnelDetailsText -match "\+Anonymous\s+\[connect\]"
    )

    if ($anonymousAccessExists) {
        Write-Step "anonymous access is already enabled"
    }
    else {
        Write-Step "enabling anonymous access"

        # Anonymous access applies to the tunnel, not to an individual port.
        Invoke-DevTunnel -Arguments @(
            "access",
            "create",
            $TunnelName,
            "-a"
        )
    }
}

# -------------------------------------------------------------------------
# 4. Ensure the local port is mapped
# -------------------------------------------------------------------------

Write-Step "checking port mapping for port $Port"

$portShowExitCode = Invoke-DevTunnel `
    -Arguments @(
        "port",
        "show",
        $TunnelName,
        "-p",
        $Port.ToString()
    ) `
    -SuppressOutput `
    -IgnoreExitCode

if ($portShowExitCode -eq 0) {
    Write-Step "port $Port is already mapped"
}
else {
    Write-Step "mapping port $Port using HTTP"

    Invoke-DevTunnel -Arguments @(
        "port",
        "create",
        $TunnelName,
        "-p",
        $Port.ToString(),
        "--protocol",
        "http"
    )
}

# -------------------------------------------------------------------------
# 5. Display the resulting tunnel configuration
# -------------------------------------------------------------------------

Write-Host ""
Write-Step "tunnel details"

Invoke-DevTunnel -Arguments @(
    "show",
    $TunnelName
)

Write-Host ""
Write-Step "port details"

Invoke-DevTunnel -Arguments @(
    "port",
    "show",
    $TunnelName,
    "-p",
    $Port.ToString()
)

Write-Host ""
Write-Host "Once hosting starts, devtunnel will display the public URL." `
    -ForegroundColor Green

Write-Host "Append the following paths to that URL:" `
    -ForegroundColor Green

Write-Host "  Health : /health  (always anonymous)" -ForegroundColor Green
Write-Host "  MCP    : /sse     (requires McpServer:HttpAuth API key outside Development)" -ForegroundColor Green
Write-Host ""

Write-Host @"
HTTP auth: set McpServer:HttpAuth:Enabled true and a non-empty ApiKey before
exposing this tunnel. Development may start with auth off for localhost only.
"@ -ForegroundColor Yellow

if ($Anonymous) {
    Write-Host @"
WARNING: Tunnel anonymous access is enabled. That only opens the Dev Tunnel
layer; it does not disable MCP auth. If HttpAuth is off, /sse is public.
"@ -ForegroundColor Yellow
}

if (-not $Anonymous) {
    Write-Host @"
NOTE: Tunnel-level anonymous access was not requested. Clients must authenticate
to the Dev Tunnel as well as send the MCP API key on /sse.
"@ -ForegroundColor Yellow
}

# -------------------------------------------------------------------------
# 6. Host the tunnel in the foreground
# -------------------------------------------------------------------------

Write-Host(
    "Hosting '$TunnelName'. Press Ctrl+C to stop."
) -ForegroundColor DarkGray

Write-Host ""

Invoke-DevTunnel -Arguments @(
    "host",
    $TunnelName
)