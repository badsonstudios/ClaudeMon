# Publish a GitHub release for the current version, if one doesn't already exist.
#
#   - Version comes from src\ClaudeMon\ClaudeMon.csproj <Version> (single source of truth).
#   - Release notes are extracted from CHANGELOG.md for that version.
#   - Attaches the built installer dist\ClaudeMon-Setup-<version>.exe when present.
#   - No-op if a release tagged v<version> already exists.
#
# APPROVAL FIRST: this publishes a public GitHub release - confirm with the user.
#
# Usage: .\publish-release.ps1 [-Target <branch-or-sha>] [-Draft]
param(
    [string]$Target = "",
    [switch]$Draft
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$csproj = Join-Path $repoRoot 'src\ClaudeMon\ClaudeMon.csproj'
$changelog = Join-Path $repoRoot 'CHANGELOG.md'

if (-not (Test-Path $csproj)) { Write-Error "Cannot find $csproj"; exit 1 }

$csprojText = Get-Content $csproj -Raw
if ($csprojText -notmatch '<Version>([^<]+)</Version>') {
    Write-Error "Could not read <Version> from $csproj"; exit 1
}
$version = $Matches[1]
$tag = "v$version"

# Idempotent: skip if the release already exists.
& gh release view $tag *> $null
if ($LASTEXITCODE -eq 0) {
    Write-Host "Release $tag already exists - nothing to publish."
    exit 0
}

# Extract this version's notes from CHANGELOG.md.
$notesLines = @()
$inBlock = $false
foreach ($line in (Get-Content $changelog)) {
    if ($line.StartsWith("## [$version]")) { $inBlock = $true; continue }
    if ($inBlock -and $line.StartsWith("## ")) { break }
    if ($inBlock) { $notesLines += $line }
}
$notes = ($notesLines -join "`n").Trim()
if ([string]::IsNullOrWhiteSpace($notes)) { $notes = "Release $tag" }

$asset = Join-Path $repoRoot "dist\ClaudeMon-Setup-$version.exe"

$ghArgs = @('release', 'create', $tag, '--title', $tag, '--notes', $notes)
if ($Draft) { $ghArgs += '--draft' }
if ($Target) { $ghArgs += @('--target', $Target) }
if (Test-Path $asset) {
    $ghArgs += $asset
}
else {
    Write-Warning "Installer not found at $asset - publishing notes without an asset."
    Write-Warning "Run 'bash installer/build.sh' first to attach the installer."
}

Write-Host "Publishing GitHub release $tag..."
& gh @ghArgs
Write-Host "Done: $tag"
