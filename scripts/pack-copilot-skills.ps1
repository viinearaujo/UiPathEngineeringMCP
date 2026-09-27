#Requires -Version 5.1

<#
.SYNOPSIS
    Packs the Copilot Studio skills into upload-ready zip files.

.DESCRIPTION
    Every folder under docs/copilot-studio-skills that holds a SKILL.md becomes
    <OutputDir>/<folder>.zip with SKILL.md at the zip root. Files listed for a skill
    in docs/copilot-studio-skills/pack.json are copied into the zip at their "to"
    path, so shared sources such as docs/authoring/activity-spec.md stay single-copy
    in the repository.

    Upload each zip in Copilot Studio: Build > Skills > Upload a skill, or Replace on
    the existing skill.

.PARAMETER OutputDir
    Folder for the zips. Relative paths resolve against the repository root.
    Default: 'artifacts/copilot-skills' (gitignored).

.EXAMPLE
    .\scripts\pack-copilot-skills.ps1
#>

[CmdletBinding()]
param(
    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$OutputDir = "artifacts/copilot-skills"
)

$ErrorActionPreference = "Stop"

# Compress-Archive on Windows PowerShell 5.1 writes nested entries with '\' separators,
# which zip readers outside Windows reject. Build the archive directly with '/' names.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Write-Step {
    param(
        [Parameter(Mandatory)]
        [string]$Message
    )

    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Resolve-RepoPath {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $repoRoot $Path))
}

function Get-EntryName {
    param(
        [Parameter(Mandatory)]
        [string]$SkillName,

        [Parameter(Mandatory)]
        [string]$Path
    )

    $entry = $Path.Replace('\', '/').Trim()
    if ([string]::IsNullOrWhiteSpace($entry) -or $entry.StartsWith('/') -or ($entry -match '(^|/)\.\.(/|$)')) {
        throw "pack.json: '$Path' for skill '$SkillName' must be a relative path inside the zip."
    }

    return $entry
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$skillsRoot = Join-Path $repoRoot "docs/copilot-studio-skills"
$manifestPath = Join-Path $skillsRoot "pack.json"
$outputRoot = Resolve-RepoPath $OutputDir

if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Pack manifest not found: $manifestPath"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

$includesBySkill = @{}
foreach ($skill in @($manifest.skills)) {
    if ([string]::IsNullOrWhiteSpace($skill.name)) {
        throw "pack.json: every skill entry needs a name."
    }

    if (-not (Test-Path -LiteralPath (Join-Path (Join-Path $skillsRoot $skill.name) "SKILL.md") -PathType Leaf)) {
        throw "pack.json: skill '$($skill.name)' has no docs/copilot-studio-skills/$($skill.name)/SKILL.md."
    }

    $includesBySkill[$skill.name] = @($skill.include)
}

$skillDirs = @(Get-ChildItem -LiteralPath $skillsRoot -Directory |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "SKILL.md") -PathType Leaf } |
    Sort-Object Name)

if ($skillDirs.Count -eq 0) {
    throw "No skill folders with a SKILL.md under $skillsRoot."
}

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

foreach ($skillDir in $skillDirs) {
    $skillName = $skillDir.Name
    $zipPath = Join-Path $outputRoot "$skillName.zip"
    Write-Step "packing $skillName"

    $entries = [ordered]@{}
    foreach ($file in @(Get-ChildItem -LiteralPath $skillDir.FullName -File -Recurse | Sort-Object FullName)) {
        $relative = $file.FullName.Substring($skillDir.FullName.Length).TrimStart('\', '/')
        $entries[(Get-EntryName -SkillName $skillName -Path $relative)] = $file.FullName
    }

    foreach ($include in @($includesBySkill[$skillName])) {
        if ($null -eq $include) {
            continue
        }

        $source = Resolve-RepoPath $include.from
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "pack.json: '$($include.from)' for skill '$skillName' does not exist."
        }

        $entry = Get-EntryName -SkillName $skillName -Path $include.to
        if ($entries.Contains($entry)) {
            throw "pack.json: '$entry' is already in the '$skillName' zip."
        }

        $entries[$entry] = $source
    }

    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    $archive = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entry in $entries.Keys) {
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive,
                $entries[$entry],
                $entry,
                [System.IO.Compression.CompressionLevel]::Optimal)
            Write-Host "    $entry"
        }
    }
    finally {
        $archive.Dispose()
    }
}

Write-Host ""
Write-Host "Zips written to $outputRoot" -ForegroundColor Green
Write-Host "Upload each zip in Copilot Studio (Build > Skills > Upload a skill, or Replace on the existing skill)." -ForegroundColor Green
